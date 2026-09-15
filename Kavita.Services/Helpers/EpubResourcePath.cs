using System;
using System.Collections.Generic;
using System.Linq;

namespace Kavita.Services.Helpers;

public static class EpubResourcePath
{
    public static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            segments.Add(part);
        }
        return string.Join("/", segments);
    }

    // Strip URI fragments before decoding, so %23 remains part of the ZIP entry name.
    public static string Resolve(string owner, string href)
    {
        var path = Uri.UnescapeDataString(href.Split('#')[0].Split('?')[0]);
        var slash = owner.Replace('\\', '/').LastIndexOf('/');
        return Normalize((slash < 0 ? "" : owner[..(slash + 1)]) + path);
    }

    public static string EncodePath(string path) => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
}
