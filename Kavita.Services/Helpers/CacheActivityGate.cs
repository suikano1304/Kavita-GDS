using System;
using System.Threading;

namespace Kavita.Services.Helpers;

/// <summary>Allows concurrent readers/builders, but global cache purges wait for their leases.</summary>
public static class CacheActivityGate
{
    private static readonly object Mutex = new();
    private static readonly SemaphoreSlim Exclusive = new(1, 1);
    private static int _readers;

    public static IDisposable Enter()
    {
        lock (Mutex)
        {
            if (_readers == 0) Exclusive.Wait();
            _readers++;
        }
        return new Lease();
    }

    public static void Purge(Action action)
    {
        Exclusive.Wait();
        try { action(); }
        finally { Exclusive.Release(); }
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Mutex) { if (--_readers == 0) Exclusive.Release(); }
        }
    }
}
