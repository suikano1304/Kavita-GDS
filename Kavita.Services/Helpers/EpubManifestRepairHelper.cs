using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Kavita.Services.Helpers;

public static class EpubManifestRepairHelper
{
    private static readonly XNamespace ContainerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";

    public static bool TryCreateDeduplicatedManifestCopy(string sourcePath, string tempDirectory, out string repairedPath)
    {
        repairedPath = string.Empty;

        try
        {
            Directory.CreateDirectory(tempDirectory);
            using var source = ZipFile.OpenRead(sourcePath);
            var opfPath = GetOpfPath(source);
            if (string.IsNullOrWhiteSpace(opfPath)) return false;

            var opfEntry = source.GetEntry(opfPath);
            if (opfEntry == null) return false;

            XDocument opfDocument;
            using (var opfStream = opfEntry.Open())
            {
                opfDocument = XDocument.Load(opfStream, LoadOptions.PreserveWhitespace);
            }

            if (!RepairDuplicateManifestItems(opfDocument)) return false;

            repairedPath = Path.Join(tempDirectory, $"epub-manifest-repair-{Guid.NewGuid():N}.epub");
            using var repaired = ZipFile.Open(repairedPath, ZipArchiveMode.Create);
            foreach (var entry in source.Entries)
            {
                var repairedEntry = repaired.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                repairedEntry.LastWriteTime = entry.LastWriteTime;

                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;

                using var output = repairedEntry.Open();
                if (string.Equals(entry.FullName, opfPath, StringComparison.Ordinal))
                {
                    opfDocument.Save(output, SaveOptions.DisableFormatting);
                    continue;
                }

                using var input = entry.Open();
                input.CopyTo(output);
            }

            return true;
        }
        catch
        {
            DeleteQuietly(repairedPath);
            repairedPath = string.Empty;
            return false;
        }
    }

    public static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }

    private static string? GetOpfPath(ZipArchive epub)
    {
        var containerEntry = epub.GetEntry("META-INF/container.xml");
        if (containerEntry == null) return null;

        using var stream = containerEntry.Open();
        var container = XDocument.Load(stream);
        return container.Root?
            .Element(ContainerNamespace + "rootfiles")?
            .Elements(ContainerNamespace + "rootfile")
            .Select(element => element.Attribute("full-path")?.Value)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    }

    private static bool RepairDuplicateManifestItems(XDocument opfDocument)
    {
        var root = opfDocument.Root;
        if (root == null) return false;

        var opfNamespace = root.Name.Namespace;
        var manifest = root.Element(opfNamespace + "manifest");
        if (manifest == null) return false;

        var spine = root.Element(opfNamespace + "spine");
        var seenExactItems = new HashSet<string>(StringComparer.Ordinal);
        var seenIdItems = new HashSet<string>(StringComparer.Ordinal);
        var seenHrefItems = new Dictionary<string, string>(StringComparer.Ordinal);
        var idRedirects = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<XElement>();
        foreach (var item in manifest.Elements(opfNamespace + "item"))
        {
            var id = item.Attribute("id")?.Value;
            var href = item.Attribute("href")?.Value;
            var mediaType = item.Attribute("media-type")?.Value;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href)) continue;

            var exactKey = $"{id}\u001f{href}\u001f{mediaType}";
            if (!seenExactItems.Add(exactKey))
            {
                duplicates.Add(item);
                idRedirects[id] = id;
                continue;
            }

            if (!seenIdItems.Add(id))
            {
                duplicates.Add(item);
                idRedirects[id] = id;
                continue;
            }

            var hrefKey = $"{href}\u001f{mediaType}";
            if (seenHrefItems.TryGetValue(hrefKey, out var retainedId))
            {
                duplicates.Add(item);
                idRedirects[id] = retainedId;
                continue;
            }

            seenHrefItems[hrefKey] = id;
        }

        foreach (var duplicate in duplicates)
        {
            duplicate.Remove();
        }

        if (spine != null)
        {
            foreach (var itemref in spine.Elements(opfNamespace + "itemref"))
            {
                var idref = itemref.Attribute("idref")?.Value;
                if (string.IsNullOrWhiteSpace(idref)) continue;
                if (!idRedirects.TryGetValue(idref, out var retainedId) || string.IsNullOrWhiteSpace(retainedId)) continue;
                itemref.SetAttributeValue("idref", retainedId);
            }
        }

        return duplicates.Count > 0;
    }
}
