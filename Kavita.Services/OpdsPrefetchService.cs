using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Reading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kavita.Services;

/// <summary>Bounded, best-effort preparation of one successor after an actual OPDS image response.</summary>
public sealed class OpdsPrefetchService(IServiceScopeFactory scopes, ILogger<OpdsPrefetchService> logger) : BackgroundService
{
    internal Func<string, bool> HeadroomCheck { get; set; } = HasHeadroom;
    internal TimeSpan HeadroomRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
    internal int HeadroomAttempts { get; set; } = 13;

    private readonly Channel<(int UserId, int ChapterId)> _queue = Channel.CreateBounded<(int, int)>(16);
    private readonly object _gate = new();
    private readonly HashSet<int> _pending = [];
    private readonly Dictionary<int, DateTime> _recent = [];

    public void TryQueue(int userId, int chapterId)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var id in _recent.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) _recent.Remove(id);
            if (_pending.Contains(chapterId) || _recent.ContainsKey(chapterId)) return;
            if (!_queue.Writer.TryWrite((userId, chapterId))) return;
            _pending.Add(chapterId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var prepared = false;
                try
                {
                    using var scope = scopes.CreateScope();
                    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    // Resolve identity/order from the DB, never the untrusted feed query parameters.
                    if (!await uow.UserRepository.HasAccessToChapter(item.UserId, item.ChapterId, stoppingToken)) continue;
                    var current = await uow.ChapterRepository.GetChapterAsync(item.ChapterId, ct: stoppingToken);
                    var seriesId = await uow.ChapterRepository.GetSeriesIdForChapter(item.ChapterId, stoppingToken);
                    if (current == null || seriesId == null) continue;
                    var reader = scope.ServiceProvider.GetRequiredService<IReaderService>();
                    var next = await reader.GetNextChapterIdAsync(seriesId.Value, current.VolumeId, current.Id, item.UserId);
                    if (next <= 0 || next == current.Id || !await uow.UserRepository.HasAccessToChapter(item.UserId, next, stoppingToken)) continue;
                    var directories = scope.ServiceProvider.GetRequiredService<IDirectoryService>();
                    // A cold foreground read can briefly raise our own PSI. Give it
                    // bounded time to settle instead of suppressing this book for minutes.
                    if (!await WaitForHeadroomAsync(directories.CacheDirectory, stoppingToken))
                    {
                        logger.LogInformation("OPDS next chapter preparation skipped due to resource pressure");
                        continue;
                    }
                    // Permissions may have changed while waiting for resources.
                    if (!await uow.UserRepository.HasAccessToChapter(item.UserId, next, stoppingToken)) continue;
                    var timer = Stopwatch.StartNew();
                    logger.LogInformation("OPDS preparing next chapter {ChapterId}", next);
                    var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();
                    // Ensure shares the foreground extraction lock/cache. No progress write or recursive enqueue.
                    prepared = await cache.Ensure(next, true, stoppingToken) != null;
                    if (prepared) logger.LogInformation("OPDS prepared chapter {ChapterId} in {ElapsedMs} ms", next, timer.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "OPDS next chapter preparation skipped"); }
                finally
                {
                    lock (_gate)
                    {
                        _pending.Remove(item.ChapterId);
                        // Bound deduplication history even for very large browsing sessions.
                        if (_recent.Count >= 1024) _recent.Clear();
                        _recent[item.ChapterId] = DateTime.UtcNow.AddSeconds(prepared ? 600 : 30);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task<bool> WaitForHeadroomAsync(string directory, CancellationToken ct)
    {
        for (var attempt = 0; attempt < HeadroomAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (HeadroomCheck(directory)) return true;
            if (attempt == 0) logger.LogInformation("OPDS next chapter preparation waiting for resource pressure to settle");
            if (attempt + 1 < HeadroomAttempts) await Task.Delay(HeadroomRetryDelay, ct);
        }
        return false;
    }

    internal static bool HasHeadroom(string cacheDirectory)
    {
        try
        {
            var fullPath = Path.GetFullPath(cacheDirectory);
            var drive = DriveInfo.GetDrives().Where(d => fullPath.StartsWith(d.Name, StringComparison.Ordinal))
                .OrderByDescending(d => d.Name.Length).FirstOrDefault();
            if (drive != null && drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024) return false;
            if (OperatingSystem.IsLinux())
            {
                // Host PSI includes unrelated containers and kernel workers. Prefer the
                // reader's cgroup so unrelated waits do not disable preparation indefinitely.
                var membership = File.Exists("/proc/self/cgroup") ? File.ReadAllText("/proc/self/cgroup") : "";
                var pressurePath = ResolveIoPressurePath("/sys/fs/cgroup", membership);
                var line = File.Exists(pressurePath)
                    ? File.ReadLines(pressurePath).FirstOrDefault(l => l.StartsWith("full ", StringComparison.Ordinal))
                    : null;
                var value = line?.Split(' ').FirstOrDefault(v => v.StartsWith("avg10=", StringComparison.Ordinal));
                if (value != null && double.TryParse(value[6..], CultureInfo.InvariantCulture, out var pressure) && pressure >= 2) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static string ResolveIoPressurePath(string root, string membership)
    {
        var unified = membership.Split('\n').FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal));
        if (unified != null)
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, unified[3..].Trim().TrimStart('/'), "io.pressure"));
            if (candidate.StartsWith(prefix, StringComparison.Ordinal) && File.Exists(candidate)) return candidate;
            // A container may mount its own cgroup as the root while membership is
            // relative to an ancestor namespace. Never traverse outside that mount.
            var local = Path.Combine(root, "io.pressure");
            if (File.Exists(local)) return local;
        }
        return "/proc/pressure/io";
    }
}
