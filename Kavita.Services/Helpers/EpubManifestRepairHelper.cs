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
    private static readonly XNamespace XhtmlNamespace = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace EpubNamespace = "http://www.idpf.org/2007/ops";

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

            var repairedManifest = RepairDuplicateManifestItems(opfDocument);
            var repairedMediaTypes = NormalizeImageMediaTypes(opfDocument);
            var repairedMissingReferences = RepairMissingManifestReferences(opfDocument, source, opfPath);
            var repairedSpine = RemoveMissingSpineItemRefs(opfDocument);
            var synthesizedEntries = RepairMissingEpub3NavDocument(opfDocument, source, opfPath);
            if (!repairedManifest && !repairedMediaTypes && !repairedMissingReferences && !repairedSpine &&
                synthesizedEntries.Count == 0) return false;

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

            foreach (var synthesizedEntry in synthesizedEntries)
            {
                var repairedEntry = repaired.CreateEntry(synthesizedEntry.Key, CompressionLevel.Optimal);
                using var output = repairedEntry.Open();
                synthesizedEntry.Value.Save(output, SaveOptions.DisableFormatting);
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

    private static bool NormalizeImageMediaTypes(XDocument opfDocument)
    {
        var root = opfDocument.Root;
        if (root == null) return false;

        var opfNamespace = root.Name.Namespace;
        var manifest = root.Element(opfNamespace + "manifest");
        if (manifest == null) return false;

        var repaired = false;
        foreach (var item in manifest.Elements(opfNamespace + "item"))
        {
            var mediaType = item.Attribute("media-type")?.Value;
            if (!string.Equals(mediaType, "image/jpg", StringComparison.OrdinalIgnoreCase)) continue;

            item.SetAttributeValue("media-type", "image/jpeg");
            repaired = true;
        }

        return repaired;
    }

    private static bool RepairMissingManifestReferences(XDocument opfDocument, ZipArchive source, string opfPath)
    {
        var root = opfDocument.Root;
        if (root == null) return false;

        var opfNamespace = root.Name.Namespace;
        var manifest = root.Element(opfNamespace + "manifest");
        if (manifest == null) return false;

        var repaired = false;
        var manifestItems = manifest.Elements(opfNamespace + "item").ToList();
        var manifestById = manifestItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Attribute("id")?.Value))
            .GroupBy(item => item.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var manifestHrefs = manifestItems
            .Select(item => item.Attribute("href")?.Value)
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .Select(href => StripFragment(href!))
            .ToHashSet(StringComparer.Ordinal);

        var coverMetaItems = root.Descendants()
            .Where(element => element.Name.LocalName == "meta")
            .Where(element => string.Equals(element.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var coverMeta in coverMetaItems)
        {
            var content = coverMeta.Attribute("content")?.Value;
            if (string.IsNullOrWhiteSpace(content)) continue;

            if (manifestById.TryGetValue(content, out var manifestItem))
            {
                var href = manifestItem.Attribute("href")?.Value;
                if (!string.IsNullOrWhiteSpace(href) && EntryExistsForHref(source, opfPath, href)) continue;

                coverMeta.Remove();
                repaired = true;
                continue;
            }

            if (!EntryExistsForHref(source, opfPath, content))
            {
                coverMeta.Remove();
                repaired = true;
                continue;
            }

            var coverId = GetUniqueManifestId(manifestItems, "kavita-cover");
            manifest.Add(new XElement(opfNamespace + "item",
                new XAttribute("href", StripFragment(content)),
                new XAttribute("id", coverId),
                new XAttribute("media-type", GetMediaType(content))));
            coverMeta.SetAttributeValue("content", coverId);
            manifestItems.Add(manifest.Elements(opfNamespace + "item").Last());
            manifestHrefs.Add(StripFragment(content));
            repaired = true;
        }

        var guide = root.Element(opfNamespace + "guide");
        if (guide == null) return repaired;

        foreach (var reference in guide.Elements(opfNamespace + "reference").ToList())
        {
            var href = reference.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) continue;

            var hrefWithoutFragment = StripFragment(href);
            if (manifestHrefs.Contains(hrefWithoutFragment)) continue;

            if (!EntryExistsForHref(source, opfPath, hrefWithoutFragment))
            {
                reference.Remove();
                repaired = true;
                continue;
            }

            var id = GetUniqueManifestId(manifestItems, "kavita-guide");
            manifest.Add(new XElement(opfNamespace + "item",
                new XAttribute("href", hrefWithoutFragment),
                new XAttribute("id", id),
                new XAttribute("media-type", GetMediaType(hrefWithoutFragment))));
            manifestItems.Add(manifest.Elements(opfNamespace + "item").Last());
            manifestHrefs.Add(hrefWithoutFragment);
            repaired = true;
        }

        return repaired;
    }

    private static bool RemoveMissingSpineItemRefs(XDocument opfDocument)
    {
        var root = opfDocument.Root;
        if (root == null) return false;

        var opfNamespace = root.Name.Namespace;
        var manifest = root.Element(opfNamespace + "manifest");
        var spine = root.Element(opfNamespace + "spine");
        if (manifest == null || spine == null) return false;

        var manifestIds = manifest.Elements(opfNamespace + "item")
            .Select(item => item.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        var itemRefs = spine.Elements(opfNamespace + "itemref").ToList();
        var missingItemRefs = itemRefs
            .Where(itemref =>
            {
                var idref = itemref.Attribute("idref")?.Value;
                return string.IsNullOrWhiteSpace(idref) || !manifestIds.Contains(idref);
            })
            .ToList();
        if (missingItemRefs.Count == 0 || missingItemRefs.Count == itemRefs.Count) return false;

        foreach (var itemref in missingItemRefs)
        {
            itemref.Remove();
        }

        return true;
    }

    private static bool EntryExistsForHref(ZipArchive source, string opfPath, string href)
    {
        var archivePath = GetArchivePathForHref(opfPath, href);
        return source.GetEntry(archivePath) != null ||
               source.Entries.Any(entry => string.Equals(entry.FullName, archivePath, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetArchivePathForHref(string opfPath, string href)
    {
        var hrefWithoutFragment = StripFragment(href).Replace('\\', '/');
        var opfDirectory = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? string.Empty;
        return string.IsNullOrWhiteSpace(opfDirectory) ? hrefWithoutFragment : $"{opfDirectory}/{hrefWithoutFragment}";
    }

    private static string StripFragment(string href)
    {
        var hashIndex = href.IndexOf('#', StringComparison.Ordinal);
        return hashIndex < 0 ? href : href[..hashIndex];
    }

    private static string GetMediaType(string href)
    {
        return Path.GetExtension(StripFragment(href)).ToLowerInvariant() switch
        {
            ".xhtml" or ".html" or ".htm" => "application/xhtml+xml",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".css" => "text/css",
            ".ncx" => "application/x-dtbncx+xml",
            _ => "application/octet-stream"
        };
    }

    private static Dictionary<string, XDocument> RepairMissingEpub3NavDocument(
        XDocument opfDocument,
        ZipArchive source,
        string opfPath)
    {
        var synthesizedEntries = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        var root = opfDocument.Root;
        if (root == null) return synthesizedEntries;

        var version = root.Attribute("version")?.Value;
        if (string.IsNullOrWhiteSpace(version) || !version.StartsWith("3", StringComparison.Ordinal)) return synthesizedEntries;

        var opfNamespace = root.Name.Namespace;
        var manifest = root.Element(opfNamespace + "manifest");
        var spine = root.Element(opfNamespace + "spine");
        if (manifest == null || spine == null) return synthesizedEntries;

        var manifestItems = manifest.Elements(opfNamespace + "item").ToList();
        var hasNav = manifestItems.Any(item =>
            item.Attribute("properties")?.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(property => string.Equals(property, "nav", StringComparison.Ordinal)) == true);
        if (hasNav) return synthesizedEntries;

        var htmlManifestItems = manifestItems
            .Where(item => string.Equals(item.Attribute("media-type")?.Value, "application/xhtml+xml", StringComparison.Ordinal))
            .Where(item => !string.IsNullOrWhiteSpace(item.Attribute("id")?.Value))
            .ToDictionary(item => item.Attribute("id")!.Value, item => item, StringComparer.Ordinal);

        var spineHrefs = spine.Elements(opfNamespace + "itemref")
            .Select(itemRef => itemRef.Attribute("idref")?.Value)
            .Where(idRef => !string.IsNullOrWhiteSpace(idRef))
            .Select(idRef => htmlManifestItems.TryGetValue(idRef!, out var manifestItem) ? manifestItem.Attribute("href")?.Value : null)
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .Select(href => href!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (spineHrefs.Count == 0) return synthesizedEntries;

        var opfDirectory = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? string.Empty;
        var navFileName = GetUniqueNavFileName(source, opfDirectory);
        var navArchivePath = string.IsNullOrWhiteSpace(opfDirectory)
            ? navFileName
            : $"{opfDirectory}/{navFileName}";

        var navId = GetUniqueManifestId(manifestItems, "kavita-nav");
        manifest.Add(new XElement(opfNamespace + "item",
            new XAttribute("href", navFileName),
            new XAttribute("id", navId),
            new XAttribute("media-type", "application/xhtml+xml"),
            new XAttribute("properties", "nav")));

        var title = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "title")?.Value;
        if (string.IsNullOrWhiteSpace(title)) title = "Table of Contents";

        synthesizedEntries[navArchivePath] = BuildNavDocument(title, spineHrefs);
        return synthesizedEntries;
    }

    private static string GetUniqueNavFileName(ZipArchive source, string opfDirectory)
    {
        const string baseName = "kavita-nav";
        const string extension = ".xhtml";
        for (var index = 0; index < 1000; index++)
        {
            var fileName = index == 0 ? $"{baseName}{extension}" : $"{baseName}-{index}{extension}";
            var archivePath = string.IsNullOrWhiteSpace(opfDirectory) ? fileName : $"{opfDirectory}/{fileName}";
            if (source.GetEntry(archivePath) == null) return fileName;
        }

        return $"{baseName}-{Guid.NewGuid():N}{extension}";
    }

    private static string GetUniqueManifestId(IEnumerable<XElement> manifestItems, string baseId)
    {
        var ids = manifestItems
            .Select(item => item.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        if (!ids.Contains(baseId)) return baseId;

        for (var index = 1; index < 1000; index++)
        {
            var candidate = $"{baseId}-{index}";
            if (!ids.Contains(candidate)) return candidate;
        }

        return $"{baseId}-{Guid.NewGuid():N}";
    }

    private static XDocument BuildNavDocument(string title, IReadOnlyCollection<string> spineHrefs)
    {
        var items = spineHrefs.Select((href, index) =>
            new XElement(XhtmlNamespace + "li",
                new XElement(XhtmlNamespace + "a",
                    new XAttribute("href", href),
                    index == 0 ? title : $"Page {index + 1}")));

        return new XDocument(
            new XElement(XhtmlNamespace + "html",
                new XAttribute(XNamespace.Xmlns + "epub", EpubNamespace.NamespaceName),
                new XAttribute(XNamespace.Xml + "lang", "en"),
                new XElement(XhtmlNamespace + "head",
                    new XElement(XhtmlNamespace + "title", title)),
                new XElement(XhtmlNamespace + "body",
                    new XElement(XhtmlNamespace + "nav",
                        new XAttribute(EpubNamespace + "type", "toc"),
                        new XElement(XhtmlNamespace + "ol", items)))));
    }
}
