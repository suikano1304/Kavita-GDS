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
    private enum GdsCoverPriority
    {
        None = 0,
        TextTitle = 1,
        Media = 2,
        Yaml = 3,
    }

    private sealed record GdsChapterCoverResult(bool Updated, GdsCoverPriority Priority, string? CoverImage);

    public async Task<GdsCoverGenerationResult> ProcessSeriesCoverGen(Series series, bool forceUpdate,
        EncodeFormat encodeFormat, CoverImageSize coverImageSize, bool forceColorScape = false)
    {
        _updateEvents.Clear();

        var preserveSeriesCover = TryApplyGdsFolderCover(series, forceUpdate, forceColorScape);
        series.Volumes ??= [];

        var totalVolumes = series.Volumes.Count;
        if (totalVolumes == 0) return Result(false);

        var volumeIndex = 0;
        var seriesCoverCandidate = new GdsChapterCoverResult(false, GdsCoverPriority.None, null);
        foreach (var volume in series.Volumes)
        {
            volume.Chapters ??= [];

            var volumeCoverCandidate = new GdsChapterCoverResult(false, GdsCoverPriority.None, null);
            foreach (var chapter in volume.Chapters)
            {
                var chapterCoverResult = UpdateGdsChapterCover(series, chapter, forceUpdate, encodeFormat,
                    coverImageSize, forceColorScape);

                UpdateChapterLastModified(chapter, forceUpdate || chapterCoverResult.Updated);
                volumeCoverCandidate = BestCover(volumeCoverCandidate, chapterCoverResult);
                seriesCoverCandidate = BestCover(seriesCoverCandidate, chapterCoverResult);
            }

            if (!string.IsNullOrEmpty(volumeCoverCandidate.CoverImage))
            {
                UpdateVolumeCoverImage(volume, volumeCoverCandidate.CoverImage,
                    volumeCoverCandidate.Updated || forceUpdate, forceColorScape);
            }

            await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.CoverUpdateProgressEvent(series.LibraryId, volumeIndex / (float) totalVolumes,
                    ProgressEventType.Started, series.Name));

            volumeIndex++;
        }

        if (!preserveSeriesCover)
        {
            UpdateSeriesCoverImage(series, seriesCoverCandidate.CoverImage,
                seriesCoverCandidate.Updated || forceUpdate, forceColorScape);
        }

        return Result(true);
    }

    public Task<GdsCoverGenerationResult> ProcessSeriesRepresentativeCoverGen(Series series, bool forceUpdate,
        EncodeFormat encodeFormat, CoverImageSize coverImageSize, bool forceColorScape = false)
    {
        _updateEvents.Clear();

        if (TryApplyGdsFolderCover(series, forceUpdate, forceColorScape))
        {
            return Task.FromResult(Result(true));
        }

        series.Volumes ??= [];
        foreach (var volume in series.Volumes)
        {
            volume.Chapters ??= [];
            foreach (var chapter in volume.Chapters)
            {
                var chapterCoverResult = UpdateGdsChapterCover(series, chapter, forceUpdate, encodeFormat,
                    coverImageSize, forceColorScape);

                UpdateChapterLastModified(chapter, forceUpdate || chapterCoverResult.Updated);
                if (string.IsNullOrEmpty(chapterCoverResult.CoverImage)) continue;

                UpdateVolumeCoverImage(volume, chapterCoverResult.CoverImage,
                    chapterCoverResult.Updated || forceUpdate, forceColorScape);
                UpdateSeriesCoverImage(series, chapterCoverResult.CoverImage,
                    chapterCoverResult.Updated || forceUpdate, forceColorScape);

                return Task.FromResult(Result(true));
            }
        }

        return Task.FromResult(Result(true));
    }

    private static GdsChapterCoverResult BestCover(GdsChapterCoverResult current, GdsChapterCoverResult candidate)
    {
        if (candidate.Priority > current.Priority) return candidate;
        if (candidate.Priority == current.Priority && current.CoverImage == null && candidate.CoverImage != null) return candidate;
        return current;
    }

    private GdsChapterCoverResult UpdateGdsChapterCover(Series series, Chapter chapter, bool forceUpdate,
        EncodeFormat encodeFormat, CoverImageSize coverImageSize, bool forceColorScape)
    {
        var yamlUpdated = UpdateGdsChapterCoverFromYaml(chapter, forceUpdate, encodeFormat, coverImageSize);
        if (yamlUpdated)
        {
            return new GdsChapterCoverResult(true, GdsCoverPriority.Yaml, chapter.CoverImage);
        }

        if (HasMediaCoverSource(chapter))
        {
            var mediaUpdated = UpdateGdsChapterCoverFromMediaFiles(chapter, forceUpdate,
                encodeFormat, coverImageSize, forceColorScape);
            if (mediaUpdated || !string.IsNullOrEmpty(chapter.CoverImage))
            {
                return new GdsChapterCoverResult(mediaUpdated, GdsCoverPriority.Media, chapter.CoverImage);
            }
        }

        if (HasTextCoverSource(chapter))
        {
            var textUpdated = TryApplyGdsTextTitleCover(series, chapter, forceUpdate, encodeFormat,
                coverImageSize, forceColorScape);
            if (textUpdated || !string.IsNullOrEmpty(chapter.CoverImage))
            {
                return new GdsChapterCoverResult(textUpdated, GdsCoverPriority.TextTitle, chapter.CoverImage);
            }
        }

        return new GdsChapterCoverResult(false, GdsCoverPriority.None, chapter.CoverImage);
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

    private bool UpdateVolumeCoverImage(Volume? volume, string? coverImage, bool forceUpdate, bool forceColorScape = false)
    {
        if (volume == null || string.IsNullOrEmpty(coverImage)) return false;

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
            volume.CoverImage = coverImage;
        }
        imageService.UpdateColorScape(volume);
        unitOfWork.VolumeRepository.Update(volume);

        _updateEvents.Add(MessageFactory.CoverUpdateEvent(volume.Id, MessageFactoryEntityTypes.Volume));

        return true;
    }

    private void UpdateSeriesCoverImage(Series? series, string? coverImage, bool forceUpdate, bool forceColorScape = false)
    {
        if (series == null || string.IsNullOrEmpty(coverImage)) return;

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
        series.CoverImage = coverImage;
        if (series.CoverImage == null)
        {
            logger.LogDebug("[SeriesCoverImageBug] Setting Series Cover Image to null: {SeriesId}", series.Id);
        }

        imageService.UpdateColorScape(series);
        unitOfWork.SeriesRepository.Update(series);

        _updateEvents.Add(MessageFactory.CoverUpdateEvent(series.Id, MessageFactoryEntityTypes.Series));
    }

    private bool UpdateGdsChapterCoverFromYaml(Chapter chapter, bool forceUpdate, EncodeFormat encodeFormat,
        CoverImageSize coverImageSize)
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

    private static bool HasMediaCoverSource(Chapter chapter)
    {
        return chapter.Files?.Any(file => file.Bytes > 0 && file.Format != MangaFormat.Text) == true;
    }

    private static bool HasTextCoverSource(Chapter chapter)
    {
        return chapter.Files?.Any(file => file.Bytes > 0 && file.Format == MangaFormat.Text) == true;
    }

    private bool TryApplyGdsTextTitleCover(Series series, Chapter chapter, bool forceUpdate,
        EncodeFormat encodeFormat, CoverImageSize coverImageSize, bool forceColorScape)
    {
        var firstFile = chapter.Files.MinBy(x => x.Chapter);
        if (firstFile?.Format != MangaFormat.Text) return false;
        if (chapter.CoverImageLocked) return false;

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

        var coverImageNameWithoutExtension = ImageService.GetChapterFormat(chapter.Id, chapter.VolumeId);
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

        var shouldUpdateChapterColor = chapter.CoverImage != newCoverImage || NeedsColorSpace(chapter, forceColorScape);
        chapter.CoverImage = newCoverImage;
        if (shouldUpdateChapterColor)
        {
            imageService.UpdateColorScape(chapter);
        }
        unitOfWork.ChapterRepository.Update(chapter);
        _updateEvents.Add(MessageFactory.CoverUpdateEvent(chapter.Id, MessageFactoryEntityTypes.Chapter));

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
