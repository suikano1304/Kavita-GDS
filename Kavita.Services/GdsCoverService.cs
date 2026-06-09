using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Helpers;
using Kavita.API.Services.SignalR;
using Kavita.Common;
using Kavita.Common.Extensions;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Entities.Interfaces;
using Kavita.Models.Extensions;
using Kavita.Services.Comparators;
using Kavita.Services.Extensions;
using Kavita.Services.Helpers;
using Microsoft.Extensions.Logging;

namespace Kavita.Services;

public sealed class GdsCoverService(
    IUnitOfWork unitOfWork,
    ILogger<GdsCoverService> logger,
    IEventHub eventHub,
    ICacheHelper cacheHelper,
    IReadingItemService readingItemService,
    IDirectoryService directoryService,
    IImageService imageService)
    : IGdsCoverService
{
    private readonly IList<SignalRMessage> _updateEvents = new List<SignalRMessage>();

    public async Task<GdsCoverGenerationResult> ProcessSeriesCoverGen(Series series, bool forceUpdate,
        EncodeFormat encodeFormat, CoverImageSize coverImageSize, bool forceColorScape = false)
    {
        _updateEvents.Clear();

        var preserveSeriesCover = TryApplyGdsFolderCover(series, forceUpdate, forceColorScape);
        series.Volumes ??= [];

        var firstVolume = series.Volumes.MinBy(volume => volume.MinNumber);
        if (firstVolume == null) return Result(false);

        firstVolume.Chapters ??= [];
        var firstChapter = firstVolume.Chapters.FirstOrDefault(chapter => chapter.MinNumber.Is(1f)) ??
                           firstVolume.Chapters.MinBy(chapter => chapter.SortOrder, ChapterSortComparerDefaultFirst.Default);
        if (firstChapter == null) return Result(false);

        var firstFile = firstChapter.Files.MinBy(x => x.Chapter);
        if (firstFile?.Format == MangaFormat.Text)
        {
            return Result(await ProcessGdsTextSeriesCoverGen(series, firstChapter, forceUpdate, encodeFormat,
                coverImageSize, forceColorScape, preserveSeriesCover));
        }

        var totalVolumes = series.Volumes.Count;
        var volumeIndex = 0;
        var firstVolumeUpdated = false;
        foreach (var volume in series.Volumes)
        {
            volume.Chapters ??= [];

            var firstChapterUpdated = false;
            var chapterIndex = 0;
            foreach (var chapter in volume.Chapters)
            {
                var forceMediaCoverUpdate = forceUpdate && string.IsNullOrEmpty(chapter.CoverImage);
                var chapterUpdated = UpdateGdsChapterCoverFromYaml(chapter, forceUpdate, encodeFormat,
                    coverImageSize, forceColorScape);
                if (!chapterUpdated)
                {
                    chapterUpdated = UpdateGdsChapterCoverFromMediaFiles(chapter, forceMediaCoverUpdate,
                        encodeFormat, coverImageSize, forceColorScape);
                }

                UpdateChapterLastModified(chapter, forceUpdate || chapterUpdated);
                if (chapterIndex == 0 && chapterUpdated)
                {
                    firstChapterUpdated = true;
                }

                chapterIndex++;
            }

            var volumeUpdated = UpdateVolumeCoverImage(volume, firstChapterUpdated || forceUpdate, forceColorScape);
            if (volumeIndex == 0 && volumeUpdated)
            {
                firstVolumeUpdated = true;
            }

            await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.CoverUpdateProgressEvent(series.LibraryId, volumeIndex / (float) totalVolumes,
                    ProgressEventType.Started, series.Name));

            volumeIndex++;
        }

        if (!preserveSeriesCover)
        {
            UpdateSeriesCoverImage(series, firstVolumeUpdated || forceUpdate, forceColorScape);
        }

        return Result(true);
    }

    private async Task<bool> ProcessGdsTextSeriesCoverGen(Series series, Chapter firstChapter, bool forceUpdate,
        EncodeFormat encodeFormat, CoverImageSize coverImageSize, bool forceColorScape, bool preserveSeriesCover)
    {
        var totalVolumes = series.Volumes.Count;
        var volumeIndex = 0;
        var firstVolumeUpdated = false;
        var anyYamlCoverUpdated = false;

        foreach (var volume in series.Volumes)
        {
            volume.Chapters ??= [];

            var firstChapterUpdated = false;
            var chapterIndex = 0;
            foreach (var chapter in volume.Chapters)
            {
                var chapterUpdated = UpdateGdsChapterCoverFromYaml(chapter, forceUpdate, encodeFormat,
                    coverImageSize, forceColorScape);

                UpdateChapterLastModified(chapter, forceUpdate || chapterUpdated);
                if (chapterUpdated)
                {
                    anyYamlCoverUpdated = true;
                }

                if (chapterIndex == 0 && chapterUpdated)
                {
                    firstChapterUpdated = true;
                }

                chapterIndex++;
            }

            var volumeUpdated = UpdateVolumeCoverImage(volume, firstChapterUpdated || forceUpdate, forceColorScape);
            if (volumeIndex == 0 && volumeUpdated)
            {
                firstVolumeUpdated = true;
            }

            await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.CoverUpdateProgressEvent(series.LibraryId, volumeIndex / (float) totalVolumes,
                    ProgressEventType.Started, series.Name));

            volumeIndex++;
        }

        if (anyYamlCoverUpdated)
        {
            if (!preserveSeriesCover)
            {
                UpdateSeriesCoverImage(series, firstVolumeUpdated || forceUpdate, forceColorScape);
            }

            return true;
        }

        return TryApplyGdsTextTitleCover(series, firstChapter, forceUpdate, encodeFormat, coverImageSize,
            forceColorScape, preserveSeriesCover);
    }

    private GdsCoverGenerationResult Result(bool handled)
    {
        return new GdsCoverGenerationResult(handled, _updateEvents.ToArray());
    }

    private void UpdateChapterLastModified(Chapter chapter, bool forceUpdate)
    {
        var firstFile = chapter.Files.MinBy(x => x.Chapter);
        if (firstFile == null || cacheHelper.IsFileUnmodifiedSinceCreationOrLastScan(chapter, forceUpdate, firstFile)) return;

        firstFile.UpdateLastModified();
    }

    private static bool NeedsColorSpace(IHasCoverImage? entity, bool force)
    {
        if (entity == null) return false;
        if (force) return true;

        return !string.IsNullOrEmpty(entity.CoverImage) &&
               (string.IsNullOrEmpty(entity.PrimaryColor) || string.IsNullOrEmpty(entity.SecondaryColor));
    }

    private bool UpdateVolumeCoverImage(Volume? volume, bool forceUpdate, bool forceColorScape = false)
    {
        if (volume == null) return false;

        if (!cacheHelper.ShouldUpdateCoverImage(
                directoryService.FileSystem.Path.Join(directoryService.CoverImageDirectory, volume.CoverImage),
                null, volume.Created, forceUpdate))
        {
            if (NeedsColorSpace(volume, forceColorScape))
            {
                imageService.UpdateColorScape(volume);
                unitOfWork.VolumeRepository.Update(volume);
                _updateEvents.Add(MessageFactory.CoverUpdateEvent(volume.Id, MessageFactoryEntityTypes.Volume));
            }
            return false;
        }

        if (!volume.CoverImageLocked)
        {
            volume.Chapters ??= new List<Chapter>();

            var firstChapter = volume.Chapters.FirstOrDefault(x => x.MinNumber.Is(1f));
            if (firstChapter == null)
            {
                firstChapter = volume.Chapters.MinBy(x => x.SortOrder, ChapterSortComparerDefaultFirst.Default);
                if (firstChapter == null) return false;
            }

            volume.CoverImage = firstChapter.CoverImage;
        }
        imageService.UpdateColorScape(volume);
        unitOfWork.VolumeRepository.Update(volume);

        _updateEvents.Add(MessageFactory.CoverUpdateEvent(volume.Id, MessageFactoryEntityTypes.Volume));

        return true;
    }

    private void UpdateSeriesCoverImage(Series? series, bool forceUpdate, bool forceColorScape = false)
    {
        if (series == null) return;

        if (!cacheHelper.ShouldUpdateCoverImage(
                directoryService.FileSystem.Path.Join(directoryService.CoverImageDirectory, series.CoverImage),
                null, series.Created, forceUpdate, series.CoverImageLocked))
        {
            if (NeedsColorSpace(series, forceColorScape))
            {
                imageService.UpdateColorScape(series);
                _updateEvents.Add(MessageFactory.CoverUpdateEvent(series.Id, MessageFactoryEntityTypes.Series));
            }

            return;
        }

        series.Volumes ??= [];
        series.CoverImage = series.GetCoverImage();
        if (series.CoverImage == null)
        {
            logger.LogDebug("[SeriesCoverImageBug] Setting Series Cover Image to null: {SeriesId}", series.Id);
        }

        imageService.UpdateColorScape(series);
        unitOfWork.SeriesRepository.Update(series);

        _updateEvents.Add(MessageFactory.CoverUpdateEvent(series.Id, MessageFactoryEntityTypes.Series));
    }

    private bool UpdateGdsChapterCoverFromYaml(Chapter chapter, bool forceUpdate, EncodeFormat encodeFormat,
        CoverImageSize coverImageSize, bool forceColorScape)
    {
        var firstFile = chapter.Files.MinBy(x => x.Chapter);
        if (firstFile == null) return false;

        if (!cacheHelper.ShouldUpdateCoverImage(
                directoryService.FileSystem.Path.Join(directoryService.CoverImageDirectory, chapter.CoverImage),
                firstFile, chapter.Created, forceUpdate, chapter.CoverImageLocked))
        {
            return false;
        }

        foreach (var file in chapter.Files.Where(x => x.Bytes > 0).OrderBy(x => x.Chapter).ThenBy(x => x.FilePath))
        {
            if (!GdsMetadataParser.TryGetCoverBase64(file.FilePath, out var encodedImage)) continue;

            var thumbnailWidth = coverImageSize.GetDimensions().Width;
            string coverImage;
            try
            {
                coverImage = imageService.CreateThumbnailFromBase64(encodedImage,
                    ImageService.GetChapterFormat(chapter.Id, chapter.VolumeId), encodeFormat, thumbnailWidth);
            }
            catch (KavitaException ex)
            {
                logger.LogWarning(ex, "[GdsCoverService] Invalid GDS YAML cover for {File}", file.FilePath);
                continue;
            }

            if (string.IsNullOrEmpty(coverImage)) continue;

            chapter.CoverImage = coverImage;
            imageService.UpdateColorScape(chapter);
            unitOfWork.ChapterRepository.Update(chapter);
            _updateEvents.Add(MessageFactory.CoverUpdateEvent(chapter.Id, MessageFactoryEntityTypes.Chapter));

            return true;
        }

        return false;
    }

    private bool UpdateGdsChapterCoverFromMediaFiles(Chapter chapter, bool forceUpdate, EncodeFormat encodeFormat,
        CoverImageSize coverImageSize, bool forceColorScape)
    {
        var firstFile = chapter.Files.MinBy(x => x.Chapter);
        if (firstFile == null) return false;

        if (!cacheHelper.ShouldUpdateCoverImage(
                directoryService.FileSystem.Path.Join(directoryService.CoverImageDirectory, chapter.CoverImage),
                firstFile, chapter.Created, forceUpdate, chapter.CoverImageLocked))
        {
            if (NeedsColorSpace(chapter, forceColorScape))
            {
                imageService.UpdateColorScape(chapter);
                unitOfWork.ChapterRepository.Update(chapter);
                _updateEvents.Add(MessageFactory.CoverUpdateEvent(chapter.Id, MessageFactoryEntityTypes.Chapter));
            }

            return false;
        }

        foreach (var file in chapter.Files.Where(x => x.Bytes > 0).OrderBy(x => x.Chapter).ThenBy(x => x.FilePath))
        {
            logger.LogDebug("[GdsCoverService] Generating GDS cover image for {File}", file.FilePath);

            string coverImage;
            try
            {
                coverImage = readingItemService.GetCoverImage(file.FilePath,
                    ImageService.GetChapterFormat(chapter.Id, chapter.VolumeId), file.Format, encodeFormat, coverImageSize);
            }
            catch (Exception ex) when (ex is KavitaException or InvalidDataException or IOException or InvalidOperationException)
            {
                logger.LogWarning(ex, "[GdsCoverService] Failed to generate GDS cover image for {File}", file.FilePath);
                continue;
            }

            if (string.IsNullOrEmpty(coverImage)) continue;

            chapter.CoverImage = coverImage;
            imageService.UpdateColorScape(chapter);
            unitOfWork.ChapterRepository.Update(chapter);
            _updateEvents.Add(MessageFactory.CoverUpdateEvent(chapter.Id, MessageFactoryEntityTypes.Chapter));

            return true;
        }

        return false;
    }

    private bool TryApplyGdsTextTitleCover(Series series, Chapter firstChapter, bool forceUpdate,
        EncodeFormat encodeFormat, CoverImageSize coverImageSize, bool forceColorScape, bool preserveSeriesCover)
    {
        var firstFile = firstChapter.Files.MinBy(x => x.Chapter);
        if (firstFile?.Format != MangaFormat.Text) return false;
        if (series.CoverImageLocked && !forceUpdate && !preserveSeriesCover) return false;

        if (!preserveSeriesCover && !forceUpdate && !string.IsNullOrWhiteSpace(series.CoverImage) &&
            File.Exists(Path.Join(directoryService.CoverImageDirectory, series.CoverImage)))
        {
            if (NeedsColorSpace(series, forceColorScape))
            {
                imageService.UpdateColorScape(series);
                unitOfWork.SeriesRepository.Update(series);
                _updateEvents.Add(MessageFactory.CoverUpdateEvent(series.Id, MessageFactoryEntityTypes.Series));
            }

            return true;
        }

        var coverImageNameWithoutExtension = ImageService.GetSeriesFormat(series.Id);
        var newCoverImage = coverImageNameWithoutExtension + encodeFormat.GetExtension();
        var configCoverFilePath = Path.Join(directoryService.CoverImageDirectory, newCoverImage);
        if (forceUpdate || !File.Exists(configCoverFilePath))
        {
            var title = string.IsNullOrWhiteSpace(series.Name) ? series.OriginalName : series.Name;
            var generatedCover = imageService.CreateTitleCover(title, "TEXT", coverImageNameWithoutExtension, encodeFormat, coverImageSize);
            if (string.IsNullOrEmpty(generatedCover)) return false;
            newCoverImage = generatedCover;
            configCoverFilePath = Path.Join(directoryService.CoverImageDirectory, newCoverImage);
        }

        if (!File.Exists(configCoverFilePath)) return false;

        if (!preserveSeriesCover)
        {
            var shouldUpdateSeriesColor = series.CoverImage != newCoverImage || NeedsColorSpace(series, forceColorScape);
            series.CoverImage = newCoverImage;
            if (shouldUpdateSeriesColor)
            {
                imageService.UpdateColorScape(series);
            }
            unitOfWork.SeriesRepository.Update(series);
            _updateEvents.Add(MessageFactory.CoverUpdateEvent(series.Id, MessageFactoryEntityTypes.Series));
        }

        foreach (var volume in series.Volumes)
        {
            volume.Chapters ??= [];

            if (!volume.CoverImageLocked)
            {
                var shouldUpdateVolumeColor = volume.CoverImage != newCoverImage || NeedsColorSpace(volume, forceColorScape);
                volume.CoverImage = newCoverImage;
                if (shouldUpdateVolumeColor)
                {
                    imageService.UpdateColorScape(volume);
                }
                unitOfWork.VolumeRepository.Update(volume);
                _updateEvents.Add(MessageFactory.CoverUpdateEvent(volume.Id, MessageFactoryEntityTypes.Volume));
            }

            foreach (var chapter in volume.Chapters)
            {
                if (chapter.CoverImageLocked) continue;
                var shouldUpdateChapterColor = chapter.CoverImage != newCoverImage || NeedsColorSpace(chapter, forceColorScape);
                chapter.CoverImage = newCoverImage;
                if (shouldUpdateChapterColor)
                {
                    imageService.UpdateColorScape(chapter);
                }
                unitOfWork.ChapterRepository.Update(chapter);
                _updateEvents.Add(MessageFactory.CoverUpdateEvent(chapter.Id, MessageFactoryEntityTypes.Chapter));
            }
        }

        return true;
    }

    private bool TryApplyGdsFolderCover(Series series, bool forceUpdate, bool forceColorScape)
    {
        if (string.IsNullOrWhiteSpace(series.FolderPath)) return false;

        var coverFilePath = Path.Join(series.FolderPath, "cover.jpg");
        var newCoverImage = "_s" + series.Id + ".jpg";
        if (!File.Exists(coverFilePath))
        {
            coverFilePath = Path.Join(series.FolderPath, "cover.png");
            newCoverImage = "_s" + series.Id + ".png";
        }
        if (!File.Exists(coverFilePath))
        {
            coverFilePath = Path.Join(series.FolderPath, "cover.webp");
            newCoverImage = "_s" + series.Id + ".webp";
        }

        var configCoverFilePath = Path.Join(directoryService.CoverImageDirectory, newCoverImage);
        if (!File.Exists(coverFilePath))
        {
            return false;
        }

        var shouldCopy = forceUpdate || !File.Exists(configCoverFilePath) ||
                         new FileInfo(coverFilePath).Length != new FileInfo(configCoverFilePath).Length;

        var seriesAlreadyUsesCover = series.CoverImage == newCoverImage;
        var needsColorScape = NeedsColorSpace(series, forceColorScape);
        if (!shouldCopy && seriesAlreadyUsesCover && !needsColorScape)
        {
            return true;
        }

        if (shouldCopy)
        {
            File.Copy(coverFilePath, configCoverFilePath, true);
        }

        if (!File.Exists(configCoverFilePath)) return false;

        var shouldUpdateSeriesColor = shouldCopy || series.CoverImage != newCoverImage ||
                                      NeedsColorSpace(series, forceColorScape);
        series.CoverImage = newCoverImage;
        if (shouldUpdateSeriesColor)
        {
            imageService.UpdateColorScape(series);
        }
        unitOfWork.SeriesRepository.Update(series);
        _updateEvents.Add(MessageFactory.CoverUpdateEvent(series.Id, MessageFactoryEntityTypes.Series));

        return true;
    }
}
