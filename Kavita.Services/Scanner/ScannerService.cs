using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Kavita.API.Database;
using Kavita.API.Repositories;
using Kavita.API.Services;
using Kavita.API.Services.Metadata;
using Kavita.API.Services.Scanner;
using Kavita.API.Services.SignalR;
using Kavita.Common.Extensions;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.KavitaPlus.Metadata;
using Kavita.Models.DTOs.MediaErrors;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.DTOs.SignalR.Bodies;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
using Kavita.Models.Scanner;
using Kavita.Services.Extensions;
using Kavita.Services.Helpers;
using Kavita.Services.Plus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Scanner;

public enum ScanCancelReason
{
    /// <summary>
    /// Don't cancel, everything is good
    /// </summary>
    NoCancel = 0,
    /// <summary>
    /// A folder is completely empty or missing
    /// </summary>
    FolderMount = 1,
    /// <summary>
    /// There has been no change to the filesystem since last scan
    /// </summary>
    NoChange = 2,
    /// <summary>
    /// The underlying folder is missing
    /// </summary>
    FolderMissing = 3
}

/**
 * Responsible for Scanning the disk and importing/updating/deleting files -> DB entities.
 */
public class ScannerService(
    IUnitOfWork unitOfWork,
    ILogger<ScannerService> logger,
    IMetadataService metadataService,
    ICacheService cacheService,
    IEventHub eventHub,
    IDirectoryService directoryService,
    IReadingItemService readingItemService,
    IServiceScopeFactory scopeFactory,
    IWordCountAnalyzerService wordCountAnalyzerService,
    IMapper mapper)
    : IScannerService
{
    public const string Name = "ScannerService";
    private const int Timeout = 60 * 60 * 60; // 2.5 days
    // A retry waits in Scheduled, which IsScannerBusy does not count, so it would run beside a scan started meanwhile
    private const int RetryAttempts = 0;

    /// <summary>
    /// This is only used for v0.7 to get files analyzed
    /// </summary>
    public async Task AnalyzeFiles()
    {
        logger.LogInformation("Starting Analyze Files task");
        var missingExtensions = await unitOfWork.MangaFileRepository.GetAllWithMissingExtension();
        if (missingExtensions.Count == 0)
        {
            logger.LogInformation("Nothing to do");
            return;
        }

        var sw = Stopwatch.StartNew();

        foreach (var file in missingExtensions)
        {
            var fileInfo = directoryService.FileSystem.FileInfo.New(file.FilePath);
            if (!fileInfo.Exists)continue;
            file.Extension = fileInfo.Extension.ToLowerInvariant();
            file.Bytes = fileInfo.Length;
            unitOfWork.MangaFileRepository.Update(file);
        }

        await unitOfWork.CommitAsync();

        logger.LogInformation("Completed Analyze Files task in {ElapsedTime}", sw.Elapsed);
    }

    /// <summary>
    /// Given a generic folder path, will invoke a Series scan or Library scan.
    /// </summary>
    /// <remarks>Nothing is requested when a queued scan already covers the change</remarks>
    public async Task ScanFolder(ScanFolderRequest request)
    {
        try
        {
            await RequestScanForFolder(request);
        }
        finally
        {
            await SendIfFolderStoppedWaiting(request.Folder);
        }
    }

    /// <summary>
    /// The activity widget lists waiting folders, so it is told when the last job for this folder has run
    /// </summary>
    private async Task SendIfFolderStoppedWaiting(string folder)
    {
        if (ScanJobQueue.Read().FolderJobs.Any(j => j.Request.Folder == folder)) return;
        await eventHub.SendMessageAsync(MessageFactory.ScanRescheduled, TaskScheduler.ScanQueueChangedEvent());
    }

    private async Task RequestScanForFolder(ScanFolderRequest request)
    {
        var folder = request.Folder;
        var series = await FindSeriesForFolder(folder, request.ChangedPath);

        if (series != null)
        {
            RequestScan(ScanTarget.Series(series.LibraryId, series.Id), folder);
            return;
        }

        if (request.AbortOnNoSeriesMatch) return;


        // This is basically rework of what's already done in Library Watcher but is needed if invoked via API
        var parentDirectory = directoryService.GetParentDirectoryName(folder);
        if (string.IsNullOrEmpty(parentDirectory))
        {
            logger.LogWarning("[ScannerService] Scan folder invoked for {Folder} but parent directory is empty. Dropping request", folder);
            return;
        }

        var libraries = (await unitOfWork.LibraryRepository.GetLibraryDtosAsync()).ToList();
        var libraryFolders = libraries.SelectMany(l => l.Folders);
        var libraryFolder = folder.DeepestContainingFolder(libraryFolders.Select(Parser.NormalizePath));

        if (string.IsNullOrEmpty(libraryFolder))
        {
            logger.LogWarning("[ScannerService] Scan folder invoked for {Folder} but no matching library found. Dropping request", folder);
            return;
        }

        var library = libraries.Find(l => l.Folders.Select(Parser.NormalizePath).Contains(libraryFolder));

        if (library != null)
        {
            RequestScan(ScanTarget.Library(library.Id), folder);
        }
    }

    internal void RequestScan(ScanTarget target, string folder)
    {
        lock (TaskScheduler.ScanRequestLock)
        {
            if (ScanJobQueue.Read().Jobs.FindAlreadyRequested(target) is { } covering)
            {
                logger.LogDebug("[ScannerService] Scan folder invoked for {Folder} but job {JobId} already covers it. Dropping request",
                    folder, covering.JobId);
                return;
            }

            var libraryId = target.LibraryId!.Value;
            if (target.SeriesId is { } seriesId)
            {
                logger.LogDebug("[ScannerService] Scan folder invoked for {Folder}, requesting a scan of series {SeriesId}", folder, seriesId);
                ScanJobQueue.Enqueue<ITaskScheduler>(t => t.EnqueueScanSeries(libraryId, seriesId, false));
                return;
            }

            logger.LogDebug("[ScannerService] Scan folder invoked for {Folder}, requesting a scan of library {LibraryId}", folder, libraryId);
            ScanJobQueue.Enqueue<ITaskScheduler>(t => t.EnqueueScanLibrary(libraryId, false));
        }
    }

    /// <summary>
    /// The one series that owns the changed path. Null when there is none, or when several could own it and only a
    /// library scan would pick up a new series beside them
    /// </summary>
    internal async Task<Series?> FindSeriesForFolder(string folder, string originalPath)
    {
        var path = string.IsNullOrEmpty(originalPath) ? folder : originalPath;

        var byLowestFolder = await unitOfWork.SeriesRepository.GetSeriesThatContainsLowestFolderPathAsync(path, SeriesIncludes.Library);
        if (byLowestFolder.Count == 1) return byLowestFolder[0];

        if (byLowestFolder.Count > 1)
        {
            logger.LogInformation("[ScannerService] {Count} series share the folder of {Path}. Library scan will be used for ScanFolder",
                byLowestFolder.Count, path);
            return null;
        }

        // Every series under a publisher folder has it as FolderPath, and ScanSeries would drop a new series beside them
        var byFolder = await unitOfWork.SeriesRepository.GetSeriesByFolderPathAsync(folder, SeriesIncludes.Library);
        if (byFolder.Count == 1 && string.IsNullOrEmpty(byFolder[0].LowestFolderPath)) return byFolder[0];

        if (byFolder.Count > 0)
        {
            logger.LogInformation("[ScannerService] {Path} is not inside a single series folder. Library scan will be used for ScanFolder", path);
        }

        return null;
    }

    /// <summary>
    /// Scans just an existing Series for changes. If the series doesn't exist, will delete it.
    /// </summary>
    /// <param name="seriesId"></param>
    /// <param name="bypassFolderOptimizationChecks">Not Used. Scan series will always force</param>
    [Queue(TaskScheduler.ScanQueue)]
    [DisableConcurrentExecution(Timeout)]
    [AutomaticRetry(Attempts = RetryAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ScanSeries(int seriesId, bool bypassFolderOptimizationChecks = true)
    {
        var sw = Stopwatch.StartNew();

        var series = await unitOfWork.SeriesRepository.GetFullSeriesForSeriesIdAsync(seriesId);
        if (series == null) return; // This can occur when UI deletes a series but doesn't update and user re-requests update

        var settings = await unitOfWork.SettingsRepository.GetMetadataSettingDto();
        var serverSettings = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();

        var existingChapterIdsToClean = await unitOfWork.SeriesRepository.GetChapterIdsForSeriesAsync(new[] {seriesId});

        var library = await unitOfWork.LibraryRepository.GetLibraryForIdAsync(series.LibraryId, LibraryIncludes.Folders | LibraryIncludes.FileTypes | LibraryIncludes.ExcludePatterns);
        if (library == null) return;

        var libraryPaths = library.Folders.Select(f => f.Path).ToList();
        if (await ShouldScanSeries(seriesId, library, libraryPaths) != ScanCancelReason.NoCancel)
        {
            BackgroundJob.Enqueue(() => metadataService.GenerateCoversForSeries(serverSettings, series.LibraryId, seriesId, false, false));
            BackgroundJob.Enqueue(() => wordCountAnalyzerService.ScanSeries(library.Id, seriesId, bypassFolderOptimizationChecks));
            return;
        }

        List<string> folderPaths = [];
        if (!string.IsNullOrEmpty(series.LowestFolderPath) && directoryService.Exists(series.LowestFolderPath))
        {
            folderPaths.Add(series.LowestFolderPath);
        }
        else
        {
            // Without a LowestFolderPath the files can sit in several top level folders, and FolderPath is only the first
            var files = await unitOfWork.SeriesRepository.GetFilesForSeriesAsync(seriesId);
            var seriesDirs = directoryService.FindHighestDirectoriesFromFiles(libraryPaths,
                files.Select(f => f.FilePath).ToList());
            if (seriesDirs.Keys.Count == 0)
            {
                logger.LogCritical("Scan Series has files spread outside a main series folder. Defaulting to library folder (this is expensive)");
                await eventHub.SendMessageAsync(MessageFactory.Info, MessageFactory.FilesOutsideFolderEvent(series.LibraryId, series.Id, series.Name));
            }

            folderPaths.AddRange(seriesDirs.Keys);
            if (folderPaths.Count == 0 && !string.IsNullOrEmpty(series.FolderPath) && directoryService.Exists(series.FolderPath))
            {
                folderPaths.Add(series.FolderPath);
            }

            // We should check if folderPath is a library folder path and if so, return early and tell user to correct their setup.
            if (folderPaths.Exists(f => !libraryPaths.Any(f.IsInsideFolder)))
            {
                logger.LogCritical("[ScannerSeries] {SeriesName} scan aborted. Files for series are not in a nested folder under library path. Correct this and rescan", series.Name);
                await eventHub.SendMessageAsync(MessageFactory.Error, MessageFactory.ScanSeriesNotNestedEvent(series.LibraryId, series.Id, series.Name));
                return;
            }
            return;
        }

        if (folderPaths.Count == 0)
        {
            logger.LogCritical("[ScannerSeries] Scan Series could not find a single, valid folder root for files");
            await eventHub.SendMessageAsync(MessageFactory.Error, MessageFactory.ScanSeriesNoRootEvent(series.LibraryId, series.Id, series.Name));
            return;
        }

        var seriesScan = new SeriesScanTargetDto(series.Id, series.Name);
        await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.LibraryScanProgressEvent(library.Id, library.Name, ProgressEventType.Started, series.Name, 1, seriesScan: seriesScan));

        logger.LogInformation("Beginning file scan on {SeriesName}", series.Name);
        var scanStarted = DateTime.Now;
        var (scanElapsedTime, parsedSeries, savedIssues, _) = await ScanFiles(library, folderPaths,
            false, true);

        logger.LogInformation("[ScannerService] Series file scan for {Series} finished in {Time} milliseconds. Parsed series: {ParsedSeriesCount}",
            series.Name, scanElapsedTime, parsedSeries.Count);

        var existingSeriesFormats = (await unitOfWork.SeriesRepository.GetFilesForSeriesAsync(series.Id))
            .Select(f => f.Format)
            .ToHashSet();

        // Remove any parsedSeries keys that don't belong to our series. This can occur when users store 2 series in the same folder.
        // GDS can intentionally merge TXT/EPUB/PDF variants into one logical series, so keep matching existing file formats for later batching.
        RemoveParsedInfosNotForSeries(parsedSeries, series, library.Type, existingSeriesFormats);

        var tally = new ScanTally();

        // If nothing was found, first validate any of the files still exist. If they don't then we have a deletion and can skip the rest of the logic flow
        if (parsedSeries.Count == 0)
        {
             var seriesFiles = (await unitOfWork.SeriesRepository.GetFilesForSeriesAsync(series.Id));
             if (!string.IsNullOrEmpty(series.FolderPath) &&
                 !seriesFiles.Where(f => f.FilePath.IsInsideFolder(series.FolderPath)).Any(m => File.Exists(m.FilePath)))
             {
                 try
                 {
                     unitOfWork.SeriesRepository.Remove(series);
                     await CommitAndSend(1, sw, scanElapsedTime, series);
                     await eventHub.SendMessageAsync(MessageFactory.SeriesRemoved,
                         MessageFactory.SeriesRemovedEvent(seriesId, series.LibraryId), false);
                     tally.SeriesRemoved++;
                 }
                 catch (Exception ex)
                 {
                     logger.LogCritical(ex, "There was an error during ScanSeries to delete the series as no files could be found. Aborting scan");
                     await unitOfWork.RollbackAsync();
                     return;
                 }
             }
             else
             {
                 // I think we should just fail and tell user to fix their setup. This is extremely expensive for an edge case
                 logger.LogCritical("We weren't able to find any files in the series scan, but there should be. Please correct your naming convention or put Series in a dedicated folder. Aborting scan");
                 await eventHub.SendMessageAsync(MessageFactory.Error,
                     MessageFactory.ScanSeriesNoFilesEvent(series.LibraryId, series.Id, series.Name));
                 await unitOfWork.RollbackAsync();
                 await AssignScanIssuesAsync(library, savedIssues, series.Id);
                 return;
             }
        }

        // At this point, parsedSeries will have at least one key then we can perform the update. If it still doesn't, just return and don't do anything
        // Don't allow any processing on files that aren't part of this series
        var toProcess = parsedSeries.Keys.Where(key =>
            key.ExistingSeriesId == series.Id ||
            key.NormalizedName.Equals(series.NormalizedName) ||
            key.NormalizedName.Equals(series.NormalizedOriginalName))
            .ToList();

        var toProcessList = toProcess.Select(k => parsedSeries[k]).ToList();
        var totalCount = toProcessList.Count;
        var seriesLeftToProcess = totalCount;

        foreach (var pSeries in toProcessList)
        {
            using var scope = scopeFactory.CreateScope();
            var processSeries = scope.ServiceProvider.GetRequiredService<IProcessSeries>();
            var unitOfWorkScoped = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var scopedLibrary = (await unitOfWorkScoped.LibraryRepository.GetLibraryForIdAsync(library.Id,
                LibraryIncludes.Folders | LibraryIncludes.FileTypes | LibraryIncludes.ExcludePatterns))!;

            var result = await processSeries.ProcessSeriesAsync(settings, pSeries, new ProcessSeriesArgs
            {
                Library = scopedLibrary,
                LeftToProcess = seriesLeftToProcess,
                TotalToProcess = totalCount,
                ForceUpdate = bypassFolderOptimizationChecks,
                ScanStarted = scanStarted,
                SeriesScan = seriesScan,
            });
            tally.Add(result);

            if (result.SeriesId != null)
            {
                var metadataServiceScoped = scope.ServiceProvider.GetRequiredService<IMetadataService>();
                var wordCountAnalyzerServiceScoped = scope.ServiceProvider.GetRequiredService<IWordCountAnalyzerService>();

                await metadataServiceScoped.GenerateCoversForSeries(serverSettings, scopedLibrary.Id, result.SeriesId.Value, bypassFolderOptimizationChecks, false);
                await wordCountAnalyzerServiceScoped.ScanSeries(scopedLibrary.Id, result.SeriesId.Value, bypassFolderOptimizationChecks);
            }

            seriesLeftToProcess--;
        }

        var issues = await ReportScanIssuesAsync(library, savedIssues, series.Id);

        // Tell UI that this series is done
        await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.LibraryScanEndedEvent(tally.ToEventBody(library, issues), series.Name));

        BackgroundJob.Enqueue(() => cacheService.CleanupChapters(existingChapterIdsToClean));
        if (ShouldRunGlobalPostScanCleanup(library.Type))
        {
            logger.LogInformation("[ScannerService] Starting post-scan cleanup for {SeriesName}", series.Name);
            BackgroundJob.Enqueue<IMetadataService>(service => service.RemoveAbandonedMetadataKeys(CancellationToken.None));
            BackgroundJob.Enqueue(() => directoryService.ClearDirectory(directoryService.CacheDirectory));
        }
        else
        {
            logger.LogInformation("[ScannerService] Skipping global post-scan cleanup for GDS ScanSeries on {SeriesName} ({SeriesId})",
                series.Name, seriesId);
        }

        logger.LogInformation("[ScannerService] Post-scan cleanup enqueued for {SeriesName}. Scan series job completed in {ElapsedScanTime} milliseconds",
            series.Name, sw.ElapsedMilliseconds);
    }

    public static bool ShouldRunWordCountAnalysisAfterScan(LibraryType libraryType)
    {
        return libraryType != LibraryType.GDS;
    }

    public static bool ShouldRunGlobalPostScanCleanup(LibraryType libraryType)
    {
        return libraryType != LibraryType.GDS;
    }

    private async Task<IList<string>> ResolveSeriesScanFolders(Series series, IList<string> libraryPaths)
    {
        var files = await unitOfWork.SeriesRepository.GetFilesForSeriesAsync(series.Id);
        var fileDirectories = files
            .Select(f => directoryService.FileSystem.FileInfo.New(f.FilePath).Directory?.FullName ?? string.Empty)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(Parser.NormalizePath)
            .Distinct()
            .Where(directoryService.Exists)
            .ToList();

        var normalizedLibraryPaths = libraryPaths
            .Select(Parser.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resolvedFolders = ResolveSeriesScanFoldersFromFileDirectories(series.LowestFolderPath ?? series.FolderPath,
            fileDirectories, normalizedLibraryPaths);

        if (fileDirectories.Any(normalizedLibraryPaths.Contains))
        {
            logger.LogCritical(
                "[ScannerSeries] {SeriesName} scan aborted. One or more files are directly under a library root, which would require an expensive broad scan",
                series.Name);
            await eventHub.SendMessageAsync(MessageFactory.Error,
                MessageFactory.ErrorEvent($"{series.Name} scan aborted",
                    "One or more files are directly under a library root. Put files in a dedicated series folder or run a library scan."));
            return [];
        }

        if (resolvedFolders.Count > 0)
        {
            var storedFolder = Parser.NormalizePath(series.LowestFolderPath ?? series.FolderPath ?? string.Empty);
            if (!string.IsNullOrEmpty(storedFolder) && !resolvedFolders.Contains(storedFolder, StringComparer.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "[ScannerSeries] {SeriesName} stored folder path '{StoredFolder}' does not match actual file directories. Scanning {RootCount} file directory roots instead: {Roots}",
                    series.Name, storedFolder, resolvedFolders.Count, string.Join(", ", resolvedFolders));
            }

            if (resolvedFolders.Count > 1)
            {
                logger.LogInformation(
                    "[ScannerSeries] {SeriesName} spans {RootCount} file directory roots. ScanSeries will scan only those exact roots, not the stored common/category folder",
                    series.Name, resolvedFolders.Count);
            }

            return resolvedFolders;
        }

        var fallbackFolder = Parser.NormalizePath(series.LowestFolderPath ?? series.FolderPath ?? string.Empty);
        if (string.IsNullOrEmpty(fallbackFolder) || !directoryService.Exists(fallbackFolder))
        {
            return [];
        }

        if (normalizedLibraryPaths.Contains(fallbackFolder))
        {
            logger.LogCritical(
                "[ScannerSeries] {SeriesName} scan aborted. Stored folder path is a library root and would require an expensive broad scan",
                series.Name);
            await eventHub.SendMessageAsync(MessageFactory.Error,
                MessageFactory.ErrorEvent($"{series.Name} scan aborted",
                    "Stored folder path is a library root. Put files in a dedicated series folder or run a library scan."));
            return [];
        }

        logger.LogWarning(
            "[ScannerSeries] {SeriesName} has no existing MangaFile directories. Falling back to stored folder path: {FolderPath}",
            series.Name, fallbackFolder);
        return [fallbackFolder];
    }

    internal static IList<string> ResolveSeriesScanFoldersFromFileDirectories(string? storedFolderPath,
        IList<string> fileDirectories, ISet<string> normalizedLibraryPaths)
    {
        var normalizedStoredFolder = Parser.NormalizePath(storedFolderPath ?? string.Empty);
        var normalizedFileDirectories = fileDirectories
            .Select(Parser.NormalizePath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedFileDirectories.Count == 0 || normalizedFileDirectories.Any(normalizedLibraryPaths.Contains))
        {
            return [];
        }

        if (string.IsNullOrEmpty(normalizedStoredFolder))
        {
            return normalizedFileDirectories;
        }

        if (normalizedLibraryPaths.Contains(normalizedStoredFolder))
        {
            return normalizedFileDirectories;
        }

        if (normalizedFileDirectories.Count == 1 &&
            normalizedFileDirectories.Contains(normalizedStoredFolder, StringComparer.OrdinalIgnoreCase))
        {
            return [normalizedStoredFolder];
        }

        return normalizedFileDirectories;
    }

    private static Dictionary<ParsedSeries, IList<ParserInfo>> TrackFoundSeriesAndFiles(IList<ScannedSeriesResult> seenSeries,
        IList<SeriesNameMatch> existingSeries)
    {
        var parsedSeries = new Dictionary<ParsedSeries, IList<ParserInfo>>();

        var existingByName = existingSeries
            .SelectMany(s => new[] { s.NormalizedName, s.NormalizedLocalizedName, s.NormalizedOriginalName }
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct()
                .Select(name => (Name: name, Series: s)))
            .ToLookup(x => x.Name, x => x.Series);

        // One series can arrive under several names (once per folder, or a folder named after its LocalizedName).
        // Processed apart, each group deletes the files the other one read
        var seriesAcrossFolders = seenSeries
            .Where(s => s.ParsedInfos.Count > 0)
            .Select(s => (Result: s, SeriesId: FindExistingSeriesId(s, existingByName)))
            .GroupBy(s => s.SeriesId != null
                ? (s.SeriesId, string.Empty, MangaFormat.Unknown)
                : ((int?) null, s.Result.ParsedSeries.NormalizedName, s.Result.ParsedSeries.Format),
                s => s.Result);

        foreach (var series in seriesAcrossFolders)
        {
            var key = series.First().ParsedSeries;
            key.ExistingSeriesId = series.Key.Item1;
            key.HasChanged = series.Any(s => s.HasChanged);
            key.HasMissingFiles = series.Any(s => s.HasMissingFiles);

            if (key.HasChanged)
            {
                parsedSeries.Add(key, [.. series.SelectMany(s => s.ParsedInfos)]);
            }
            else
            {
                parsedSeries.Add(key, []);
            }
        }

        return parsedSeries;
    }

    private static int? FindExistingSeriesId(ScannedSeriesResult result, ILookup<string, SeriesNameMatch> existingByName)
    {
        var names = result.ParsedInfos
            .Select(info => info.LocalizedSeries?.ToNormalized())
            .Prepend(result.ParsedSeries.NormalizedName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct();

        var ids = names
            .SelectMany(name => existingByName[name!])
            .Where(s => s.Format == result.ParsedSeries.Format || s.Format == MangaFormat.Unknown)
            .Select(s => s.Id)
            .Distinct()
            .ToList();

        return ids.Count == 1 ? ids[0] : null;
    }

    private async Task<ScanCancelReason> ShouldScanSeries(int seriesId, Library library, IList<string> libraryPaths)
    {
        var seriesFolderPaths = (await unitOfWork.SeriesRepository.GetFilesForSeriesAsync(seriesId))
            .Select(f => directoryService.FileSystem.FileInfo.New(f.FilePath).Directory?.FullName ?? string.Empty)
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct()
            .ToList();

        if (!await CheckMounts(library.Id, library.Name, seriesFolderPaths))
        {
            logger.LogCritical(
                "Some of the root folders for library are not accessible. Please check that drives are connected and rescan. Scan will be aborted");
            return ScanCancelReason.FolderMount;
        }

        if (!await CheckMounts(library.Id, library.Name, libraryPaths))
        {
            logger.LogCritical(
                "Some of the root folders for library are not accessible. Please check that drives are connected and rescan. Scan will be aborted");
            return ScanCancelReason.FolderMount;
        }

        return ScanCancelReason.NoCancel;
    }

    private void RemoveParsedInfosNotForSeries(Dictionary<ParsedSeries, IList<ParserInfo>> parsedSeries, Series series)
    {
        var keysToRemove = parsedSeries.Keys
            .Where(key => key.ExistingSeriesId != series.Id && !SeriesHelper.FindSeries(series, key))
            .ToList();

        foreach (var key in keysToRemove)
        {
            var fileNames = parsedSeries[key].Select(info => info.Filename).ToList();
            logger.LogTrace("Removing files {FilePaths} for {SeriesName} as no match was found. {@ParsedSeries}. ",
                fileNames, series.Name, key);

            parsedSeries.Remove(key);
        }
    }

    private async Task CommitAndSend(int seriesCount, Stopwatch sw, long scanElapsedTime, Series series)
    {
        if (unitOfWork.HasChanges())
        {
            await unitOfWork.CommitAsync();
            logger.LogInformation(
                "Processed files and {SeriesCount} series in {ElapsedScanTime} milliseconds for {SeriesName}",
                seriesCount, sw.ElapsedMilliseconds + scanElapsedTime, series.Name);
        }
    }

    /// <summary>
    /// Ensure that all library folders are mounted. In the case that any are empty or non-existent, emit an event to the UI via EventHub and return false
    /// </summary>
    /// <param name="libraryId"></param>
    /// <param name="libraryName"></param>
    /// <param name="folders"></param>
    /// <returns></returns>
    private async Task<bool> CheckMounts(int libraryId, string libraryName, IList<string> folders)
    {
        // Check if any of the folder roots are not available (ie disconnected from network, etc) and fail if any of them are
        if (folders.Any(f => !directoryService.IsDriveMounted(f)))
        {
            logger.LogCritical("[ScannerService] Some of the root folders for library ({LibraryName} are not accessible. Please check that drives are connected and rescan. Scan will be aborted", libraryName);
            var unmountedFolders = folders.Where(f => !directoryService.IsDriveMounted(f)).ToArray();

            await eventHub.SendMessageAsync(MessageFactory.Error,
                MessageFactory.RootFoldersInaccessibleEvent(libraryId, libraryName, unmountedFolders));

            return false;
        }

        var unreadableFolders = folders.Where(f => !CanList(f)).Select(Parser.NormalizePath).ToList();
        if (unreadableFolders.Count > 0)
        {
            logger.LogError("[ScannerService] Some of the root folders for library {LibraryName} could not be read. Scan has been aborted", libraryName);
            await eventHub.SendMessageAsync(MessageFactory.Error,
                MessageFactory.UnreadableFoldersEvent(libraryId, libraryName, unreadableFolders));

            return false;
        }

        // For Docker instances check if any of the folder roots are not available (ie disconnected volumes, etc) and fail if any of them are
        if (folders.Any(directoryService.IsDirectoryEmpty))
        {
            // That way logging and UI informing is all in one place with full context
            logger.LogError("[ScannerService] Some of the root folders for the library are empty. " +
                             "Either your mount has been disconnected or you are trying to delete all series in the library. " +
                             "Scan has been aborted. " +
                             "Check that your mount is connected or change the library's root folder and rescan");

            await eventHub.SendMessageAsync(MessageFactory.Error, MessageFactory.RootFoldersEmptyEvent(libraryId, libraryName));

            return false;
        }

        return true;
    }

    private bool CanList(string folder)
    {
        try
        {
            directoryService.IsDirectoryEmpty(folder);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "[ScannerService] Could not list {Folder}", folder);
            return false;
        }
    }

    [Queue(TaskScheduler.ScanQueue)]
    [DisableConcurrentExecution(Timeout)]
    [AutomaticRetry(Attempts = RetryAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ScanLibraries(bool forceUpdate = false)
    {
        logger.LogInformation("[ScannerService] Starting Scan of All Libraries, Forced: {Forced}", forceUpdate);
        foreach (var lib in await unitOfWork.LibraryRepository.GetLibrariesAsync())
        {
            // BUG: This will trigger the first N libraries to scan over and over if there is always an interruption later in the chain
            if (ScanJobQueue.Read().Jobs.HasLibraryScan(lib.Id))
            {
                // We don't need to send SignalR event as this is a background job that user doesn't need insight into
                logger.LogInformation("[ScannerService] Scan library invoked via nightly scan job but a task is already running for {LibraryName}. Rescheduling for 4 hours", lib.Name);
                await Task.Delay(TimeSpan.FromHours(4));
            }

            await ScanLibrary(lib.Id, forceUpdate, true);
        }

        logger.LogInformation("[ScannerService] Scan of All Libraries Finished");
    }


    /// <summary>
    /// Scans a library for file changes.
    /// Will kick off a scheduled background task to refresh metadata,
    /// ie) all entities will be rechecked for new cover images and comicInfo.xml changes
    /// </summary>
    /// <param name="libraryId"></param>
    /// <param name="forceUpdate">Defaults to false</param>
    /// <param name="isSingleScan">Defaults to true. Is this a standalone invocation or is it in a loop?</param>
    [Queue(TaskScheduler.ScanQueue)]
    [DisableConcurrentExecution(Timeout)]
    [AutomaticRetry(Attempts = RetryAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ScanLibrary(int libraryId, bool forceUpdate = false, bool isSingleScan = true)
    {
        var sw = Stopwatch.StartNew();
        var library = await unitOfWork.LibraryRepository.GetLibraryForIdAsync(libraryId,
            LibraryIncludes.Folders | LibraryIncludes.FileTypes | LibraryIncludes.ExcludePatterns);

        var libraryFolderPaths = library!.Folders.Select(fp => fp.Path).ToList();
        if (!await CheckMounts(library.Id, library.Name, libraryFolderPaths)) return;


        // Validations are done, now we can start actual scan
        logger.LogInformation("[ScannerService] Beginning file scan on {LibraryName}", library.Name);

        if (!library.EnableMetadata)
        {
            logger.LogInformation("[ScannerService] Warning! {LibraryName} has metadata turned off", library.Name);
        }

        // This doesn't work for something like M:/Manga/ and a series has library folder as root
        var shouldUseLibraryScan = !(await unitOfWork.LibraryRepository.DoAnySeriesFoldersMatch(libraryFolderPaths));
        if (!shouldUseLibraryScan)
        {
            logger.LogError("[ScannerService] Library {LibraryName} consists of one or more Series folders as a library root, using series scan", library.Name);
        }


        logger.LogDebug("[ScannerService] Library {LibraryName} Step 1: Scan & Parse Files", library.Name);
        var scanStarted = DateTime.Now;
        var (scanElapsedTime, parsedSeries, savedIssues, unreadableFolders) = await ScanFiles(library, libraryFolderPaths,
            shouldUseLibraryScan, forceUpdate);

        // We need to remove any keys where there is no actual parser info
        logger.LogDebug("[ScannerService] Library {LibraryName} Step 2: Process and Update Database", library.Name);
        var tally = await ProcessParsedSeries(forceUpdate, parsedSeries, library, scanElapsedTime, scanStarted);

        UpdateLastScanned(library);
        unitOfWork.LibraryRepository.Update(library);

        logger.LogDebug("[ScannerService] Library {LibraryName} Step 3: Save Library", library.Name);
        if (await unitOfWork.CommitAsync())
        {
            logger.LogDebug("[ScannerService] Library {LibraryName} Step 5: Remove Deleted Series", library.Name);
            tally.SeriesRemoved += await RemoveSeriesNotFound(parsedSeries, library);

            if (!tally.HasChanges)
            {
                logger.LogInformation(
                    "[ScannerService] Finished library scan of {ParsedSeriesCount} series in {ElapsedScanTime} milliseconds for {LibraryName}. There were no changes",
                    parsedSeries.Count, sw.ElapsedMilliseconds, library.Name);
            }
            else
            {
                logger.LogInformation(
                    "[ScannerService] Finished library scan of {TotalFiles} files and {ParsedSeriesCount} series in {ElapsedScanTime} milliseconds for {LibraryName}: " +
                    "{SeriesAdded} series added, {SeriesRemoved} removed, {ChaptersAdded} chapters added, {ChaptersUpdated} updated, {ChaptersRemoved} removed",
                    tally.TotalFiles, parsedSeries.Count, sw.ElapsedMilliseconds, library.Name,
                    tally.SeriesAdded, tally.SeriesRemoved, tally.ChaptersAdded, tally.ChaptersUpdated, tally.ChaptersRemoved);
            }
        }
        else
        {
            logger.LogCritical(
                "[ScannerService] There was a critical error that resulted in a failed scan. Please check logs and rescan");
        }

        var issues = await ReportScanIssuesAsync(library, savedIssues);

        if (unreadableFolders.Count > 0)
        {
            await eventHub.SendMessageAsync(MessageFactory.Error,
                MessageFactory.UnreadableFoldersEvent(library.Id, library.Name, unreadableFolders));
        }

        await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.LibraryScanEndedEvent(tally.ToEventBody(library, issues)));
        await metadataService.RemoveAbandonedMetadataKeys();

        BackgroundJob.Enqueue(() => directoryService.ClearDirectory(directoryService.CacheDirectory));
        logger.LogInformation("[ScannerService] Scan library job completed for {LibraryName} in {ElapsedScanTime} milliseconds",
            library.Name, sw.ElapsedMilliseconds);
    }

    private async Task ScanGdsLibraryLowMemory(Library library, IList<string> libraryFolderPaths,
        bool shouldUseLibraryScan, bool forceFileSystemScan, Stopwatch sw)
    {
        try
        {
            var (scanElapsedTime, scannedSeries) = await ScanGdsFiles(library, libraryFolderPaths,
                shouldUseLibraryScan, forceFileSystemScan);
            var foundSeriesCount = scannedSeries.Count;
            var foundSeriesNames = scannedSeries
                .Select(series => series.ParsedSeries.NormalizedName)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToHashSet(StringComparer.Ordinal);

            logger.LogDebug("[ScannerService] Library {LibraryName} Step 2: Process and Update Database", library.Name);
            var totalFiles = await ProcessGdsSeriesPlan(forceFileSystemScan, scannedSeries, library, scanElapsedTime);

            UpdateLastScanned(library);
            unitOfWork.LibraryRepository.Update(library);

            logger.LogDebug("[ScannerService] Library {LibraryName} Step 3: Save Library", library.Name);
            if (await unitOfWork.CommitAsync())
            {
                if (totalFiles == 0)
                {
                    logger.LogInformation(
                        "[ScannerService] Finished library scan of {ParsedSeriesCount} series in {ElapsedScanTime} milliseconds for {LibraryName}. There were no changes",
                        foundSeriesCount, sw.ElapsedMilliseconds, library.Name);
                }
                else
                {
                    logger.LogInformation(
                        "[ScannerService] Finished library scan of {TotalFiles} files and {ParsedSeriesCount} series in {ElapsedScanTime} milliseconds for {LibraryName}",
                        totalFiles, foundSeriesCount, sw.ElapsedMilliseconds, library.Name);
                }

                logger.LogInformation("[ScannerService] GDS post-scan delete reconciliation starting for {LibraryName}",
                    library.Name);
                await RemoveGdsSeriesNotFound(foundSeriesNames, library);
                logger.LogInformation("[ScannerService] GDS post-scan delete reconciliation finished for {LibraryName}",
                    library.Name);
            }
            else
            {
                logger.LogCritical(
                    "[ScannerService] There was a critical error that resulted in a failed scan. Please check logs and rescan");
            }

            await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.LibraryScanProgressEvent(library.Name, ProgressEventType.Ended, string.Empty));
            logger.LogInformation(
                "[ScannerService] Skipping synchronous abandoned metadata cleanup after GDS scan for {LibraryName}; scheduled cleanup can handle orphaned metadata",
                library.Name);

            BackgroundJob.Enqueue(() => directoryService.ClearDirectory(directoryService.CacheDirectory));
            logger.LogInformation("[ScannerService] Scan library job completed for {LibraryName} in {ElapsedScanTime} milliseconds",
                library.Name, sw.ElapsedMilliseconds);
        }
        finally
        {
            GdsMetadataParser.ClearCache();
        }
    }

    private async Task RemoveGdsSeriesNotFound(ISet<string> seenNormalizedNames, Library library)
    {
        try
        {
            logger.LogDebug("[ScannerService] Removing GDS series that were not found during the scan");

            var removedSeries = await unitOfWork.SeriesRepository.RemoveGdsSeriesNotInNormalizedNamesAsync(
                seenNormalizedNames, library.Id);
            logger.LogDebug("[ScannerService] Found {Count} GDS series to remove: {SeriesList}",
                removedSeries.Count, string.Join(", ", removedSeries.Select(s => s.Name)));

            await unitOfWork.CommitAsync();

            foreach (var series in removedSeries)
            {
                await eventHub.SendMessageAsync(
                    MessageFactory.SeriesRemoved,
                    MessageFactory.SeriesRemovedEvent(series.Id, series.Name, series.LibraryId),
                    false
                );
            }

            logger.LogDebug("[ScannerService] GDS series removal process completed");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "[ScannerService] Error during GDS series cleanup. Please check logs and rescan");
        }
    }

    /// <returns>How many series were removed</returns>
    private async Task<int> RemoveSeriesNotFound(Dictionary<ParsedSeries, IList<ParserInfo>> parsedSeries, Library library)
    {
        try
        {
            logger.LogDebug("[ScannerService] Removing series that were not found during the scan");

            var removedSeries = await unitOfWork.SeriesRepository.RemoveSeriesNotInListAsync(parsedSeries, library.Id);
            logger.LogDebug("[ScannerService] Found {Count} series to remove: {SeriesList}",
                removedSeries.Count, string.Join(", ", removedSeries.Select(s => s.Name)));

            // Commit the changes
            await unitOfWork.CommitAsync();

            // Notify for each removed series
            foreach (var series in removedSeries)
            {
                await eventHub.SendMessageAsync(
                    MessageFactory.SeriesRemoved,
                    MessageFactory.SeriesRemovedEvent(series.Id, series.LibraryId),
                    false
                );
            }

            logger.LogDebug("[ScannerService] Series removal process completed");
            return removedSeries.Count;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "[ScannerService] Error during series cleanup. Please check logs and rescan");
            return 0;
        }
    }

    private async Task<ScanTally> ProcessParsedSeries(bool forceUpdate, Dictionary<ParsedSeries, IList<ParserInfo>> parsedSeries, Library library,
        long scanElapsedTime, DateTime scanStarted)
    {
        if (library.Type == LibraryType.GDS)
        {
            return await ProcessParsedGdsSeries(forceUpdate, parsedSeries, library, scanElapsedTime);
        }

        // Iterate over the dictionary and remove only the ParserInfos that don't need processing
        var toProcess = new Dictionary<ParsedSeries, IList<ParserInfo>>();
        var scanSw = Stopwatch.StartNew();

        var settings = await unitOfWork.SettingsRepository.GetMetadataSettingDto();

        foreach (var series in parsedSeries)
        {
            if (!series.Key.HasChanged)
            {
                logger.LogDebug("{Series} hasn't changed", series.Key.Name);
                continue;
            }

            if (series.Key.HasMissingFiles || series.Value.Any(info => !string.IsNullOrEmpty(info.Filename)))
            {
                toProcess[series.Key] = series.Value.Where(info => !string.IsNullOrEmpty(info.Filename) || !string.IsNullOrEmpty(info.UnchangedFolderPath)).ToList();
            }
        }

        if (toProcess.Count > 0)
        {
            // For all Genres in the ParserInfos, do a bulk check against the DB on what is not in the DB and create them
            // This will ensure all Genres are pre-created and allow our Genre lookup (and Priming) to be much simpler. It will be slower, but more consistent.
            var allGenres = toProcess
                .SelectMany(s => s.Value
                    .SelectMany(p => p.ComicInfo?.Genre?
                                         .Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                     ?? []))
                .Distinct()
                .ToList();

            var allTags = toProcess
                .SelectMany(s => s.Value
                    .SelectMany(p => p.ComicInfo?.Tags?
                                         .Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                     ?? []))
                .Distinct()
                .ToList();

            ExternalMetadataService.GenerateExternalGenreAndTagsList(allGenres, allTags, settings,
                out var processedTags, out var processedGenres);

            await CreateAllGenresAsync(processedGenres);
            await CreateAllTagsAsync(processedTags);
        }

        logger.LogInformation("[ScannerService] Found {SeriesCount} Series that need processing in {Time} ms", toProcess.Count, scanSw.ElapsedMilliseconds + scanElapsedTime);

        var tally = await ProcessParserInfo(settings, toProcess.Values.ToList(), library, forceUpdate, scanStarted);

        logger.LogInformation("[ScannerService] Finished scan in {ScanAndUpdateTime} milliseconds.", scanSw.ElapsedMilliseconds + scanElapsedTime);

        return tally;
    }

    private async Task<int> ProcessParsedGdsSeries(bool forceUpdate, Dictionary<ParsedSeries, IList<ParserInfo>> parsedSeries,
        Library library, long scanElapsedTime)
    {
        var scanSw = Stopwatch.StartNew();
        var settings = await unitOfWork.SettingsRepository.GetMetadataSettingDto();
        var seriesKeys = parsedSeries.Keys.ToList();
        var keysToProcess = new List<ParsedSeries>();
        Dictionary<string, SeriesScanFingerprintInfo> gdsFingerprintInfo = [];

        if (!forceUpdate)
        {
            gdsFingerprintInfo = (await unitOfWork.SeriesRepository.GetGdsScanFingerprintInfoAsync(library.Id))
                .GroupBy(info => GdsScanFingerprintHelper.BuildKey(info.NormalizedName, info.Format))
                .Where(group => group.Count() == 1)
                .ToDictionary(
                    group => group.Key,
                    group => group.Single());
        }

        foreach (var seriesKey in seriesKeys)
        {
            if (!parsedSeries.TryGetValue(seriesKey, out var parserInfos))
            {
                continue;
            }

            if (!seriesKey.HasChanged)
            {
                logger.LogDebug("{Series} hasn't changed", seriesKey.Name);
                parsedSeries[seriesKey] = [];
                continue;
            }

            var validInfos = parserInfos.Where(info => !string.IsNullOrEmpty(info.Filename)).ToList();
            if (validInfos.Count == 0)
            {
                parsedSeries[seriesKey] = [];
                continue;
            }

            if (!forceUpdate &&
                gdsFingerprintInfo.TryGetValue(
                    GdsScanFingerprintHelper.BuildKey(seriesKey.NormalizedName, seriesKey.Format),
                    out var existingFingerprint))
            {
                var currentFingerprint = GdsScanFingerprintHelper.Calculate(validInfos);
                if (existingFingerprint.GdsScanFingerprintVersion == GdsScanFingerprintHelper.FingerprintVersion &&
                    string.Equals(existingFingerprint.GdsScanFingerprint, currentFingerprint.Fingerprint,
                        StringComparison.Ordinal))
                {
                    logger.LogDebug("[ScannerService] Skipping unchanged GDS series {SeriesName} by scan fingerprint", seriesKey.Name);
                    parsedSeries[seriesKey] = [];
                    continue;
                }
            }

            parsedSeries[seriesKey] = validInfos;
            keysToProcess.Add(seriesKey);
        }

        logger.LogInformation("[ScannerService] Found {SeriesCount} Series that need processing in {Time} ms",
            keysToProcess.Count, scanSw.ElapsedMilliseconds + scanElapsedTime);

        seriesKeys.Clear();
        gdsFingerprintInfo.Clear();

        var totalFiles = await ProcessGdsParserInfoByKey(settings, parsedSeries, keysToProcess, library, forceUpdate);

        logger.LogInformation("[ScannerService] Finished scan in {ScanAndUpdateTime} milliseconds.",
            scanSw.ElapsedMilliseconds + scanElapsedTime);

        return totalFiles;
    }

    private async Task<int> ProcessGdsSeriesPlan(bool forceUpdate, IList<GdsScannedSeriesResult> scannedSeries,
        Library library, long scanElapsedTime)
    {
        var scanSw = Stopwatch.StartNew();
        var settings = await unitOfWork.SettingsRepository.GetMetadataSettingDto();
        var seriesIndexesToProcess = new List<int>();
        var representativeCoverSeriesIds = new List<int>();
        var fullCoverSeriesIds = new List<int>();
        var forceCoverGenerationBySeriesKey = new Dictionary<string, bool>(StringComparer.Ordinal);
        Dictionary<string, SeriesScanFingerprintInfo> gdsFingerprintInfo = [];

        if (!forceUpdate)
        {
            gdsFingerprintInfo = (await unitOfWork.SeriesRepository.GetGdsScanFingerprintInfoAsync(library.Id))
                .GroupBy(info => GdsScanFingerprintHelper.BuildKey(info.NormalizedName, info.Format))
                .Where(group => group.Count() == 1)
                .ToDictionary(
                    group => group.Key,
                    group => group.Single());
        }

        for (var seriesIndex = 0; seriesIndex < scannedSeries.Count; seriesIndex++)
        {
            var series = scannedSeries[seriesIndex];
            if (!series.HasChanged || series.Files.Count == 0)
            {
                series.Files = [];
                continue;
            }

            if (!forceUpdate &&
                gdsFingerprintInfo.TryGetValue(
                    GdsScanFingerprintHelper.BuildKey(series.ParsedSeries.NormalizedName, series.ParsedSeries.Format),
                    out var existingFingerprint))
            {
                var seriesKey = GdsScanFingerprintHelper.BuildKey(series.ParsedSeries.NormalizedName,
                    series.ParsedSeries.Format);
                var fingerprintInfos = BuildGdsFingerprintParserInfos(series);
                var currentFingerprint = GdsScanFingerprintHelper.Calculate(fingerprintInfos);
                var hasSidecars = GdsScanFingerprintHelper.HasAnySidecars(fingerprintInfos);

                if (existingFingerprint.GdsScanFingerprintVersion == GdsScanFingerprintHelper.FingerprintVersion &&
                    string.Equals(existingFingerprint.GdsScanFingerprint, currentFingerprint.Fingerprint,
                        StringComparison.Ordinal))
                {
                    if (existingFingerprint.HasMissingNestedCoverImages)
                    {
                        fullCoverSeriesIds.Add(existingFingerprint.SeriesId);
                        logger.LogInformation(
                            "[ScannerService] GDS series {SeriesName} matched scan fingerprint but has missing volume/chapter covers; scheduling cover completion without DB/scan processing",
                            series.ParsedSeries.Name);
                    }
                    else if (existingFingerprint.NeedsSeriesCoverImage)
                    {
                        representativeCoverSeriesIds.Add(existingFingerprint.SeriesId);
                        logger.LogInformation(
                            "[ScannerService] GDS series {SeriesName} matched scan fingerprint but has no series cover; scheduling representative cover generation without DB/scan processing",
                            series.ParsedSeries.Name);
                    }
                    else
                    {
                        logger.LogDebug("[ScannerService] Skipping unchanged GDS series {SeriesName} by scan fingerprint",
                            series.ParsedSeries.Name);
                        series.Files = [];
                        continue;
                    }

                    series.Files = [];
                    continue;
                }

                if (await TryBackfillUnchangedGdsFingerprint(library, existingFingerprint, fingerprintInfos,
                        currentFingerprint))
                {
                    if (existingFingerprint.HasMissingNestedCoverImages)
                    {
                        fullCoverSeriesIds.Add(existingFingerprint.SeriesId);
                    }
                    else if (existingFingerprint.NeedsSeriesCoverImage)
                    {
                        representativeCoverSeriesIds.Add(existingFingerprint.SeriesId);
                    }

                    logger.LogInformation(
                        "[ScannerService] Backfilled GDS scan fingerprint for unchanged existing series {SeriesName}; skipping DB/cover processing",
                        series.ParsedSeries.Name);
                    series.Files = [];
                    continue;
                }

                forceCoverGenerationBySeriesKey[seriesKey] = hasSidecars;
            }

            seriesIndexesToProcess.Add(seriesIndex);
        }

        logger.LogInformation("[ScannerService] Found {SeriesCount} Series that need processing in {Time} ms",
            seriesIndexesToProcess.Count, scanSw.ElapsedMilliseconds + scanElapsedTime);

        gdsFingerprintInfo.Clear();

        var serverSettings = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();

        foreach (var seriesId in representativeCoverSeriesIds.Distinct())
        {
            using var scope = scopeFactory.CreateScope();
            var scopedMetadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();

            await scopedMetadataService.GenerateRepresentativeCoverForSeries(serverSettings, library.Id,
                seriesId, false, false);
        }

        foreach (var seriesId in fullCoverSeriesIds.Distinct())
        {
            using var scope = scopeFactory.CreateScope();
            var scopedMetadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();

            await scopedMetadataService.GenerateCoversForSeries(serverSettings, library.Id,
                seriesId, false, false);
        }

        var seriesPaths = await unitOfWork.SeriesRepository.GetFolderPathMapAsync(library.Id);
        var parser = new ParseScannedFiles(logger, directoryService, readingItemService, eventHub, mediaErrorService);
        var totalFiles = 0;
        var processedSeriesCount = 0;
        var totalSeriesToProcess = seriesIndexesToProcess.Count;

        logger.LogInformation(
            "[ScannerService] Using low-memory reparsing GDS scan path for {LibraryName}. Series to process: {SeriesCount}",
            library.Name, totalSeriesToProcess);
        LogGdsMemoryCheckpoint(library.Name, processedSeriesCount, totalSeriesToProcess);

        foreach (var seriesIndex in seriesIndexesToProcess)
        {
            var series = scannedSeries[seriesIndex];
            IList<ParserInfo> parsedInfos = [];
            try
            {
                parsedInfos = await parser.ParseGdsSeriesFiles(library, series.Files, seriesPaths);
                if (parsedInfos.Count == 0)
                {
                    continue;
                }

                totalFiles += parsedInfos.Count;
                var forceCoverGeneration = forceUpdate;
                var seriesKey = GdsScanFingerprintHelper.BuildKey(series.ParsedSeries.NormalizedName,
                    series.ParsedSeries.Format);
                if (forceCoverGenerationBySeriesKey.TryGetValue(seriesKey, out var forceCoversForSeries))
                {
                    forceCoverGeneration = forceCoverGeneration || forceCoversForSeries;
                }

                int? seriesId;
                using (var scope = scopeFactory.CreateScope())
                {
                    var scopedUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var processSeries = scope.ServiceProvider.GetRequiredService<IProcessSeries>();

                    var scopedLibrary = (await scopedUnitOfWork.LibraryRepository.GetLibraryForIdAsync(library.Id,
                        LibraryIncludes.Folders | LibraryIncludes.FileTypes | LibraryIncludes.ExcludePatterns))!;

                    seriesId = await processSeries.ProcessSeriesAsync(settings, parsedInfos, new ProcessSeriesArgs
                    {
                        Library = scopedLibrary,
                        LeftToProcess = totalSeriesToProcess - processedSeriesCount,
                        TotalToProcess = totalSeriesToProcess,
                        ForceUpdate = forceUpdate,
                    });
                }

                if (seriesId != null)
                {
                    using var scope = scopeFactory.CreateScope();
                    var scopedMetadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();

                    await scopedMetadataService.GenerateCoversForSeries(serverSettings, library.Id,
                        seriesId.Value, forceCoverGeneration, false);
                }
            }
            finally
            {
                series.Files = [];
                parsedInfos = [];
            }

            processedSeriesCount++;
            if (processedSeriesCount % 25 == 0)
            {
                LogGdsMemoryCheckpoint(library.Name, processedSeriesCount, totalSeriesToProcess);
            }
        }

        LogGdsMemoryCheckpoint(library.Name, processedSeriesCount, totalSeriesToProcess);

        logger.LogInformation("[ScannerService] Finished scan in {ScanAndUpdateTime} milliseconds.",
            scanSw.ElapsedMilliseconds + scanElapsedTime);

        return totalFiles;
    }

    private static IList<ParserInfo> BuildGdsFingerprintParserInfos(GdsScannedSeriesResult series)
    {
        return series.Files.Select(file => new ParserInfo
        {
            Series = series.ParsedSeries.Name,
            Format = Parser.ParseFormat(file.FilePath),
            FullFilePath = file.FilePath
        }).ToList();
    }

    private async Task<bool> TryBackfillUnchangedGdsFingerprint(Library library,
        SeriesScanFingerprintInfo existingFingerprint, IList<ParserInfo> parserInfos,
        GdsScanFingerprintState currentFingerprint)
    {
        if (existingFingerprint.SeriesId <= 0) return false;
        if (existingFingerprint.GdsScanFingerprintVersion == GdsScanFingerprintHelper.FingerprintVersion &&
            !string.IsNullOrWhiteSpace(existingFingerprint.GdsScanFingerprint))
        {
            return false;
        }

        if (GdsScanFingerprintHelper.HasAnySidecars(parserInfos)) return false;

        var dbFiles = await unitOfWork.SeriesRepository.GetFilesForSeriesAsync(existingFingerprint.SeriesId);
        if (!MangaFilesMatchCurrentFileSystem(dbFiles, parserInfos)) return false;

        var series = await unitOfWork.DataContext.Series
            .FirstOrDefaultAsync(s => s.Id == existingFingerprint.SeriesId && s.LibraryId == library.Id);
        if (series == null) return false;

        series.GdsScanFingerprint = currentFingerprint.Fingerprint;
        series.GdsScanFingerprintVersion = GdsScanFingerprintHelper.FingerprintVersion;
        series.UpdateLastFolderScanned();

        await unitOfWork.CommitAsync();
        return true;
    }

    private async Task<bool> GdsSeriesHasNewOrChangedFilePathsOrSizes(int seriesId, IList<ParserInfo> parserInfos)
    {
        if (seriesId <= 0) return true;

        var dbFiles = await unitOfWork.SeriesRepository.GetFilesForSeriesAsync(seriesId);
        if (dbFiles.Count != parserInfos.Count) return true;

        var dbFileMap = dbFiles
            .GroupBy(file => Parser.NormalizePath(file.FilePath))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        if (dbFileMap.Count != dbFiles.Count) return true;

        foreach (var info in parserInfos)
        {
            var normalizedPath = Parser.NormalizePath(info.FullFilePath);
            if (!dbFileMap.TryGetValue(normalizedPath, out var dbFile)) return true;
            if (dbFile.Format != info.Format) return true;

            try
            {
                var fileInfo = new FileInfo(normalizedPath);
                if (!fileInfo.Exists || dbFile.Bytes != fileInfo.Length) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> GdsSeriesHasExistingCover(int seriesId)
    {
        if (seriesId <= 0) return false;

        return await unitOfWork.DataContext.Series
            .AsNoTracking()
            .Where(series => series.Id == seriesId)
            .AnyAsync(series => !string.IsNullOrWhiteSpace(series.CoverImage));
    }

    private static bool MangaFilesMatchCurrentFileSystem(IList<MangaFile> dbFiles, IList<ParserInfo> parserInfos)
    {
        if (dbFiles.Count != parserInfos.Count) return false;

        var dbFileMap = dbFiles
            .GroupBy(file => Parser.NormalizePath(file.FilePath))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        if (dbFileMap.Count != dbFiles.Count) return false;

        foreach (var info in parserInfos)
        {
            var normalizedPath = Parser.NormalizePath(info.FullFilePath);
            if (!dbFileMap.TryGetValue(normalizedPath, out var dbFile)) return false;
            if (dbFile.Format != info.Format) return false;

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(normalizedPath);
                if (!fileInfo.Exists) return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }

            if (dbFile.Bytes != fileInfo.Length) return false;
            if (!IsSameFileTime(dbFile.LastModifiedUtc, fileInfo.LastWriteTimeUtc)) return false;
            if (!IsSameFileTime(dbFile.FileCreatedUtc, fileInfo.CreationTimeUtc)) return false;
        }

        return true;
    }

    private static bool IsSameFileTime(DateTime left, DateTime right)
    {
        if (left == DateTime.MinValue || right == DateTime.MinValue) return false;
        return Math.Abs((left.ToUniversalTime() - right.ToUniversalTime()).TotalSeconds) <= 2;
    }

    /// <summary>
    /// Runs metadata updates (database heavy) and extra tasks (I/O heavy) in parallel
    /// </summary>
    /// <param name="settings"></param>
    /// <param name="toProcess"></param>
    /// <param name="library"></param>
    /// <param name="forceUpdate"></param>
    /// <param name="scanStarted"></param>
    private async Task<ScanTally> ProcessParserInfo(MetadataSettingsDto settings, IList<IList<ParserInfo>> toProcess, Library library,
        bool forceUpdate, DateTime scanStarted)
    {
        if (library.Type == LibraryType.GDS)
        {
            return await ProcessParserInfoSequential(settings, toProcess, library, forceUpdate);
        }

        var channel = Channel.CreateUnbounded<int>();

        var serverSettings = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();

        var dbTask = Task.Run(async () => await DbMetadataTask(channel, settings, toProcess, library.Id, library.Name, forceUpdate, scanStarted));

        var amountOfProcessors = Environment.ProcessorCount;
        var usingCount = Math.Max(1, amountOfProcessors / 2);
        logger.LogDebug("[ScannerService] Going to use {Cores} / {TotalCores} threads for I/O tasks this scan",
            usingCount, amountOfProcessors);

        IList<Task<long>> tasks = [];
        for (var i = 0; i < usingCount; i++)
        {
            tasks.Add(Task.Run(async () => await ExtraWorkTask(channel, serverSettings, library.Id, forceUpdate)));
        }

        await Task.WhenAll(tasks.Append<Task>(dbTask));

        var totalIoTime = tasks.Select(t => t.Result).Sum();
        var avgTimePerThread = totalIoTime / usingCount;
        logger.LogDebug("[ScannerService] Spend {Elapsed}ms processing covers & word count, {Average}ms per thread",
            totalIoTime, avgTimePerThread);

        return dbTask.Result;
    }

    /// <summary>
    /// GDS libraries can contain very large remote/rclone-backed sets. Processing DB updates,
    /// and cover extraction in parallel can push RSS high enough for OOM. Keep GDS scans
    /// sequential so each series releases archive/EPUB/image resources before the next series
    /// starts. Word-count analysis is intentionally skipped here; it can re-read large remote
    /// EPUBs for minutes per series and is not required for scan/import/cover regeneration.
    /// </summary>
    private async Task<int> ProcessParserInfoSequential(MetadataSettingsDto settings, IList<IList<ParserInfo>> toProcess,
        Library library, bool forceUpdate)
    {
        var sw = Stopwatch.StartNew();
        var serverSettings = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();
        var totalFiles = 0;
        var seriesLeftToProcess = toProcess.Count;
        var totalSeriesToProcess = toProcess.Count;
        var processedSeriesCount = 0;

        logger.LogInformation(
            "[ScannerService] Using low-memory sequential GDS scan path for {LibraryName}. Series to process: {SeriesCount}",
            library.Name, totalSeriesToProcess);

        foreach (var pSeries in toProcess)
        {
            totalFiles += pSeries.Count;

            int? seriesId;
            using (var scope = scopeFactory.CreateScope())
            {
                var scopedUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var processSeries = scope.ServiceProvider.GetRequiredService<IProcessSeries>();

                var scopedLibrary = (await scopedUnitOfWork.LibraryRepository.GetLibraryForIdAsync(library.Id,
                    LibraryIncludes.Folders | LibraryIncludes.FileTypes | LibraryIncludes.ExcludePatterns))!;

                seriesId = await processSeries.ProcessSeriesAsync(settings, pSeries, new ProcessSeriesArgs
                {
                    Library = scopedLibrary,
                    LeftToProcess = seriesLeftToProcess,
                    TotalToProcess = totalSeriesToProcess,
                    ForceUpdate = forceUpdate,
                });
            }

            if (seriesId != null)
            {
                using var scope = scopeFactory.CreateScope();
                var scopedMetadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();

                await scopedMetadataService.GenerateCoversForSeries(serverSettings, library.Id, seriesId.Value, false, false);
            }

            processedSeriesCount++;
            seriesLeftToProcess--;

            if (processedSeriesCount % 25 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                logger.LogDebug("[ScannerService] Low-memory GDS scan processed {Processed}/{Total} series for {LibraryName}",
                    processedSeriesCount, totalSeriesToProcess, library.Name);
            }
        }

        await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.LibraryScanProgressEvent(library.Name, ProgressEventType.Ended));

        logger.LogDebug("[ScannerService] Finished low-memory GDS scan path for {Count} series in {Elapsed}ms",
            toProcess.Count, sw.ElapsedMilliseconds);

        return totalFiles;
    }

    private async Task<int> ProcessGdsParserInfoByKey(MetadataSettingsDto settings,
        Dictionary<ParsedSeries, IList<ParserInfo>> parsedSeries, IList<ParsedSeries> keysToProcess,
        Library library, bool forceUpdate)
    {
        var sw = Stopwatch.StartNew();
        var serverSettings = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();
        var totalFiles = 0;
        var seriesLeftToProcess = keysToProcess.Count;
        var totalSeriesToProcess = keysToProcess.Count;
        var processedSeriesCount = 0;

        logger.LogInformation(
            "[ScannerService] Using low-memory sequential GDS scan path for {LibraryName}. Series to process: {SeriesCount}",
            library.Name, totalSeriesToProcess);
        LogGdsMemoryCheckpoint(library.Name, processedSeriesCount, totalSeriesToProcess);

        foreach (var seriesKey in keysToProcess)
        {
            IList<ParserInfo> pSeries = [];
            try
            {
                if (!parsedSeries.TryGetValue(seriesKey, out var parserInfos))
                {
                    continue;
                }

                pSeries = parserInfos.Any(info => string.IsNullOrEmpty(info.Filename))
                    ? parserInfos.Where(info => !string.IsNullOrEmpty(info.Filename)).ToList()
                    : parserInfos;
                if (pSeries.Count == 0)
                {
                    continue;
                }

                totalFiles += pSeries.Count;

                int? seriesId;
                using (var scope = scopeFactory.CreateScope())
                {
                    var scopedUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var processSeries = scope.ServiceProvider.GetRequiredService<IProcessSeries>();

                    var scopedLibrary = (await scopedUnitOfWork.LibraryRepository.GetLibraryForIdAsync(library.Id,
                        LibraryIncludes.Folders | LibraryIncludes.FileTypes | LibraryIncludes.ExcludePatterns))!;

                    seriesId = await processSeries.ProcessSeriesAsync(settings, pSeries, new ProcessSeriesArgs
                    {
                        Library = scopedLibrary,
                        LeftToProcess = seriesLeftToProcess,
                        TotalToProcess = totalSeriesToProcess,
                        ForceUpdate = forceUpdate,
                    });
                }

                if (seriesId != null)
                {
                    using var scope = scopeFactory.CreateScope();
                    var scopedMetadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();

                    await scopedMetadataService.GenerateCoversForSeries(serverSettings, library.Id, seriesId.Value, false, false);
                }
            }
            finally
            {
                parsedSeries[seriesKey] = [];
                pSeries = [];
            }

            processedSeriesCount++;
            seriesLeftToProcess--;

            if (processedSeriesCount % 25 == 0)
            {
                LogGdsMemoryCheckpoint(library.Name, processedSeriesCount, totalSeriesToProcess);
            }
        }

        LogGdsMemoryCheckpoint(library.Name, processedSeriesCount, totalSeriesToProcess);

        await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.LibraryScanProgressEvent(library.Name, ProgressEventType.Ended));

        logger.LogDebug("[ScannerService] Finished low-memory GDS scan path for {Count} series in {Elapsed}ms",
            keysToProcess.Count, sw.ElapsedMilliseconds);

        return totalFiles;
    }

    private void LogGdsMemoryCheckpoint(string libraryName, int processedSeriesCount, int totalSeriesToProcess)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        logger.LogInformation(
            "[ScannerService] Low-memory GDS checkpoint {Processed}/{Total} series for {LibraryName}: managed={ManagedMemoryMb}MB, workingSet={WorkingSetMb}MB, private={PrivateMemoryMb}MB",
            processedSeriesCount,
            totalSeriesToProcess,
            libraryName,
            ToMegabytes(managedBytes),
            ToMegabytes(process.WorkingSet64),
            ToMegabytes(process.PrivateMemorySize64));
    }

    private static long ToMegabytes(long bytes)
    {
        return bytes / 1024 / 1024;
    }

    /// <summary>
    /// A thread handling cover generation and word count. Completes when the channel completes
    /// </summary>
    /// <param name="channel"></param>
    /// <param name="serverSettings"></param>
    /// <param name="libraryId"></param>
    /// <param name="forceUpdate"></param>
    /// <returns></returns>
    private async Task<long> ExtraWorkTask(Channel<int> channel, ServerSettingDto serverSettings, int libraryId, bool forceUpdate)
    {
        var sw = Stopwatch.StartNew();

        await foreach (var seriesId in channel.Reader.ReadAllAsync())
        {
            using var scope = scopeFactory.CreateScope();
            var metadataServiceScoped = scope.ServiceProvider.GetRequiredService<IMetadataService>();
            var wordCountAnalyzerServiceScoped = scope.ServiceProvider.GetRequiredService<IWordCountAnalyzerService>();

            await metadataServiceScoped.GenerateCoversForSeries(serverSettings, libraryId, seriesId, false, false);
            await wordCountAnalyzerServiceScoped.ScanSeries(libraryId, seriesId, forceUpdate);
        }

        return sw.ElapsedMilliseconds;
    }

    /// <summary>
    /// Processes all founds series sequentially, and writes the seriesIds to the channel afterwards
    /// </summary>
    /// <param name="channel"></param>
    /// <param name="settings"></param>
    /// <param name="toProcess"></param>
    /// <param name="libraryId"></param>
    /// <param name="libraryName"></param>
    /// <param name="forceUpdate"></param>
    /// <param name="scanStarted"></param>
    private async Task<ScanTally> DbMetadataTask(Channel<int> channel, MetadataSettingsDto settings,
        IList<IList<ParserInfo>> toProcess, int libraryId, string libraryName, bool forceUpdate, DateTime scanStarted)
    {
        var tally = new ScanTally();
        var seriesLeftToProcess = toProcess.Count;
        var totalSeriesToProcess = toProcess.Count;
        var sw = Stopwatch.StartNew();

        try
        {
            foreach (var pSeries in toProcess)
            {
                // Placeholders for skipped folders aren't files
                tally.TotalFiles += pSeries.Count(info => string.IsNullOrEmpty(info.UnchangedFolderPath));

                using var scope = scopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var processSeries = scope.ServiceProvider.GetRequiredService<IProcessSeries>();

                // Library needs to be returned from the used UnitOfWork
                var library = (await unitOfWork.LibraryRepository.GetLibraryForIdAsync(libraryId,
                    LibraryIncludes.Folders | LibraryIncludes.FileTypes | LibraryIncludes.ExcludePatterns))!;

                var result = await processSeries.ProcessSeriesAsync(settings, pSeries, new ProcessSeriesArgs
                {
                    Library = library,
                    LeftToProcess = seriesLeftToProcess,
                    TotalToProcess = totalSeriesToProcess,
                    ForceUpdate = forceUpdate,
                    ScanStarted = scanStarted,
                });
                tally.Add(result);

                if (result.SeriesId != null)
                {
                    await channel.Writer.WriteAsync(result.SeriesId.Value);
                }

                seriesLeftToProcess--;
            }
        }
        finally // Ensure the channel is closed in case of an exception that we didn't expect
        {
            channel.Writer.Complete();
        }

        // Not an ended: ScanLibrary sends that after the commit. Progress 1 tells the widget covers and word count are what remain
        if (totalSeriesToProcess > 0)
        {
            await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.LibraryScanProgressEvent(libraryId, libraryName, ProgressEventType.Updated, string.Empty, 0, totalSeriesToProcess));
        }

        logger.LogDebug("[ScannerService] Finished writing metadata for {Count} series in {Elapsed}ms", toProcess.Count, sw.ElapsedMilliseconds);

        return tally;
    }

    private static void UpdateLastScanned(Library library)
    {
        var time = DateTime.Now;
        foreach (var folderPath in library.Folders)
        {
            folderPath.UpdateLastScanned(time);
        }

        library.UpdateLastScanned(time);
    }

    /// <returns>How long the walk took, the parsed series, and the issues saved</returns>
    private async Task<ScanFilesResult> ScanFiles(Library library, IList<string> dirs, bool isLibraryScan, bool forceChecks = false)
    {
        var scanner = new ParseScannedFiles(logger, directoryService, readingItemService, eventHub);
        var scanWatch = Stopwatch.StartNew();

        var folderMap = await unitOfWork.SeriesRepository.GetFolderPathMapAsync(library.Id);
        var problemFiles = await unitOfWork.MediaErrorRepository.GetFailedFilesAsync(library.Id);

        var processedSeries = await scanner.ScanLibrariesForSeries(library, dirs,
            isLibraryScan, folderMap, forceChecks,
            problemFiles);

        // This is the one time backfill we must do in v0.9.2 to ensure all files have FileLastWriteTime which is critical to the new Change Detection work
        await unitOfWork.MangaFileRepository.SetFileLastWriteTimesAsync(scanner.WriteTimesToBackfill);

        var savedIssues = await SaveScanIssuesAsync(library, scanner);
        await RemoveChangedProducerErrorsAsync(library, scanner);
        await RemoveErrorsForGoneFilesAsync(library, dirs, scanner);

        var scanElapsedTime = scanWatch.ElapsedMilliseconds;

        var parsedSeries = TrackFoundSeriesAndFiles(processedSeries,
            await unitOfWork.SeriesRepository.GetSeriesNameMatchesAsync(library.Id));

        return new ScanFilesResult(scanElapsedTime, parsedSeries, savedIssues, scanner.UnreadableFolders);
    }

    /// <summary>
    /// Saves one row per file with an issue and removes the rows of files that were read again without one, or are gone
    /// </summary>
    private async Task<SavedScanIssues> SaveScanIssuesAsync(Library library, ParseScannedFiles scanner)
    {
        var issues = scanner.Issues;
        var resolved = scanner.ResolvedFailedFiles.ToHashSet();
        var newCount = 0;

        if (issues.Count <= 0 && resolved.Count <= 0)
            return new SavedScanIssues(newCount, [], scanner.FilesInIssueFolders);

        var paths = issues.Select(i => i.Path).ToList();

        var rows = await unitOfWork.MediaErrorRepository.GetScannerErrorsAsync(library.Id, paths.Concat(resolved).ToList());
        var rowsByPath = rows.GroupBy(r => r.FilePath)
            .ToDictionary(g => g.Key, g => g.First());
        var toRemove = rows
            .Where(r => resolved.Contains(r.FilePath) || rowsByPath[r.FilePath] != r)
            .ToList();

        foreach (var issue in issues)
        {
            if (!rowsByPath.TryGetValue(issue.Path, out var row))
            {
                row = mapper.Map<MediaError>(issue);
                row.LibraryId = library.Id;
                unitOfWork.MediaErrorRepository.Attach(row);

                if (!MediaErrorReasons.Imported.Contains(issue.Reason))
                {
                    newCount++;
                }
                continue;
            }

            var isSameFailure = row.Reason == issue.Reason && row.Bytes == issue.Bytes &&
                                row.FileLastWriteTimeUtc is { } writeTime &&
                                FolderChangeCheck.IsSameWriteTime(writeTime, issue.LastWriteTimeUtc);
            mapper.Map(issue, row);
            if (isSameFailure) continue;

            row.IsDismissed = false;
            if (!MediaErrorReasons.Imported.Contains(issue.Reason))
            {
                newCount++;
            }
        }

        unitOfWork.MediaErrorRepository.Remove(toRemove);
        await unitOfWork.CommitAsync();

        return new SavedScanIssues(newCount, paths, scanner.FilesInIssueFolders);
    }

    /// <summary>
    /// Removes the reader, cover and word count rows of files this scan listed with a new size or write time, or no longer listed
    /// </summary>
    private async Task RemoveChangedProducerErrorsAsync(Library library, ParseScannedFiles scanner)
    {
        if (scanner.ReadFolders.Count == 0) return;

        var rows = await unitOfWork.MediaErrorRepository.GetProducerErrorsAsync(library.Id);
        var changed = rows.Where(r => IsChangedOrGone(r, scanner)).ToList();
        if (changed.Count == 0) return;

        unitOfWork.MediaErrorRepository.Remove(changed);
        await unitOfWork.CommitAsync();
    }

    /// <summary>
    /// Removes the rows of files under the scanned folders whose folder the scan never came across and are gone from disk,
    /// such as a deleted volume folder
    /// </summary>
    private async Task RemoveErrorsForGoneFilesAsync(Library library, IList<string> dirs, ParseScannedFiles scanner)
    {
        var roots = dirs.Select(Parser.NormalizePath).ToList();
        var paths = await unitOfWork.MediaErrorRepository.GetFilePathsAsync(library.Id);
        var gone = paths
            .Where(p => roots.Exists(p.Value.IsInsideFolder) && scanner.IsUnaccountedFor(p.Value.FolderOf())
                        && !directoryService.FileSystem.File.Exists(p.Value))
            .Select(p => p.Key)
            .ToList();
        if (gone.Count == 0) return;

        await unitOfWork.MediaErrorRepository.DeleteAsync(gone);
    }

    private static bool IsChangedOrGone(MediaError row, ParseScannedFiles scanner)
    {
        if (scanner.ReadFiles.TryGetValue(row.FilePath, out var stamp))
        {
            return row.Bytes != stamp.Bytes || row.FileLastWriteTimeUtc is not { } writeTime ||
                   !FolderChangeCheck.IsSameWriteTime(writeTime, stamp.LastWriteTimeUtc);
        }

        return scanner.ReadFolders.Contains(row.FilePath.FolderOf());
    }

    /// <param name="NewCount">Files that could not be read with a new issue: first seen, or a new reason or stamp</param>
    /// <param name="FilesByFolder">Every file listed directly in each folder with an issue</param>
    private sealed class ScanTally
    {
        public int TotalFiles { get; set; }
        public int SeriesAdded { get; private set; }
        public int SeriesRemoved { get; set; }
        public int ChaptersAdded { get; private set; }
        public int ChaptersUpdated { get; private set; }
        public int ChaptersRemoved { get; private set; }
        public bool HasChanges => SeriesAdded + SeriesRemoved + ChaptersAdded + ChaptersUpdated + ChaptersRemoved > 0;

        public void Add(ProcessSeriesResult result)
        {
            if (result.SeriesAdded)
            {
                SeriesAdded++;
            }
            ChaptersAdded += result.ChaptersAdded;
            ChaptersUpdated += result.ChaptersUpdated;
            ChaptersRemoved += result.ChaptersRemoved;
        }

        public LibraryScanEndedEventBodyDto ToEventBody(Library library, ScanIssueSummaryDto issues) =>
            new(library.Id, library.Name, SeriesAdded, SeriesRemoved, ChaptersAdded, ChaptersUpdated, ChaptersRemoved,
                issues.Count, issues.NewCount, issues.Issues);
    }

    private sealed record ScanFilesResult(long ElapsedMs, Dictionary<ParsedSeries, IList<ParserInfo>> ParsedSeries, SavedScanIssues SavedIssues,
        IReadOnlyList<string> UnreadableFolders);

    private sealed record SavedScanIssues(int NewCount, IList<string> Paths, IReadOnlyDictionary<string, IList<string>> FilesByFolder);

    private async Task AssignScanIssuesAsync(Library library, SavedScanIssues savedIssues, int? scannedSeriesId)
    {
        await unitOfWork.MediaErrorRepository.AssignScannerErrorsToSeriesAsync(library.Id, savedIssues.Paths, savedIssues.FilesByFolder,
            scannedSeriesId);
        await unitOfWork.CommitAsync();
    }

    /// <summary>
    /// Runs once the series are saved, so a row next to a series created this scan gets that series
    /// </summary>
    /// <param name="scannedSeriesId">Set by a series scan, for a row whose folder holds no known file</param>
    private async Task<ScanIssueSummaryDto> ReportScanIssuesAsync(Library library, SavedScanIssues savedIssues, int? scannedSeriesId = null)
    {
        await AssignScanIssuesAsync(library, savedIssues, scannedSeriesId);

        var summary = new ScanIssueSummaryDto(
            await unitOfWork.MediaErrorRepository.GetUnreadableFileCountAsync(library.Id),
            savedIssues.NewCount,
            await unitOfWork.MediaErrorRepository.GetUnreadableFilesAsync(library.Id, ScanIssueSummaryDto.MaxIssues));
        LogUnreadableFiles(library, summary);

        return summary;
    }

    private void LogUnreadableFiles(Library library, ScanIssueSummaryDto summary)
    {
        if (summary.Count == 0) return;

        var paths = string.Join(", ", summary.Issues.Select(i => i.FilePath));
        var more = summary.Count > summary.Issues.Count ? $" and {summary.Count - summary.Issues.Count} more" : string.Empty;
        logger.LogWarning("[ScannerService] {Count} file(s) in {LibraryName} could not be read: {Paths}{More}",
            summary.Count, library.Name, paths, more);
    }

    private async Task<Tuple<long, IList<GdsScannedSeriesResult>>> ScanGdsFiles(Library library, IList<string> dirs,
        bool isLibraryScan, bool forceChecks = false)
    {
        var scanner = new ParseScannedFiles(logger, directoryService, readingItemService, eventHub, mediaErrorService);
        var scanWatch = Stopwatch.StartNew();

        logger.LogInformation("[ScannerService] Lightweight GDS ScanFiles starting for {LibraryName}. Roots: {RootCount}; LibraryScan: {IsLibraryScan}; Force: {ForceChecks}",
            library.Name, dirs.Count, isLibraryScan, forceChecks);
        var processedSeries = await scanner.ScanGdsLibrariesForSeriesPlan(library, dirs,
            isLibraryScan, await unitOfWork.SeriesRepository.GetFolderPathMapAsync(library.Id), forceChecks);

        var scanElapsedTime = scanWatch.ElapsedMilliseconds;
        logger.LogInformation("[ScannerService] Lightweight GDS ScanFiles finished for {LibraryName}. Roots: {RootCount}; Parsed series: {ParsedSeriesCount}; Elapsed: {Elapsed} ms",
            library.Name, dirs.Count, processedSeries.Count, scanElapsedTime);

        return Tuple.Create<long, IList<GdsScannedSeriesResult>>(scanElapsedTime, processedSeries);
    }

    /// <summary>
    /// Given a list of all Genres, generates new Genre entries for any that do not exist.
    /// Does not delete anything, that will be handled by nightly task
    /// </summary>
    /// <param name="genres"></param>
    private async Task CreateAllGenresAsync(ICollection<string> genres)
    {
        logger.LogInformation("[ScannerService] Attempting to pre-save all Genres");

        try
        {
            // Pass the non-normalized genres directly to the repository
            var nonExistingGenres = await unitOfWork.GenreRepository.GetAllGenresNotInListAsync(genres);

            // Create and attach new genres using the non-normalized names
            foreach (var genre in nonExistingGenres)
            {
                var newGenre = new GenreBuilder(genre).Build();
                unitOfWork.GenreRepository.Attach(newGenre);
            }

            // Commit changes
            if (nonExistingGenres.Count > 0)
            {
                await unitOfWork.CommitAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ScannerService] There was an unknown issue when pre-saving all Genres");
        }
    }

    /// <summary>
    /// Given a list of all Tags, generates new Tag entries for any that do not exist.
    /// Does not delete anything, that will be handled by nightly task
    /// </summary>
    /// <param name="tags"></param>
    private async Task CreateAllTagsAsync(ICollection<string> tags)
    {
        logger.LogInformation("[ScannerService] Attempting to pre-save all Tags");

        try
        {
            // Pass the non-normalized tags directly to the repository
            var nonExistingTags = await unitOfWork.TagRepository.GetAllTagsNotInListAsync(tags);

            // Create and attach new genres using the non-normalized names
            foreach (var tag in nonExistingTags)
            {
                var newTag = new TagBuilder(tag).Build();
                unitOfWork.TagRepository.Attach(newTag);
            }

            // Commit changes
            if (nonExistingTags.Count > 0)
            {
                await unitOfWork.CommitAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ScannerService] There was an unknown issue when pre-saving all Tags");
        }
    }
}
