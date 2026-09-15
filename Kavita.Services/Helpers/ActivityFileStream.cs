using System;
using System.IO;

namespace Kavita.Services.Helpers;

public sealed class ActivityFileStream(string path, bool deleteOnClose, IDisposable activity)
    : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536,
        FileOptions.Asynchronous | (deleteOnClose ? FileOptions.DeleteOnClose : FileOptions.None))
{
    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (disposing) activity.Dispose(); }
    }
    public override async System.Threading.Tasks.ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { activity.Dispose(); }
    }
}
