using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.API.Services.SignalR;
using Kavita.Common.Extensions;
using Kavita.Common.Helpers;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
using Kavita.Services.Extensions;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Scanner;

/// <summary>
/// Responsible for taking parsed info from ReadingItemService and DirectoryService and combining them to emit DB work
/// on a series by series.
/// </summary>
public class ParseScannedFiles
{
    private static readonly HashSet<string> GdsFormatFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive", "archives", "book", "books", "cbz", "comic", "comics", "epub", "image", "images",
        "pdf", "rar", "text", "txt", "zip"
    };

    private readonly ILogger _logger;
    private readonly IDirectoryService _directoryService;
    private readonly IReadingItemService _readingItemService;
    private readonly IEventHub _eventHub;
    private readonly IMediaErrorService _mediaErrorService;

    /// <summary>
    /// An instance of a pipeline for processing files and returning a Map of Series -> ParserInfos.
    /// Each instance is separate from other threads, allowing for no cross over.
    /// </summary>
    /// <param name="logger">Logger of the parent class that invokes this</param>
    /// <param name="directoryService">Directory Service</param>
    /// <param name="readingItemService">ReadingItemService Service for extracting information on a number of formats</param>
    /// <param name="eventHub">For firing off SignalR events</param>
    /// <param name="mediaErrorService"></param>
    public ParseScannedFiles(ILogger logger, IDirectoryService directoryService,
        IReadingItemService readingItemService, IEventHub eventHub, IMediaErrorService mediaErrorService)
    {
        _logger = logger;
        _directoryService = directoryService;
        _readingItemService = readingItemService;
        _eventHub = eventHub;
        _mediaErrorService = mediaErrorService;
    }

    /// <summary>
    /// This will Scan all files in a folder path. For each folder within the folderPath, FolderAction will be invoked for all files contained
    /// </summary>
    /// <param name="scanDirectoryByDirectory">Scan directory by directory and for each, call folderAction</param>
    /// <param name="seriesPaths">A dictionary mapping a normalized path to a list of <see cref="SeriesModified"/> to help scanner skip I/O</param>
    /// <param name="folderPath">A library folder or series folder</param>
    /// <param name="forceCheck">If we should bypass any folder last write time checks on the scan and force I/O</param>
    public async Task<IList<ScanResult>> ScanFiles(string folderPath, bool scanDirectoryByDirectory,
        IDictionary<string, IList<SeriesModified>> seriesPaths, Library library, bool forceCheck = false)
    {
        var fileExtensions = string.Join("|", library.LibraryFileTypes.Select(l => l.FileTypeGroup.GetRegex()));

        // If there are no library file types, skip scanning entirely
        if (string.IsNullOrWhiteSpace(fileExtensions))
        {
            return ArraySegment<ScanResult>.Empty;
        }

        var matcher = BuildMatcher(library);

        var result = new List<ScanResult>();

        // Not to self: this whole thing can be parallelized because we don't deal with any DB or global state
        if (scanDirectoryByDirectory)
        {
            return await ScanDirectories(folderPath, seriesPaths, library, forceCheck, matcher, result, fileExtensions);
        }

        return await ScanSingleDirectory(folderPath, seriesPaths, library, forceCheck, result, fileExtensions, matcher);
    }

    private async Task<IList<ScanResult>> ScanDirectories(string folderPath, IDictionary<string, IList<SeriesModified>> seriesPaths,
        Library library, bool forceCheck, GlobMatcher matcher, List<ScanResult> result, string fileExtensions)
    {
        if (library.Type == LibraryType.GDS)
        {
            return await ScanDirectoriesBottomUp(folderPath, seriesPaths, library, forceCheck, matcher, result, fileExtensions);
        }

        var allDirectories = _directoryService.GetAllDirectories(folderPath, matcher)
            .Select(Parser.NormalizePath)
            .OrderByDescending(d => d.Length)
            .ToList();

        var processedDirs = new HashSet<string>();

        _logger.LogDebug("[ScannerService] Step 1.C Found {DirectoryCount} directories to process for {FolderPath}", allDirectories.Count, folderPath);
        foreach (var directory in allDirectories)
        {
            // Don't process any folders where we've already scanned everything below
            if (processedDirs.Any(d => d.StartsWith(directory + Path.AltDirectorySeparatorChar) || d.Equals(directory)))
            {
                var hasChanged = !HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, directory, forceCheck);
                // Skip this directory as we've already processed a parent unless there are loose files at that directory
                // and they have changes
                CheckSurfaceFiles(result, directory, folderPath, fileExtensions, matcher, hasChanged);
                continue;
            }

            // Skip directories ending with "Specials", let the parent handle it
            if (directory.EndsWith("Specials", StringComparison.OrdinalIgnoreCase))
            {
                // Log or handle that we are skipping this directory
                _logger.LogDebug("Skipping {Directory} as it ends with 'Specials'", directory);
                continue;
            }

            await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.FileScanProgressEvent(directory, library.Name, ProgressEventType.Updated));

            if (HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, directory, forceCheck))
            {
                HandleUnchangedFolder(result, folderPath, directory);
            }
            else
            {
                PerformFullScan(result, directory, folderPath, fileExtensions, matcher);
            }

            processedDirs.Add(directory);
        }

        return result;
    }

    private async Task<IList<ScanResult>> ScanDirectoriesBottomUp(string folderPath, IDictionary<string, IList<SeriesModified>> seriesPaths,
        Library library, bool forceCheck, GlobMatcher matcher, List<ScanResult> result, string fileExtensions)
    {
        var processedDirs = new HashSet<string>();
        var processedDirectoryCount = 0;

        _logger.LogDebug("[ScannerService] Step 1.C Streaming bottom-up GDS directory scan for {FolderPath}", folderPath);

        foreach (var directory in EnumerateDirectoriesBottomUp(folderPath, matcher))
        {
            processedDirectoryCount++;

            if (HasProcessedDescendant(processedDirs, directory))
            {
                var hasChanged = !HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, directory, forceCheck);
                CheckSurfaceFiles(result, directory, folderPath, fileExtensions, matcher, hasChanged);
                continue;
            }

            if (directory.EndsWith("Specials", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping {Directory} as it ends with 'Specials'", directory);
                continue;
            }

            await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.FileScanProgressEvent(directory, library.Name, ProgressEventType.Updated));

            if (HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, directory, forceCheck))
            {
                HandleUnchangedFolder(result, folderPath, directory);
            }
            else
            {
                PerformFullScan(result, directory, folderPath, fileExtensions, matcher);
            }

            processedDirs.Add(directory);
        }

        _logger.LogDebug("[ScannerService] Step 1.C Processed {DirectoryCount} streamed GDS directories for {FolderPath}",
            processedDirectoryCount, folderPath);

        return result;
    }

    private IEnumerable<string> EnumerateDirectoriesBottomUp(string folderPath, GlobMatcher matcher)
    {
        foreach (var subdirectory in _directoryService.GetDirectories(folderPath, matcher))
        {
            foreach (var childDirectory in EnumerateDirectoriesBottomUp(subdirectory, matcher))
            {
                yield return childDirectory;
            }

            yield return Parser.NormalizePath(subdirectory);
        }
    }

    private static bool HasProcessedDescendant(HashSet<string> processedDirs, string directory)
    {
        return processedDirs.Any(d => d.StartsWith(directory + Path.AltDirectorySeparatorChar) || d.Equals(directory));
    }

    /// <summary>
    /// Checks against all folder paths on file if the last scanned is >= the directory's last write time, down to the second
    /// </summary>
    /// <param name="library"></param>
    /// <param name="seriesPaths"></param>
    /// <param name="directory">This should be normalized</param>
    /// <param name="forceCheck"></param>
    /// <returns></returns>
    private bool HasSeriesFolderNotChangedSinceLastScan(Library library, IDictionary<string, IList<SeriesModified>> seriesPaths, string directory, bool forceCheck)
    {
        // Reverting code from: https://github.com/Kareadita/Kavita/pull/3619/files#diff-0625df477047ab9d8e97a900201f2f29b2dc0599ba58eb75cfbbd073a9f3c72f
        // This is to be able to release hotfix and tackle this in appropriate time

        // With the bottom-up approach, this can report a false positive where a nested folder will get scanned even though a parent is the series
        // This can't really be avoided. This is more likely to happen on Image chapter folder library layouts.
        if (forceCheck)
        {
            return false;
        }

        // GDS: rclone FUSE mounts can have stale directory mtimes due to --dir-cache-time,
        // so mtime-based change detection is unreliable. Always rescan GDS directories.
        if (library.Type == LibraryType.GDS)
        {
            return false;
        }

        if (!seriesPaths.TryGetValue(directory, out var seriesList))
        {
            // GDS libraries often keep files under a format folder directly below the real series folder.
            // Only handle that explicit layout to avoid broad parent fallback scans on normal libraries.
            if (library.Type != LibraryType.GDS)
            {
                return false;
            }

            var matchedByDirectoryName = false;
            if (IsGdsFormatFolder(directory))
            {
                var parentDirectory = _directoryService.GetParentDirectoryName(directory);
                if (string.IsNullOrEmpty(parentDirectory) || !seriesPaths.TryGetValue(parentDirectory, out seriesList))
                {
                    return false;
                }
            }
            else
            {
                seriesList = TryGetGdsSeriesListByDirectoryName(library, seriesPaths, directory);
                matchedByDirectoryName = seriesList != null;
            }

            if (seriesList == null)
            {
                return false;
            }

            if (library.Type == LibraryType.GDS && seriesList.Any(series => series.HasZeroPageFiles))
            {
                return false;
            }

            if (matchedByDirectoryName)
            {
                var directoryLastWriteTime = _directoryService.GetLastWriteTime(directory).Truncate(TimeSpan.TicksPerSecond);
                return seriesList.All(series => series.LastScanned.Truncate(TimeSpan.TicksPerSecond) >= directoryLastWriteTime);
            }
        }

        if (library.Type == LibraryType.GDS && seriesList.Any(series => series.HasZeroPageFiles))
        {
            return false;
        }

        // if (forceCheck)
        // {
        //     return false;
        // }

        // TryGetSeriesList falls back to parent folders to match to seriesList
        // var seriesList = TryGetSeriesList(library, seriesPaths, directory);
        // if (seriesList == null)
        // {
        //     return false;
        // }

        foreach (var series in seriesList)
        {
            var lastWriteTime = _directoryService.GetLastWriteTime(series.LowestFolderPath!).Truncate(TimeSpan.TicksPerSecond);
            var seriesLastScanned = series.LastScanned.Truncate(TimeSpan.TicksPerSecond);
            if (seriesLastScanned < lastWriteTime)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsGdsFormatFolder(string directory)
    {
        return GdsFormatFolderNames.Contains(Path.GetFileName(directory));
    }

    private IList<SeriesModified>? TryGetSeriesList(Library library, IDictionary<string, IList<SeriesModified>> seriesPaths, string directory)
    {
        if (seriesPaths.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        if (library.Folders.Any(fp => fp.Path.Equals(directory)))
        {
            return null;
        }

        if (seriesPaths.TryGetValue(directory, out var seriesList))
        {
            return seriesList;
        }

        var gdsSeriesList = TryGetGdsSeriesListByDirectoryName(library, seriesPaths, directory);
        if (gdsSeriesList != null)
        {
            return gdsSeriesList;
        }

        return TryGetSeriesList(library, seriesPaths, _directoryService.GetParentDirectoryName(directory));
    }

    private static IList<SeriesModified>? TryGetGdsSeriesListByDirectoryName(Library library, IDictionary<string, IList<SeriesModified>> seriesPaths, string directory)
    {
        if (library.Type != LibraryType.GDS)
        {
            return null;
        }

        var directoryName = Path.GetFileName(directory).ToNormalized();
        if (string.IsNullOrEmpty(directoryName))
        {
            return null;
        }

        var matches = seriesPaths.Values
            .SelectMany(series => series)
            .Where(series => series.SeriesName.ToNormalized().Equals(directoryName))
            .DistinctBy(series => (series.SeriesName, series.FolderPath, series.LowestFolderPath, series.Format))
            .ToList();

        return matches.Count == 0 ? null : matches;
    }

    /// <summary>
    /// Handles directories that haven't changed since the last scan.
    /// </summary>
    private void HandleUnchangedFolder(List<ScanResult> result, string folderPath, string directory)
    {
        if (result.Exists(r => r.Folder == directory))
        {
            _logger.LogDebug("[ProcessFiles] Skipping adding {Directory} as it's already added, this indicates a bad layout issue", directory);
        }
        else
        {
            _logger.LogDebug("[ProcessFiles] Skipping {Directory} as it hasn't changed since last scan", directory);
            result.Add(CreateScanResult(directory, folderPath, false, ArraySegment<string>.Empty));
        }
    }

    /// <summary>
    /// Performs a full scan of the directory and adds it to the result.
    /// </summary>
    private void PerformFullScan(List<ScanResult> result, string directory, string folderPath, string fileExtensions, GlobMatcher matcher)
    {
        _logger.LogDebug("[ProcessFiles] Performing full scan on {Directory}", directory);
        var files = _directoryService.ScanFiles(directory, fileExtensions, matcher);
        if (files.Count == 0)
        {
            _logger.LogDebug("[ProcessFiles] Empty directory: {Directory}. Keeping empty will cause Kavita to scan this each time", directory);
        }
        result.Add(CreateScanResult(directory, folderPath, true, files));
    }

    /// <summary>
    /// Performs a full scan of the directory and adds it to the result.
    /// </summary>
    private void CheckSurfaceFiles(List<ScanResult> result, string directory, string folderPath, string fileExtensions, GlobMatcher matcher, bool hasChanged)
    {
        var files = _directoryService.ScanFiles(directory, fileExtensions, matcher, SearchOption.TopDirectoryOnly);
        if (files.Count == 0)
        {
            return;
        }
        result.Add(CreateScanResult(directory, folderPath, hasChanged, files));
    }

    /// <summary>
    /// Scans a single directory and processes the scan result.
    /// </summary>
    private async Task<IList<ScanResult>> ScanSingleDirectory(string folderPath, IDictionary<string, IList<SeriesModified>> seriesPaths, Library library, bool forceCheck, List<ScanResult> result,
        string fileExtensions, GlobMatcher matcher)
    {
        var normalizedPath = Parser.NormalizePath(folderPath);
        var libraryRoot =
            library.Folders.FirstOrDefault(f =>
                normalizedPath.Contains(Parser.NormalizePath(f.Path)))?.Path ??
            folderPath;

        await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.FileScanProgressEvent(normalizedPath, library.Name, ProgressEventType.Updated));

        if (HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, normalizedPath, forceCheck))
        {
            result.Add(CreateScanResult(folderPath, libraryRoot, false, ArraySegment<string>.Empty));
        }
        else
        {
            result.Add(CreateScanResult(folderPath, libraryRoot, true,
                _directoryService.ScanFiles(folderPath, fileExtensions, matcher)));
        }

        return result;
    }

    private static GlobMatcher BuildMatcher(Library library)
    {
        var matcher = new GlobMatcher();
        foreach (var pattern in library.LibraryExcludePatterns.Where(p => !string.IsNullOrEmpty(p.Pattern)))
        {
            matcher.AddExclude(pattern.Pattern);
        }

        return matcher;
    }

    private static ScanResult CreateScanResult(string folderPath, string libraryRoot, bool hasChanged,
        IList<string> files)
    {
        return new ScanResult()
        {
            Files = files,
            Folder = Parser.NormalizePath(folderPath),
            LibraryRoot = libraryRoot,
            HasChanged = hasChanged
        };
    }

    /// <summary>
    /// Processes scanResults to track all series across the combined results.
    /// Ensures series are correctly grouped even if they span multiple folders.
    /// </summary>
    /// <param name="scanResults">A collection of scan results</param>
    /// <param name="scannedSeries">A concurrent dictionary to store the tracked series</param>
    public void TrackSeriesAcrossScanResults(IList<ScanResult> scanResults, ConcurrentDictionary<ParsedSeries, List<ParserInfo>> scannedSeries, LibraryType libraryType)
    {
        foreach (var scanResult in scanResults)
        {
            foreach (var info in scanResult.ParserInfos)
            {
                if (info == null) continue;

                try
                {
                    TrackSeries(scannedSeries, info, libraryType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ScannerService] Exception occurred during tracking {FilePath}. Skipping this file", info?.FullFilePath);
                }
            }
        }
    }


    /// <summary>
    /// Attempts to either add a new instance of a series mapping to the _scannedSeries bag or adds to an existing.
    /// This will check if the name matches an existing series name (multiple fields) <see cref="MergeName"/>
    /// </summary>
    /// <param name="scannedSeries">A localized list of a series' parsed infos</param>
    /// <param name="info"></param>
    private ParsedSeries? TrackSeries(ConcurrentDictionary<ParsedSeries, List<ParserInfo>> scannedSeries, ParserInfo? info, LibraryType libraryType)
    {
        if (info == null || info.Series == string.Empty) return null;

        // Do not ingest series with no meaningful information as title. These break merging as they'll all merge into each other
        // They would also merge all series that don't have localised names previously
        // This does create the edge case where some series may really not have any meaningful information.
        // But until this is reported, we should ignore it and play it safe!
        if (string.IsNullOrEmpty(info.Series.ToNormalized()))
        {
            _logger.LogCritical("[ScannerService] {SeriesName} @ {FileName} is empty when normalized, this file will not be ingested! The filename does not follow our guidelines or this is a bug in the parser, please report this! https://github.com/Kareadita/Kavita/issues",
                info.Series, info.Filename);

            _mediaErrorService.ReportMediaIssue(info.Filename, MediaErrorProducer.Scanner,
                "Failed to parse a valid series name for a file",
                $"{info.Series} is empty when normalized, this file will not be ingested! The filename does not follow our guidelines or this is a bug in the parser, please report this! https://github.com/Kareadita/Kavita/issues");

            return null;
        }

        // Check if normalized info.Series already exists and if so, update info to use that name instead
        info.Series = MergeName(scannedSeries, info, libraryType);

        // BUG: This will fail for Solo Leveling & Solo Leveling (Manga)

        var normalizedSeries = info.Series.ToNormalized();
        var normalizedSortSeries = info.SeriesSort.ToNormalized();
        var normalizedLocalizedSeries = info.LocalizedSeries.ToNormalized();

        try
        {
            var existingKey = scannedSeries.Keys.SingleOrDefault(Guard);
            existingKey ??= new ParsedSeries()
            {
                Format = info.Format,
                Name = info.Series,
                NormalizedName = normalizedSeries
            };

            scannedSeries.AddOrUpdate(existingKey, [info], (_, oldValue) =>
            {
                oldValue ??= new List<ParserInfo>();
                if (!oldValue.Contains(info))
                {
                    oldValue.Add(info);
                }

                return oldValue;
            });

            return existingKey;
        }
        catch (Exception ex)
        {
            #pragma warning disable S6667
            _logger.LogCritical("[ScannerService] {SeriesName} matches against multiple series in the parsed series. This indicates a critical kavita issue. Key will be skipped", info.Series);
            #pragma warning restore S6667
            foreach (var seriesKey in scannedSeries.Keys.Where(Guard))
            {
                _logger.LogCritical("[ScannerService] Matches: '{SeriesName}' matches on '{SeriesKey}'", info.Series, seriesKey.Name);
            }

            return null;
        }

        bool Guard(ParsedSeries series)
        {
            return MergeNameGuard(series.Format, series.NormalizedName, info.Format, libraryType,
                normalizedSeries, normalizedSortSeries, normalizedLocalizedSeries);
        }
    }


    /// <summary>
    /// Using a normalized name from the passed ParserInfo, this checks against all found series so far and if an existing one exists with
    /// same normalized name, it merges into the existing one. This is important as some manga may have a slight difference with punctuation or capitalization.
    /// </summary>
    /// <param name="scannedSeries"></param>
    /// <param name="info"></param>
    /// <returns>Series Name to group this info into</returns>
    private string MergeName(ConcurrentDictionary<ParsedSeries, List<ParserInfo>> scannedSeries, ParserInfo info, LibraryType libraryType)
    {

        var normalizedName = info.Series.ToNormalized();
        var normalizedSortName = info.SeriesSort.ToNormalized();
        var normalizedLocalizedName = info.LocalizedSeries.ToNormalized();

        try
        {
            var existingName = scannedSeries.SingleOrDefault(Guard).Key;

            if (existingName == null)
            {
                return info.Series;
            }

            if (!string.IsNullOrEmpty(existingName.Name))
            {
                return existingName.Name;
            }
        }
        catch (Exception ex)
        {
            #pragma warning disable S6667
            _logger.LogCritical("[ScannerService] Multiple series detected for {SeriesName} ({File})! This is critical to fix! There should only be 1", info.Series, info.FullFilePath);
            #pragma warning restore S6667
            var values = scannedSeries.Where(Guard);

            foreach (var pair in values)
            {
                _logger.LogCritical("[ScannerService] Duplicate Series in DB matches with {SeriesName}: {DuplicateName}", info.Series, pair.Key.Name);
            }

        }

        return info.Series;

        bool Guard(KeyValuePair<ParsedSeries, List<ParserInfo>> p)
        {
            return MergeNameGuard(p.Key.Format, p.Key.NormalizedName, info.Format, libraryType,
                normalizedName, normalizedSortName, normalizedLocalizedName);
        }
    }

    /// <summary>
    /// Checks if the given series should be merged into the other
    /// </summary>
    /// <param name="mergeIntoFormat"></param>
    /// <param name="mergeIntoSeries">Normalised name of the series to be merged into</param>
    /// <param name="format"></param>
    /// <param name="normalizedNames"></param>
    /// <returns></returns>
    private static bool MergeNameGuard(
        MangaFormat mergeIntoFormat, string mergeIntoSeries,
        MangaFormat format, LibraryType libraryType, params string[] normalizedNames)
    {
        if (libraryType != LibraryType.GDS && mergeIntoFormat != format) return false;

        if (string.IsNullOrEmpty(mergeIntoSeries)) return false;

        return normalizedNames.Any(n => n.Equals(mergeIntoSeries));
    }

    /// <summary>
    /// This will process series by folder groups. This is used solely by ScanSeries
    /// </summary>
    /// <param name="library">This should have the FileTypes included</param>
    /// <param name="folders"></param>
    /// <param name="isLibraryScan">If true, does a directory scan first (resulting in folders being tackled in parallel), else does an immediate scan files</param>
    /// <param name="seriesPaths">A map of Series names -> existing folder paths to handle skipping folders</param>
    /// <param name="forceCheck">Defaults to false</param>
    /// <returns></returns>
    public async Task<IList<ScannedSeriesResult>> ScanLibrariesForSeries(Library library,
        IList<string> folders, bool isLibraryScan,
        IDictionary<string, IList<SeriesModified>> seriesPaths, bool forceCheck = false)
    {
        await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.FileScanProgressEvent("File Scan Starting", library.Name, ProgressEventType.Started));

        _logger.LogDebug("[ScannerService] Library {LibraryName} Step 1.A: Process {FolderCount} folders", library.Name, folders.Count);
        var processedScannedSeries = new ConcurrentBag<ScannedSeriesResult>();

        foreach (var folder in folders)
        {
            try
            {
                await ScanAndParseFolder(folder, library, isLibraryScan, seriesPaths, processedScannedSeries, forceCheck);
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, "[ScannerService] The directory '{FolderPath}' does not exist", folder);
            }
        }

        await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.FileScanProgressEvent("File Scan Done", library.Name, ProgressEventType.Ended));

        return processedScannedSeries.ToList();
    }

    public async Task<IList<GdsScannedSeriesResult>> ScanGdsLibrariesForSeriesPlan(Library library,
        IList<string> folders, bool isLibraryScan,
        IDictionary<string, IList<SeriesModified>> seriesPaths, bool forceCheck = false)
    {
        await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.FileScanProgressEvent("File Scan Starting", library.Name, ProgressEventType.Started));

        _logger.LogInformation("[ScannerService] Lightweight GDS scan plan starting for {LibraryName}. Roots: {RootCount}",
            library.Name, folders.Count);

        var scannedSeries = new ConcurrentDictionary<ParsedSeries, List<GdsScannedFileRef>>();
        var changedSeries = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            try
            {
                await ScanAndPlanGdsFolder(folder, library, isLibraryScan, seriesPaths, scannedSeries, changedSeries,
                    forceCheck);
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, "[ScannerService] The directory '{FolderPath}' does not exist", folder);
            }
        }

        await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.FileScanProgressEvent("File Scan Done", library.Name, ProgressEventType.Ended));

        _logger.LogInformation("[ScannerService] Lightweight GDS scan plan finished for {LibraryName}. Parsed series: {SeriesCount}",
            library.Name, scannedSeries.Count);

        return scannedSeries
            .Where(pair => pair.Value.Count > 0)
            .Select(pair => new GdsScannedSeriesResult
            {
                ParsedSeries = pair.Key,
                HasChanged = changedSeries.Contains(BuildTrackedSeriesKey(pair.Key)),
                Files = pair.Value
            })
            .ToList();
    }

    public async Task<IList<ParserInfo>> ParseGdsSeriesFiles(Library library, IList<GdsScannedFileRef> files,
        IDictionary<string, IList<SeriesModified>> seriesPaths)
    {
        var scannedSeries = new ConcurrentDictionary<ParsedSeries, List<ParserInfo>>();

        foreach (var group in files.GroupBy(file => (file.Folder, file.LibraryRoot)))
        {
            var scanResult = CreateScanResult(group.Key.Folder, group.Key.LibraryRoot, true,
                group.Select(file => file.FilePath).ToList());
            await ParseAndTrackGdsScanResult(scanResult, seriesPaths, library, scannedSeries, []);
        }

        UpdateSeriesSortOrder(scannedSeries);

        return scannedSeries.Values
            .SelectMany(infos => infos)
            .GroupBy(info => Parser.NormalizePath(info.FullFilePath))
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// Helper method to scan and parse a folder
    /// </summary>
    /// <param name="folderPath"></param>
    /// <param name="library"></param>
    /// <param name="isLibraryScan"></param>
    /// <param name="seriesPaths"></param>
    /// <param name="processedScannedSeries"></param>
    /// <param name="forceCheck"></param>
    private async Task ScanAndParseFolder(string folderPath, Library library,
        bool isLibraryScan, IDictionary<string, IList<SeriesModified>> seriesPaths,
        ConcurrentBag<ScannedSeriesResult> processedScannedSeries, bool forceCheck)
    {
        if (library.Type == LibraryType.GDS)
        {
            await ScanAndParseGdsFolder(folderPath, library, isLibraryScan, seriesPaths, processedScannedSeries,
                forceCheck);
            return;
        }

        _logger.LogDebug("\t[ScannerService] Library {LibraryName} Step 1.B: Scan files in {Folder}", library.Name, folderPath);
        var scanResults = await ScanFiles(folderPath, isLibraryScan, seriesPaths, library, forceCheck);

        // Aggregate the scanned series across all scanResults
        var scannedSeries = new ConcurrentDictionary<ParsedSeries, List<ParserInfo>>();

        _logger.LogDebug("\t[ScannerService] Library {LibraryName} Step 1.C: Process files in {Folder}", library.Name, folderPath);
        foreach (var scanResult in scanResults)
        {
            await ParseFiles(scanResult, seriesPaths, library);
        }

        _logger.LogDebug("\t[ScannerService] Library {LibraryName} Step 1.D: Merge any localized series with series {Folder}", library.Name, folderPath);
        scanResults = MergeLocalizedSeriesAcrossScanResults(scanResults);

        _logger.LogDebug("\t[ScannerService] Library {LibraryName} Step 1.E: Group all parsed data into logical Series", library.Name);
        TrackSeriesAcrossScanResults(scanResults, scannedSeries, library.Type);


        // Now transform and add to processedScannedSeries AFTER everything is processed
        _logger.LogDebug("\t[ScannerService] Library {LibraryName} Step 1.F: Generate Sort Order for Series and Finalize", library.Name);
        GenerateProcessedScannedSeries(scannedSeries, scanResults, processedScannedSeries, library.Type);
    }

    private async Task ScanAndParseGdsFolder(string folderPath, Library library, bool isLibraryScan,
        IDictionary<string, IList<SeriesModified>> seriesPaths, ConcurrentBag<ScannedSeriesResult> processedScannedSeries,
        bool forceCheck)
    {
        var fileExtensions = string.Join("|", library.LibraryFileTypes.Select(l => l.FileTypeGroup.GetRegex()));
        if (string.IsNullOrWhiteSpace(fileExtensions))
        {
            return;
        }

        _logger.LogInformation("[ScannerService] Streaming GDS scan starting for {LibraryName} in {Folder}", library.Name, folderPath);

        var matcher = BuildMatcher(library);
        var scannedSeries = new ConcurrentDictionary<ParsedSeries, List<ParserInfo>>();
        var changedSeries = new HashSet<string>(StringComparer.Ordinal);
        var processedDirs = new HashSet<string>();
        var processedDirectoryCount = 0;

        if (isLibraryScan)
        {
            foreach (var directory in EnumerateDirectoriesBottomUp(folderPath, matcher))
            {
                processedDirectoryCount++;

                if (HasProcessedDescendant(processedDirs, directory))
                {
                    var surfaceHasChanged = !HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, directory, forceCheck);
                    await ParseAndTrackGdsSurfaceFiles(directory, folderPath, fileExtensions, matcher, seriesPaths,
                        library, scannedSeries, changedSeries, surfaceHasChanged);
                    continue;
                }

                if (directory.EndsWith("Specials", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Skipping {Directory} as it ends with 'Specials'", directory);
                    continue;
                }

                await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                    MessageFactory.FileScanProgressEvent(directory, library.Name, ProgressEventType.Updated));

                var hasChanged = !HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, directory, forceCheck);
                var scanResult = hasChanged
                    ? CreateScanResult(directory, folderPath, true, ScanDirectoryFiles(directory, fileExtensions, matcher))
                    : CreateScanResult(directory, folderPath, false, ArraySegment<string>.Empty);

                await ParseAndTrackGdsScanResult(scanResult, seriesPaths, library, scannedSeries, changedSeries);
                processedDirs.Add(directory);

                if (processedDirectoryCount % 250 == 0)
                {
                    _logger.LogDebug("[ScannerService] GDS streaming scan processed {DirectoryCount} directories for {FolderPath}",
                        processedDirectoryCount, folderPath);
                }
            }
        }
        else
        {
            var normalizedPath = Parser.NormalizePath(folderPath);
            var libraryRoot = library.Folders.FirstOrDefault(f =>
                normalizedPath.Contains(Parser.NormalizePath(f.Path)))?.Path ?? folderPath;

            await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.FileScanProgressEvent(normalizedPath, library.Name, ProgressEventType.Updated));

            var hasChanged = !HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, normalizedPath, forceCheck);
            var scanResult = hasChanged
                ? CreateScanResult(normalizedPath, libraryRoot, true, ScanDirectoryFiles(folderPath, fileExtensions, matcher))
                : CreateScanResult(normalizedPath, libraryRoot, false, ArraySegment<string>.Empty);

            await ParseAndTrackGdsScanResult(scanResult, seriesPaths, library, scannedSeries, changedSeries);
            processedDirectoryCount = 1;
        }

        _logger.LogInformation("[ScannerService] Streaming GDS scan grouped {SeriesCount} series from {DirectoryCount} directories for {LibraryName}",
            scannedSeries.Count, processedDirectoryCount, library.Name);
        _logger.LogDebug("\t[ScannerService] Library {LibraryName} Step 1.C: GDS streaming scan grouped {SeriesCount} series from {DirectoryCount} directories",
            library.Name, scannedSeries.Count, processedDirectoryCount);

        UpdateSeriesSortOrder(scannedSeries);
        CreateFinalGdsSeriesResults(scannedSeries, changedSeries, processedScannedSeries);
    }

    private async Task ScanAndPlanGdsFolder(string folderPath, Library library, bool isLibraryScan,
        IDictionary<string, IList<SeriesModified>> seriesPaths,
        ConcurrentDictionary<ParsedSeries, List<GdsScannedFileRef>> scannedSeries, HashSet<string> changedSeries,
        bool forceCheck)
    {
        var fileExtensions = string.Join("|", library.LibraryFileTypes.Select(l => l.FileTypeGroup.GetRegex()));
        if (string.IsNullOrWhiteSpace(fileExtensions))
        {
            return;
        }

        var matcher = BuildMatcher(library);
        var processedDirs = new HashSet<string>();

        if (isLibraryScan)
        {
            foreach (var directory in EnumerateDirectoriesBottomUp(folderPath, matcher))
            {
                if (HasProcessedDescendant(processedDirs, directory))
                {
                    var surfaceHasChanged = !HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, directory, forceCheck);
                    await ParseAndPlanGdsSurfaceFiles(directory, folderPath, fileExtensions, matcher, seriesPaths,
                        library, scannedSeries, changedSeries, surfaceHasChanged);
                    continue;
                }

                if (directory.EndsWith("Specials", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Skipping {Directory} as it ends with 'Specials'", directory);
                    continue;
                }

                await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                    MessageFactory.FileScanProgressEvent(directory, library.Name, ProgressEventType.Updated));

                var hasChanged = !HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, directory, forceCheck);
                var scanResult = hasChanged
                    ? CreateScanResult(directory, folderPath, true, ScanDirectoryFiles(directory, fileExtensions, matcher))
                    : CreateScanResult(directory, folderPath, false, ArraySegment<string>.Empty);

                await ParseAndPlanGdsScanResult(scanResult, seriesPaths, library, scannedSeries, changedSeries);
                processedDirs.Add(directory);
            }
        }
        else
        {
            var normalizedPath = Parser.NormalizePath(folderPath);
            var libraryRoot = library.Folders.FirstOrDefault(f =>
                normalizedPath.Contains(Parser.NormalizePath(f.Path)))?.Path ?? folderPath;

            await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.FileScanProgressEvent(normalizedPath, library.Name, ProgressEventType.Updated));

            var hasChanged = !HasSeriesFolderNotChangedSinceLastScan(library, seriesPaths, normalizedPath, forceCheck);
            var scanResult = hasChanged
                ? CreateScanResult(normalizedPath, libraryRoot, true, ScanDirectoryFiles(folderPath, fileExtensions, matcher))
                : CreateScanResult(normalizedPath, libraryRoot, false, ArraySegment<string>.Empty);

            await ParseAndPlanGdsScanResult(scanResult, seriesPaths, library, scannedSeries, changedSeries);
        }
    }

    private async Task ParseAndPlanGdsSurfaceFiles(string directory, string folderPath, string fileExtensions,
        GlobMatcher matcher, IDictionary<string, IList<SeriesModified>> seriesPaths, Library library,
        ConcurrentDictionary<ParsedSeries, List<GdsScannedFileRef>> scannedSeries, HashSet<string> changedSeries,
        bool hasChanged)
    {
        var files = _directoryService.ScanFiles(directory, fileExtensions, matcher, SearchOption.TopDirectoryOnly);
        if (files.Count == 0)
        {
            return;
        }

        await ParseAndPlanGdsScanResult(CreateScanResult(directory, folderPath, hasChanged, files), seriesPaths,
            library, scannedSeries, changedSeries);
    }

    private async Task ParseAndPlanGdsScanResult(ScanResult scanResult,
        IDictionary<string, IList<SeriesModified>> seriesPaths, Library library,
        ConcurrentDictionary<ParsedSeries, List<GdsScannedFileRef>> scannedSeries, HashSet<string> changedSeries)
    {
        await ParseFiles(scanResult, seriesPaths, library);

        foreach (var info in scanResult.ParserInfos)
        {
            ParsedSeries? series = null;
            try
            {
                series = TrackGdsSeriesFile(scannedSeries, info, scanResult.Folder, scanResult.LibraryRoot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScannerService] Exception occurred during GDS planning {FilePath}. Skipping this file",
                    info?.FullFilePath);
            }

            if (scanResult.HasChanged && series != null)
            {
                changedSeries.Add(BuildTrackedSeriesKey(series));
            }
        }

        scanResult.Files = ArraySegment<string>.Empty;
        scanResult.ParserInfos = ArraySegment<ParserInfo>.Empty;
    }

    private ParsedSeries? TrackGdsSeriesFile(ConcurrentDictionary<ParsedSeries, List<GdsScannedFileRef>> scannedSeries,
        ParserInfo? info, string folder, string libraryRoot)
    {
        if (info == null || string.IsNullOrEmpty(info.Series) || string.IsNullOrEmpty(info.FullFilePath)) return null;

        if (string.IsNullOrEmpty(info.Series.ToNormalized()))
        {
            _logger.LogCritical("[ScannerService] {SeriesName} @ {FileName} is empty when normalized, this file will not be ingested!",
                info.Series, info.Filename);
            return null;
        }

        var normalizedSeries = info.Series.ToNormalized();
        var normalizedSortSeries = info.SeriesSort.ToNormalized();
        var normalizedLocalizedSeries = info.LocalizedSeries.ToNormalized();
        var existingKey = scannedSeries.Keys.SingleOrDefault(series =>
            MergeNameGuard(series.Format, series.NormalizedName, info.Format, LibraryType.GDS,
                normalizedSeries, normalizedSortSeries, normalizedLocalizedSeries));
        existingKey ??= new ParsedSeries
        {
            Format = info.Format,
            Name = info.Series,
            NormalizedName = normalizedSeries
        };

        var fileRef = new GdsScannedFileRef
        {
            FilePath = Parser.NormalizePath(info.FullFilePath),
            Folder = Parser.NormalizePath(folder),
            LibraryRoot = libraryRoot
        };

        scannedSeries.AddOrUpdate(existingKey, [fileRef], (_, oldValue) =>
        {
            oldValue ??= [];
            if (oldValue.All(existing => !Parser.NormalizePath(existing.FilePath).Equals(fileRef.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                oldValue.Add(fileRef);
            }

            return oldValue;
        });

        return existingKey;
    }

    private IList<string> ScanDirectoryFiles(string directory, string fileExtensions, GlobMatcher matcher)
    {
        var files = _directoryService.ScanFiles(directory, fileExtensions, matcher);
        if (files.Count == 0)
        {
            _logger.LogDebug("[ProcessFiles] Empty directory: {Directory}. Keeping empty will cause Kavita to scan this each time", directory);
        }

        return files;
    }

    private async Task ParseAndTrackGdsSurfaceFiles(string directory, string folderPath, string fileExtensions,
        GlobMatcher matcher, IDictionary<string, IList<SeriesModified>> seriesPaths, Library library,
        ConcurrentDictionary<ParsedSeries, List<ParserInfo>> scannedSeries, HashSet<string> changedSeries,
        bool hasChanged)
    {
        var files = _directoryService.ScanFiles(directory, fileExtensions, matcher, SearchOption.TopDirectoryOnly);
        if (files.Count == 0)
        {
            return;
        }

        await ParseAndTrackGdsScanResult(CreateScanResult(directory, folderPath, hasChanged, files), seriesPaths,
            library, scannedSeries, changedSeries);
    }

    private async Task ParseAndTrackGdsScanResult(ScanResult scanResult,
        IDictionary<string, IList<SeriesModified>> seriesPaths, Library library,
        ConcurrentDictionary<ParsedSeries, List<ParserInfo>> scannedSeries, HashSet<string> changedSeries)
    {
        await ParseFiles(scanResult, seriesPaths, library);

        foreach (var info in scanResult.ParserInfos)
        {
            ParsedSeries? series = null;
            try
            {
                series = TrackSeries(scannedSeries, info, library.Type);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScannerService] Exception occurred during GDS tracking {FilePath}. Skipping this file",
                    info?.FullFilePath);
            }

            if (scanResult.HasChanged && series != null)
            {
                changedSeries.Add(BuildTrackedSeriesKey(series));
            }
        }

        scanResult.Files = ArraySegment<string>.Empty;
        scanResult.ParserInfos = ArraySegment<ParserInfo>.Empty;
    }

    private static void CreateFinalGdsSeriesResults(ConcurrentDictionary<ParsedSeries, List<ParserInfo>> scannedSeries,
        HashSet<string> changedSeries, ConcurrentBag<ScannedSeriesResult> processedScannedSeries)
    {
        foreach (var series in scannedSeries.Keys)
        {
            if (scannedSeries[series].Count <= 0) continue;

            processedScannedSeries.Add(new ScannedSeriesResult
            {
                HasChanged = changedSeries.Contains(BuildTrackedSeriesKey(series)),
                ParsedSeries = series,
                ParsedInfos = scannedSeries[series]
            });
        }
    }

    private static string BuildTrackedSeriesKey(ParsedSeries series)
    {
        return $"{series.Format}:{series.NormalizedName}";
    }

    /// <summary>
    /// Processes and generates the final results for processedScannedSeries after updating sort order.
    /// </summary>
    /// <param name="scannedSeries">A concurrent dictionary of tracked series and their parsed infos</param>
    /// <param name="scanResults">List of all scan results, used to determine if any series has changed</param>
    /// <param name="processedScannedSeries">A thread-safe concurrent bag of processed series results</param>
    private void GenerateProcessedScannedSeries(ConcurrentDictionary<ParsedSeries, List<ParserInfo>> scannedSeries, IList<ScanResult> scanResults,
        ConcurrentBag<ScannedSeriesResult> processedScannedSeries, LibraryType libraryType)
    {
        // First, update the sort order for all series
        UpdateSeriesSortOrder(scannedSeries);

        // Now, generate the final processed scanned series results
        CreateFinalSeriesResults(scannedSeries, scanResults, processedScannedSeries, libraryType);
    }

    /// <summary>
    /// Updates the sort order for all series in the scannedSeries dictionary.
    /// </summary>
    /// <param name="scannedSeries">A concurrent dictionary of tracked series and their parsed infos</param>
    private void UpdateSeriesSortOrder(ConcurrentDictionary<ParsedSeries, List<ParserInfo>> scannedSeries)
    {
        foreach (var series in scannedSeries.Keys)
        {
            if (scannedSeries[series].Count <= 0) continue;

            try
            {
                UpdateSortOrder(scannedSeries, series);  // Call to method that updates sort order
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScannerService] Issue occurred while setting IssueOrder for series {SeriesName}", series.Name);
            }
        }
    }

    /// <summary>
    /// Generates the final processed scanned series results after processing the sort order.
    /// </summary>
    /// <param name="scannedSeries">A concurrent dictionary of tracked series and their parsed infos</param>
    /// <param name="scanResults">List of all scan results, used to determine if any series has changed</param>
    /// <param name="processedScannedSeries">The list where processed results will be added</param>
    private static void CreateFinalSeriesResults(ConcurrentDictionary<ParsedSeries, List<ParserInfo>> scannedSeries,
        IList<ScanResult> scanResults, ConcurrentBag<ScannedSeriesResult> processedScannedSeries, LibraryType libraryType)
    {
        foreach (var series in scannedSeries.Keys)
        {
            if (scannedSeries[series].Count <= 0) continue;

            processedScannedSeries.Add(new ScannedSeriesResult
            {
                HasChanged = scanResults.Any(sr => sr.HasChanged &&
                                                   sr.ParserInfos.Any(info => IsParserInfoForSeries(info, series, libraryType))),
                ParsedSeries = series,
                ParsedInfos = scannedSeries[series]
            });
        }
    }

    private static bool IsParserInfoForSeries(ParserInfo info, ParsedSeries series, LibraryType libraryType)
    {
        if (!info.Series.ToNormalized().Equals(series.NormalizedName)) return false;

        return libraryType == LibraryType.GDS || info.Format == series.Format;
    }

    /// <summary>
    /// Merges localized series with the series field across all scan results.
    /// Combines ParserInfos from all scanResults and processes them collectively
    /// to ensure consistent series names.
    /// </summary>
    /// <example>
    /// Accel World v01.cbz has Series "Accel World" and Localized Series "World of Acceleration"
    /// World of Acceleration v02.cbz has Series "World of Acceleration"
    /// After running this code, we'd have:
    /// World of Acceleration v02.cbz having Series "Accel World" and Localized Series of "World of Acceleration"
    /// </example>
    /// <param name="scanResults">A collection of scan results</param>
    /// <returns>A new list of scan results with merged series</returns>
    private IList<ScanResult> MergeLocalizedSeriesAcrossScanResults(IList<ScanResult> scanResults)
    {
        // Flatten all ParserInfos across scanResults
        var allInfos = scanResults.SelectMany(sr => sr.ParserInfos).ToList();

        // Filter relevant infos (non-special and with localized series)
        var relevantInfos = GetRelevantInfos(allInfos);

        if (relevantInfos.Count == 0) return scanResults;

        // Get distinct localized series and process each one
        var distinctLocalizedSeries = relevantInfos
            .Select(i => i.LocalizedSeries)
            .Distinct()
            .ToList();

        foreach (var localizedSeries in distinctLocalizedSeries)
        {
            if (string.IsNullOrEmpty(localizedSeries)) continue;

            // Process the localized series for merging
            ProcessLocalizedSeries(scanResults, allInfos, relevantInfos, localizedSeries);
        }

        // Remove or clear any scan results that now have no ParserInfos after merging
        return scanResults.Where(sr => sr.ParserInfos.Count > 0).ToList();
    }

    private static List<ParserInfo> GetRelevantInfos(List<ParserInfo> allInfos)
    {
        return allInfos
            .Where(i => !i.IsSpecial && !string.IsNullOrEmpty(i.LocalizedSeries))
            .GroupBy(i => i.Format)
            .SelectMany(g => g.ToList())
            .ToList();
    }

    private void ProcessLocalizedSeries(IList<ScanResult> scanResults, List<ParserInfo> allInfos, List<ParserInfo> relevantInfos, string localizedSeries)
    {
        var seriesForLocalized = GetSeriesForLocalized(relevantInfos, localizedSeries);
        if (seriesForLocalized.Count == 0) return;

        var nonLocalizedSeries = GetNonLocalizedSeries(seriesForLocalized, localizedSeries);
        if (nonLocalizedSeries == null) return;

        // Remap and update relevant ParserInfos
        RemapSeries(scanResults, allInfos, localizedSeries, nonLocalizedSeries);

    }

    private static List<string> GetSeriesForLocalized(List<ParserInfo> relevantInfos, string localizedSeries)
    {
        return relevantInfos
            .Where(i => i.LocalizedSeries == localizedSeries)
            .DistinctBy(r => r.Series)
            .Select(r => r.Series)
            .ToList();
    }

    private string? GetNonLocalizedSeries(List<string> seriesForLocalized, string localizedSeries)
    {
        switch (seriesForLocalized.Count)
        {
            case 1:
                return seriesForLocalized[0];
            case <= 2:
                return seriesForLocalized.FirstOrDefault(s => !s.Equals(Parser.Normalize(localizedSeries)));
            default:
                _logger.LogError(
                    "[ScannerService] Multiple series detected across scan results that contain localized series. " +
                    "This will cause them to group incorrectly. Please separate series into their own dedicated folder: {LocalizedSeries}",
                    string.Join(", ", seriesForLocalized)
                );
                return null;
        }
    }

    private static void RemapSeries(IList<ScanResult> scanResults, List<ParserInfo> allInfos, string localizedSeries, string nonLocalizedSeries)
    {
        // If the series names are identical, no remapping is needed (rare but valid)
        if (localizedSeries.ToNormalized().Equals(nonLocalizedSeries.ToNormalized()))
        {
            return;
        }

        // Find all infos that need to be remapped from the localized series to the non-localized series
        var normalizedLocalizedSeries = localizedSeries.ToNormalized();
        var seriesToBeRemapped = allInfos.Where(i => i.Series.ToNormalized().Equals(normalizedLocalizedSeries)).ToList();

        foreach (var infoNeedingMapping in seriesToBeRemapped)
        {
            infoNeedingMapping.Series = nonLocalizedSeries;

            // Find the scan result containing the localized info
            var localizedScanResult = scanResults.FirstOrDefault(sr => sr.ParserInfos.Contains(infoNeedingMapping));
            if (localizedScanResult == null) continue;

            // Remove the localized series from this scan result
            localizedScanResult.ParserInfos.Remove(infoNeedingMapping);

            // Find the scan result that should be merged with
            var nonLocalizedScanResult = scanResults.FirstOrDefault(sr => sr.ParserInfos.Any(pi => pi.Series == nonLocalizedSeries));

            if (nonLocalizedScanResult == null) continue;

            // Add the remapped info to the non-localized scan result
            nonLocalizedScanResult.ParserInfos.Add(infoNeedingMapping);

            // Assign the higher folder path (i.e., the one closer to the root)
            //nonLocalizedScanResult.Folder = DirectoryService.GetDeepestCommonPath(localizedScanResult.Folder, nonLocalizedScanResult.Folder);
        }
    }

    /// <summary>
    /// For a given ScanResult, sets the ParserInfos on the result
    /// </summary>
    /// <param name="result"></param>
    /// <param name="seriesPaths"></param>
    /// <param name="library"></param>
    private async Task ParseFiles(ScanResult result, IDictionary<string, IList<SeriesModified>> seriesPaths, Library library)
    {
        var normalizedFolder = Parser.NormalizePath(result.Folder);

        // If folder hasn't changed, generate fake ParserInfos
        if (!result.HasChanged)
        {
            var seriesList = TryGetSeriesList(library, seriesPaths, normalizedFolder);
            if (seriesList == null)
            {
                _logger.LogDebug("[ScannerService] Skipped File Scan for {Folder} but no matching existing series path was found", normalizedFolder);
                result.ParserInfos = ArraySegment<ParserInfo>.Empty;
                return;
            }

            result.ParserInfos = seriesList
                .Select(fp => new ParserInfo { Series = fp.SeriesName, Format = fp.Format })
                .ToList();

            _logger.LogDebug("[ScannerService] Skipped File Scan for {Folder} as it hasn't changed", normalizedFolder);
            await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.FileScanProgressEvent($"Skipped {normalizedFolder}", library.Name, ProgressEventType.Updated));
            return;
        }

        var files = result.Files;
        var fileCount = files.Count;

        if (fileCount == 0)
        {
            _logger.LogInformation("[ScannerService] {Folder} is empty or has no matching file types", normalizedFolder);
            result.ParserInfos = ArraySegment<ParserInfo>.Empty;
            return;
        }

        _logger.LogDebug("[ScannerService] Found {Count} files for {Folder}", files.Count, normalizedFolder);
        await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.FileScanProgressEvent($"{fileCount} files in {normalizedFolder}", library.Name, ProgressEventType.Updated));

        // GDS mounts can expose very large remote directories. Avoid creating one task per file there.
        if (library.Type == LibraryType.GDS || fileCount < 100)
        {
            result.ParserInfos = files
                .Select(file => _readingItemService.ParseFile(file, normalizedFolder, result.LibraryRoot, library.Type, library.EnableMetadata))
                .Where(info => info != null)
                .ToList()!;
        }
        else
        {
            // Process files in parallel, but bound the concurrency so that a folder with
            // many files cannot flood the ThreadPool and starve request handling (the web UI
            // and health checks become unresponsive during scans otherwise).
            // Matches the scanner's existing parallelism convention (see ScannerService).
            var maxConcurrency = Math.Max(1, Environment.ProcessorCount / 2);
            using var throttler = new SemaphoreSlim(maxConcurrency);

            var tasks = files.Select(async file =>
            {
                await throttler.WaitAsync();

                try
                {
                    // Task.Run keeps the synchronous parser work off the enumerating thread
                    // while the semaphore bounds how many parses run at once.
                    return await Task.Run(() =>
                        _readingItemService.ParseFile(file, normalizedFolder, result.LibraryRoot, library.Type, library.EnableMetadata));
                }
                finally
                {
                    throttler.Release();
                }
            });

            // Task.WhenAll preserves input (file) order, which downstream series mapping relies on
            var infos = await Task.WhenAll(tasks);
            result.ParserInfos = infos.Where(info => info != null).ToList()!;
        }

        result.Files = ArraySegment<string>.Empty;
    }


    private static void UpdateSortOrder(ConcurrentDictionary<ParsedSeries, List<ParserInfo>> scannedSeries, ParsedSeries series)
    {
        // Set the Sort order per Volume
        var volumes = scannedSeries[series].GroupBy(info => info.Volumes);
        foreach (var volume in volumes)
        {
            var infos = scannedSeries[series].Where(info => info.Volumes == volume.Key).ToList();
            IList<ParserInfo> chapters;
            var specialTreatment = infos.TrueForAll(info => info.IsSpecial);
            var hasAnySpMarker = infos.Exists(info => info.SpecialIndex > 0);
            var counter = 0f;

            // Handle specials with SpecialIndex
            if (specialTreatment && hasAnySpMarker)
            {
                chapters = infos
                    .OrderBy(info => info.SpecialIndex)
                    .ToList();

                foreach (var chapter in chapters)
                {
                    chapter.IssueOrder = counter;
                    counter++;
                }
                continue;
            }

            // Handle specials without SpecialIndex (natural order)
            if (specialTreatment)
            {
                chapters = infos
                    .OrderByNatural(info => Parser.RemoveExtensionIfSupported(info.Filename)!)
                    .ToList();

                foreach (var chapter in chapters)
                {
                    chapter.IssueOrder = counter;
                    counter++;
                }
                continue;
            }

            // Ensure chapters are sorted numerically when possible, otherwise push unparseable to the end
            chapters = infos
                .OrderBy(info => float.TryParse(info.Chapters, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) ? val : float.MaxValue)
                .ToList();

            counter = 0f;
            var prevIssue = string.Empty;
            foreach (var chapter in chapters)
            {
                // Use MinNumber in case there is a range, as otherwise sort order will cause it to be processed last
                var chapterNum =
                    $"{Parser.MinNumberFromRange(chapter.Chapters).ToString(CultureInfo.InvariantCulture)}";
                if (float.TryParse(chapterNum, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedChapter))
                {
                    // Parsed successfully, use the numeric value
                    counter = parsedChapter;
                    chapter.IssueOrder = counter;

                    // Increment for next chapter (unless the next has a similar value, then add 0.1)
                    if (!string.IsNullOrEmpty(prevIssue) && float.TryParse(prevIssue, NumberStyles.Any, CultureInfo.InvariantCulture, out var prevIssueFloat) && parsedChapter.Is(prevIssueFloat))
                    {
                        counter += 0.1f; // bump if same value as the previous issue
                    }
                    prevIssue = $"{parsedChapter.ToString(CultureInfo.InvariantCulture)}";
                }
                else
                {
                    // Unparsed chapters: use the current counter and bump for the next
                    if (!string.IsNullOrEmpty(prevIssue) && prevIssue == counter.ToString(CultureInfo.InvariantCulture))
                    {
                        counter += 0.1f; // bump if same value as the previous issue
                    }
                    chapter.IssueOrder = counter;
                    counter++;
                    prevIssue = chapter.Chapters;
                }
            }
        }
    }
}
