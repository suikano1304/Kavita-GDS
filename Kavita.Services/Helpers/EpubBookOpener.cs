using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VersOne.Epub;
using VersOne.Epub.Options;

namespace Kavita.Services.Helpers;

/// <summary>Shares validated repairs across reader, metadata and resource requests. Source files are never written.</summary>
public static class EpubBookOpener
{
    private sealed class Entry(string version, string path)
    {
        public string Version { get; } = version;
        public string Path { get; } = path;
        public int Readers;
        public DateTime LastUsed = DateTime.UtcNow;
    }
    private static readonly object Gate = new();
    private static readonly List<Entry> Repairs = [];

    public sealed class Lease(EpubBookRef book, Stream stream, string path, Action release) : IDisposable
    {
        private int _disposed;
        public IDisposable? Activity { get; set; }
        public EpubBookRef Book { get; } = book;
        public string Path { get; } = path;
        public void Dispose() { if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return; try { Book.Dispose(); } finally { stream.Dispose(); release(); Activity?.Dispose(); } }
    }

    private static string Version(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("EPUB source is missing", path);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    public static Lease Open(string path, string tempDirectory, EpubReaderOptions options)
    {
        var activity = CacheActivityGate.Enter();
        try { var lease = OpenCore(path, tempDirectory, options); lease.Activity = activity; return lease; }
        catch { activity.Dispose(); throw; }
    }

    private static Lease OpenCore(string path, string tempDirectory, EpubReaderOptions options)
    {
        lock (Gate)
        {
            var version = Version(path);
            var entry = Repairs.FirstOrDefault(e => e.Version == version && File.Exists(e.Path));
            if (entry != null) return Acquire(entry, options);
            try { return OpenLease(path, options, () => { }); }
            catch (EpubReaderException)
            {
                if (!EpubManifestRepairHelper.TryCreateDeduplicatedManifestCopy(path, tempDirectory, out var repaired)) throw;
                try
                {
                    var lease = OpenLease(repaired, options, () => Release(entry!));
                    if (Version(path) != version) { lease.Dispose(); throw new IOException("EPUB changed during repair"); }
                    entry = new Entry(version, repaired) { Readers = 1 };
                    Repairs.Add(entry);
                    Prune();
                    return lease;
                }
                catch { EpubManifestRepairHelper.DeleteQuietly(repaired); throw; }
            }
        }
    }

    private static Lease OpenLease(string path, EpubReaderOptions options, Action release)
    {
        // Own the stream even when the parser throws before returning an EpubBookRef.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try { return new Lease(Validate(EpubReader.OpenBook(stream, options), stream), stream, path, release); }
        catch { stream.Dispose(); throw; }
    }

    private static EpubBookRef Validate(EpubBookRef book, Stream stream)
    {
        try
        {
            var order = book.GetReadingOrder();
            if (order.Count == 0) throw new InvalidDataException("EPUB has no readable spine");
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (!order.Any(page => archive.GetEntry(page.FilePath) != null ||
                    archive.GetEntry(EpubResourcePath.Normalize(Uri.UnescapeDataString(page.FilePath))) != null))
                throw new InvalidDataException("EPUB has no readable spine resource");
            return book;
        }
        catch { book.Dispose(); throw; }
    }

    private static Lease Acquire(Entry entry, EpubReaderOptions options)
    {
        var lease = OpenLease(entry.Path, options, () => Release(entry));
        entry.Readers++;
        entry.LastUsed = DateTime.UtcNow;
        return lease;
    }

    private static void Release(Entry? entry)
    {
        lock (Gate) { if (entry != null) entry.Readers--; Prune(); }
    }

    private static void Prune()
    {
        foreach (var entry in Repairs.Where(e => e.Readers == 0).OrderByDescending(e => e.LastUsed).Skip(16).ToList())
        {
            EpubManifestRepairHelper.DeleteQuietly(entry.Path);
            Repairs.Remove(entry);
        }
    }
}
