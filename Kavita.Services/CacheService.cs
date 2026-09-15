using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.Common;
using Kavita.Common.Extensions;
using Kavita.Models.DTOs.Reader;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Helpers;
using Kavita.Services.Scanner;
using Microsoft.Extensions.Logging;
using NetVips;

namespace Kavita.Services;

public class CacheService(
    ILogger<CacheService> logger,
    IUnitOfWork unitOfWork,
    IDirectoryService directoryService,
    IReadingItemService readingItemService,
    IBookmarkService bookmarkService,
    ILocalizationService localizationService)
    : ICacheService
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ExtractLocks = new();
    private sealed record CacheInventory(string Mode, string Sources, Dictionary<string, long> Files);
    private static string SourceVersion(IEnumerable<MangaFile> files) => JsonSerializer.Serialize(files
        .OrderBy(f => f.Id).Select(f => new { f.Id, f.FilePath, f.Format, f.Bytes, f.LastModifiedUtc }));
    private bool IsComplete(string path, string mode, string sources)
    {
        try
        {
            var fs = directoryService.FileSystem;
            var marker = Path.Join(path, ".chapter-complete");
            if (!fs.File.Exists(marker)) return false;
            var inventory = JsonSerializer.Deserialize<CacheInventory>(fs.File.ReadAllText(marker));
            return inventory?.Mode == mode && inventory.Sources == sources && inventory.Files.Count > 0 && inventory.Files.All(file =>
                fs.File.Exists(Path.Join(path, file.Key)) && fs.FileInfo.New(Path.Join(path, file.Key)).Length == file.Value);
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    public IEnumerable<string> GetCachedPages(int chapterId)
    {
        var path = GetCachePath(chapterId);
        return directoryService.GetFilesWithExtension(path, Parser.ImageFileExtensions)
            .OrderByNatural(Path.GetFileNameWithoutExtension);
    }

    /// <summary>
    /// For a given path, scan all files (in reading order) and generate File Dimensions for it. Path must exist
    /// </summary>
    /// <param name="cachePath"></param>
    /// <returns></returns>
    public IEnumerable<FileDimensionDto> GetCachedFileDimensions(string cachePath)
    {
        var files = directoryService.GetFilesWithExtension(cachePath, Parser.ImageFileExtensions)
            .OrderByNatural(Path.GetFileNameWithoutExtension)
            .ToArray();

        if (files.Length == 0)
        {
            return ArraySegment<FileDimensionDto>.Empty;
        }

        var dimensions = new List<FileDimensionDto>();
        var originalCacheSize = Cache.MaxFiles;
        try
        {
            Cache.MaxFiles = 0;
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                using var image = Image.NewFromFile(file, memory: false, access: Enums.Access.SequentialUnbuffered);
                dimensions.Add(new FileDimensionDto()
                {
                    PageNumber = i,
                    Height = image.Height,
                    Width = image.Width,
                    IsWide = image.Width > image.Height,
                    FileName = file.Replace(cachePath, string.Empty)
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "There was an error calculating image dimensions for {CachePath}", cachePath);
        }
        finally
        {
            Cache.MaxFiles = originalCacheSize;
        }

        return dimensions;
    }

    public string GetCachedBookmarkPagePath(int seriesId, int page)
    {
        // Calculate what chapter the page belongs to
        var path = GetBookmarkCachePath(seriesId);
        var files = directoryService.GetFilesWithExtension(path, Parser.ImageFileExtensions);
        files = files
            .AsEnumerable()
            .OrderByNatural(Path.GetFileNameWithoutExtension)
            .ToArray();

        if (files.Length == 0)
        {
            return string.Empty;
        }

        // Since array is 0 based, we need to keep that in account (only affects last image)
        return page == files.Length ? files[page - 1] : files[page];
    }

    /// <summary>
    /// Returns the full path to the cached file. If the file does not exist, will fallback to the original.
    /// </summary>
    /// <param name="chapter"></param>
    /// <returns></returns>
    public string GetCachedFile(Chapter chapter)
    {
        var file = ChapterFileSelector.GetBestReadingFile(chapter.Files);
        if (file == null) return string.Empty;

        var extractPath = GetCachePath(chapter.Id);
        var path = Path.Join(extractPath, directoryService.FileSystem.Path.GetFileName(file.FilePath));
        if (!(directoryService.FileSystem.FileInfo.New(path).Exists))
        {
            path = file.FilePath;
        }
        return path;
    }

    public string GetCachedFile(int chapterId, string firstFilePath)
    {
        var extractPath = GetCachePath(chapterId);
        var path = Path.Join(extractPath, directoryService.FileSystem.Path.GetFileName(firstFilePath));
        if (!(directoryService.FileSystem.FileInfo.New(path).Exists))
        {
            path = firstFilePath;
        }
        return path;
    }


    /// <summary>
    /// Caches the files for the given chapter to CacheDirectory
    /// </summary>
    /// <param name="chapterId"></param>
    /// <param name="extractPdfToImages">Defaults to false. Extract pdf file into images rather than copying just the pdf file</param>
    /// <param name="ct"></param>
    /// <returns>This will always return the Chapter for the chapterId</returns>
    public async Task<Chapter?> Ensure(int chapterId, bool extractPdfToImages = false, CancellationToken ct = default)
    {
        using var activity = CacheActivityGate.Enter();
        directoryService.ExistOrCreate(directoryService.CacheDirectory);
        var chapter = await unitOfWork.ChapterRepository.GetChapterAsync(chapterId, ct: ct);
        var extractPath = GetCachePath(chapterId);

        var extractLock = ExtractLocks.GetOrAdd(chapterId, id => new SemaphoreSlim(1,1));

        await extractLock.WaitAsync(ct);

        try {
            if (chapter == null) return null;
            var mode = extractPdfToImages && chapter.Files.Any(f => f.Format == MangaFormat.Pdf) ? "images" : "reader";
            var sources = SourceVersion(chapter.Files);
            if (IsComplete(extractPath, mode, sources)) return chapter;
            var staging = extractPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + $".staging-{Guid.NewGuid():N}";
            try
            {
                await ExtractChapterFiles(staging, chapter.Files.ToList(), extractPdfToImages);
                ct.ThrowIfCancellationRequested();
                var inventory = directoryService.GetFiles(staging).ToDictionary(Path.GetFileName,
                    file => directoryService.FileSystem.FileInfo.New(file).Length);
                directoryService.FileSystem.File.WriteAllText(Path.Join(staging, ".chapter-complete"),
                    JsonSerializer.Serialize(new CacheInventory(mode, sources, inventory)));
                if (directoryService.Exists(extractPath)) directoryService.ClearAndDeleteDirectory(extractPath);
                directoryService.FileSystem.Directory.Move(staging, extractPath);
            }
            finally
            {
                if (directoryService.Exists(staging)) directoryService.ClearAndDeleteDirectory(staging);
            }
        } finally {
            extractLock.Release();
        }

        return chapter;
    }

    /// <summary>
    /// This is an internal method for cache service for extracting chapter files to disk. The code is structured
    /// for cache service, but can be re-used (download bookmarks)
    /// </summary>
    /// <param name="extractPath"></param>
    /// <param name="files"></param>
    /// <param name="extractPdfImages">Defaults to false, if true, will extract the images from the PDF renderer and not move the pdf file</param>
    /// <returns></returns>
    public async Task ExtractChapterFiles(string extractPath, IReadOnlyList<MangaFile>? files, bool extractPdfImages = false)
    {
        using var activity = CacheActivityGate.Enter();
        if (files == null || files.Count == 0) throw new KavitaException("Chapter has no files");
        files = files.OrderByNatural(f => f.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        directoryService.ExistOrCreate(extractPath);
        var fs = directoryService.FileSystem;
        var page = 0;
        var selected = ChapterFileSelector.GetBestReadingFile(files);
        for (var sourceIndex = 0; sourceIndex < files.Count; sourceIndex++)
        {
            var file = files[sourceIndex];
            if (file != selected && file.Format is MangaFormat.Epub or MangaFormat.Text ||
                file != selected && file.Format == MangaFormat.Pdf && !extractPdfImages) continue;
            if (!fs.File.Exists(file.FilePath)) throw new KavitaException(await localizationService.TranslateAsync("file-doesnt-exist"));
            if (file.Format == MangaFormat.Image)
            {
                fs.File.Copy(file.FilePath, Path.Join(extractPath, $"{page++:D8}{Path.GetExtension(file.FilePath)}"), false);
                continue;
            }
            if (file.Format == MangaFormat.Archive || (file.Format == MangaFormat.Pdf && extractPdfImages))
            {
                var sourcePath = Path.Join(extractPath, $"source-{sourceIndex:D8}");
                try
                {
                    readingItemService.Extract(file.FilePath, sourcePath, file.Format);
                    var images = directoryService.GetFilesWithExtension(sourcePath, Parser.ImageFileExtensions)
                        .OrderByNatural(Path.GetFileNameWithoutExtension).ToList();
                    if (images.Count == 0) throw new KavitaException("Archive contains no pages");
                    foreach (var image in images)
                        fs.File.Move(image, Path.Join(extractPath, $"{page++:D8}{Path.GetExtension(image)}"));
                }
                finally { if (directoryService.Exists(sourcePath)) directoryService.ClearAndDeleteDirectory(sourcePath); }
                continue;
            }
            if (file == selected && file.Format is MangaFormat.Epub or MangaFormat.Pdf or MangaFormat.Text)
                fs.File.Copy(file.FilePath, Path.Join(extractPath, Path.GetFileName(file.FilePath)), false);
        }
        if (!directoryService.GetFiles(extractPath).Any()) throw new KavitaException("Chapter has no readable files");
    }

    /// <summary>
    /// Removes the cached files and folders for a set of chapterIds
    /// </summary>
    /// <param name="chapterIds"></param>
    public void CleanupChapters(IEnumerable<int> chapterIds)
    {
        foreach (var chapter in chapterIds)
        {
            var gate = ExtractLocks.GetOrAdd(chapter, _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try { directoryService.ClearAndDeleteDirectory(GetCachePath(chapter)); }
            finally { gate.Release(); }
        }
    }

    /// <summary>
    /// Removes the cached files and folders for a set of chapterIds
    /// </summary>
    /// <param name="seriesIds"></param>
    public void CleanupBookmarks(IEnumerable<int> seriesIds)
    {
        foreach (var series in seriesIds)
        {
            directoryService.ClearAndDeleteDirectory(GetBookmarkCachePath(series));
        }
    }


    /// <summary>
    /// Returns the cache path for a given Chapter. Should be cacheDirectory/{chapterId}/
    /// </summary>
    /// <param name="chapterId"></param>
    /// <returns></returns>
    public string GetCachePath(int chapterId)
    {
        return directoryService.FileSystem.Path.GetFullPath(directoryService.FileSystem.Path.Join(directoryService.CacheDirectory, $"{chapterId}/"));
    }

    /// <summary>
    /// Returns the cache path for a given series' bookmarks. Should be cacheDirectory/{seriesId_bookmarks}/
    /// </summary>
    /// <param name="seriesId"></param>
    /// <returns></returns>
    public string GetBookmarkCachePath(int seriesId)
    {
        return directoryService.FileSystem.Path.GetFullPath(directoryService.FileSystem.Path.Join(directoryService.CacheDirectory, $"{seriesId}_bookmarks/"));
    }

    /// <summary>
    /// Returns the absolute path of a cached page.
    /// </summary>
    /// <param name="chapterId">Chapter id with Files populated.</param>
    /// <param name="page">Page number to look for</param>
    /// <returns>Page filepath or empty if no files found.</returns>
    public string GetCachedPagePath(int chapterId, int page)
    {
        // Calculate what chapter the page belongs to
        var path = GetCachePath(chapterId);
        // NOTE: We can optimize this by extracting and renaming, so we don't need to scan for the files and can do a direct access
        var files = directoryService.GetFilesWithExtension(path, Parser.ImageFileExtensions);

        return GetPageFromFiles(files, page);
    }

    public async Task<int> CacheBookmarkForSeries(int userId, int seriesId, CancellationToken ct = default)
    {
        var destDirectory = directoryService.FileSystem.Path.Join(directoryService.CacheDirectory, seriesId + "_bookmarks");
        if (directoryService.Exists(destDirectory)) return directoryService.GetFiles(destDirectory).Count();

        var bookmarkDtos = await unitOfWork.UserRepository.GetBookmarkDtosForSeries(userId, seriesId, ct);

        var files = (await bookmarkService.GetBookmarkFilesById(seriesId, bookmarkDtos.Select(b => b.Id), ct)).ToList();
        directoryService.CopyFilesToDirectory(files, destDirectory,
            Enumerable.Range(1, files.Count).Select(i => i + string.Empty).ToList());

        return files.Count;
    }

    /// <summary>
    /// Clears a cached bookmarks for a series id folder
    /// </summary>
    /// <param name="seriesId"></param>
    public void CleanupBookmarkCache(int seriesId)
    {
        var destDirectory = directoryService.FileSystem.Path.Join(directoryService.CacheDirectory, seriesId + "_bookmarks");
        if (!directoryService.Exists(destDirectory)) return;

        directoryService.ClearAndDeleteDirectory(destDirectory);
    }

    /// <summary>
    /// Returns either the file or an empty string
    /// </summary>
    /// <param name="files"></param>
    /// <param name="pageNum"></param>
    /// <returns></returns>
    public static string GetPageFromFiles(string[] files, int pageNum)
    {
        files = files
            .AsEnumerable()
            .OrderByNatural(Path.GetFileNameWithoutExtension)
            .ToArray();

        if (files.Length == 0)
        {
            return string.Empty;
        }

        if (pageNum < 0)
        {
            pageNum = 0;
        }

        // Since array is 0 based, we need to keep that in account (only affects last image)
        return pageNum >= files.Length ? files[Math.Min(pageNum - 1, files.Length - 1)] : files[pageNum];
    }


}
