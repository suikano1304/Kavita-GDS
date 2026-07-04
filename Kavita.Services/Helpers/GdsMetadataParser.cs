using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Kavita.Common.Extensions;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Metadata;
using Kavita.Services.Extensions;

namespace Kavita.Services.Helpers;

public static class GdsMetadataParser
{
    private const int MaxLargeYamlLinePrefixChars = 8 * 1024;
    private static readonly ConcurrentDictionary<string, CachedYaml> YamlCache = new(StringComparer.OrdinalIgnoreCase);

    public static ComicInfo? GetComicInfo(string filePath, ComicInfo? baseInfo = null)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory)) return baseInfo;

        var yamlPath = GetMetadataPath(directory);
        if (string.IsNullOrEmpty(yamlPath)) return baseInfo;

        var metadata = GetCachedYamlInfo(yamlPath)?.Metadata;
        if (metadata == null || metadata.Count == 0)
        {
            return BuildFallbackComicInfo(filePath, baseInfo);
        }

        return BuildComicInfo(filePath, baseInfo, key =>
            metadata.TryGetValue(key, out var value) ? value : null);
    }

    private static ComicInfo BuildComicInfo(string filePath, ComicInfo? baseInfo, Func<string, string?> metadata)
    {
        var info = baseInfo ?? new ComicInfo();

        if (string.IsNullOrWhiteSpace(info.Title))
        {
            info.Title = BuildTitleFromFileName(filePath);
        }

        Apply(metadata, "Summary", value => info.Summary = value);
        Apply(metadata, "Genres", value => info.Genre = value);
        Apply(metadata, "Tags", value => info.Tags = value);
        Apply(metadata, "Language", value => info.LanguageISO = value);
        Apply(metadata, "Web Links", value => info.Web = value);
        Apply(metadata, "Person Writers", value => info.Writer = value);
        Apply(metadata, "Writer", value => info.Writer = value);
        Apply(metadata, "Person Translator", value => info.Translator = value);
        Apply(metadata, "Person Publisher", value => info.Publisher = value);
        Apply(metadata, "Person Penciller", value => info.Penciller = value);
        Apply(metadata, "Person Inker", value => info.Inker = value);
        Apply(metadata, "Person Colorist", value => info.Colorist = value);
        Apply(metadata, "Person Letterer", value => info.Letterer = value);
        Apply(metadata, "Person CoverArtist", value => info.CoverArtist = value);
        Apply(metadata, "Person Editor", value => info.Editor = value);
        Apply(metadata, "Person Imprint", value => info.Imprint = value);
        Apply(metadata, "Person Character", value => info.Characters = value);
        Apply(metadata, "Person Team", value => info.Teams = value);
        Apply(metadata, "Person Location", value => info.Locations = value);
        Apply(metadata, "Age Rating", value => info.AgeRating = ParseAgeRating(value));

        Apply(metadata, "Release Date", value =>
        {
            if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                info.Year = date.Year;
                info.Month = date.Month;
                info.Day = date.Day;
            }
        });
        Apply(metadata, "Year", value =>
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
                year is >= 1 and <= 9999)
            {
                info.Year = year;
            }
        });
        Apply(metadata, "Month", value =>
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
                month is >= 1 and <= 12)
            {
                info.Month = month;
            }
        });
        Apply(metadata, "Day", value =>
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var day) &&
                day is >= 1 and <= 31)
            {
                info.Day = day;
            }
        });

        info.CleanComicInfo();
        return info;
    }

    private static ComicInfo BuildFallbackComicInfo(string filePath, ComicInfo? baseInfo)
    {
        var info = baseInfo ?? new ComicInfo();
        if (string.IsNullOrWhiteSpace(info.Title))
        {
            info.Title = BuildTitleFromFileName(filePath);
        }

        info.CleanComicInfo();
        return info;
    }

    public static bool TryGetCoverBase64(string filePath, out string encodedImage)
    {
        encodedImage = string.Empty;

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory)) return false;

        var yamlPath = GetMetadataPath(directory);
        if (string.IsNullOrEmpty(yamlPath)) return false;

        var fileName = Path.GetFileName(filePath);
        return TryGetCoverBase64FromLines(yamlPath, fileName, out encodedImage);
    }

    public static bool TryGetPageCount(string filePath, out int pages)
    {
        pages = 0;

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory)) return false;

        var yamlPath = GetMetadataPath(directory);
        if (string.IsNullOrEmpty(yamlPath)) return false;

        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName)) return false;

        var yamlInfo = GetCachedYamlInfo(yamlPath);
        return yamlInfo?.FilePages.TryGetValue(fileName, out pages) == true && pages > 0;
    }

    private static bool TryGetCoverBase64FromLines(string yamlPath, string fileName, out string encodedImage)
    {
        encodedImage = string.Empty;
        var inFiles = false;
        var inTargetFile = false;

        try
        {
            foreach (var line in File.ReadLines(yamlPath))
            {
                if (!inFiles)
                {
                    if (TryParseIndentedKey(line, 0, out var rootKey) &&
                        string.Equals(rootKey, "files", StringComparison.OrdinalIgnoreCase))
                    {
                        inFiles = true;
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line) && !char.IsWhiteSpace(line[0]))
                {
                    return false;
                }

                if (TryParseIndentedKey(line, 4, out var candidateFile))
                {
                    inTargetFile = string.Equals(UnquoteYamlScalar(candidateFile), fileName, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inTargetFile) continue;

                if (TryParseIndentedScalar(line, 8, "cover", out var cover))
                {
                    return TryNormalizeBase64Cover(cover, out encodedImage);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return false;
        }

        return false;
    }

    private static bool TryParseIndentedKey(string line, int indent, out string key)
    {
        key = string.Empty;
        if (!HasIndent(line, indent)) return false;

        var trimmed = line.Trim();
        if (!trimmed.EndsWith(":", StringComparison.Ordinal)) return false;

        key = trimmed[..^1].Trim();
        return !string.IsNullOrWhiteSpace(key);
    }

    private static bool TryParseIndentedScalar(string line, int indent, string key, out string value)
    {
        value = string.Empty;
        if (!HasIndent(line, indent)) return false;

        var trimmed = line.Trim();
        var prefix = key + ":";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        value = trimmed[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool HasIndent(string line, int indent)
    {
        if (line.Length < indent) return false;
        for (var i = 0; i < indent; i++)
        {
            if (line[i] != ' ') return false;
        }

        return line.Length == indent || line[indent] != ' ';
    }

    private static string UnquoteYamlScalar(string value)
    {
        return value.Trim().Trim('"').Trim('\'');
    }

    private static bool TryNormalizeBase64Cover(string value, out string encodedImage)
    {
        encodedImage = UnquoteYamlScalar(value);
        if (string.IsNullOrWhiteSpace(encodedImage)) return false;
        if (string.Equals(encodedImage, "TEXT", StringComparison.OrdinalIgnoreCase))
        {
            encodedImage = string.Empty;
            return false;
        }

        if (encodedImage.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            encodedImage.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            encodedImage = string.Empty;
            return false;
        }

        const string base64Marker = "base64,";
        var base64Index = encodedImage.IndexOf(base64Marker, StringComparison.OrdinalIgnoreCase);
        if (encodedImage.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && base64Index >= 0)
        {
            encodedImage = encodedImage[(base64Index + base64Marker.Length)..];
        }

        try
        {
            Convert.FromBase64String(encodedImage);
            return true;
        }
        catch (FormatException)
        {
            encodedImage = string.Empty;
            return false;
        }
    }

    private static string? GetMetadataPath(string directory)
    {
        var yamlPath = Path.Join(directory, "kavita.yaml");
        if (File.Exists(yamlPath)) return yamlPath;

        yamlPath = Path.Join(directory, "kavita.yml");
        return File.Exists(yamlPath) ? yamlPath : null;
    }

    private static LargeYamlInfo? GetCachedYamlInfo(string yamlPath)
    {
        FileInfo? fileInfo = null;
        try
        {
            fileInfo = new FileInfo(yamlPath);
            if (!fileInfo.Exists) return null;

            if (YamlCache.TryGetValue(yamlPath, out var cached) &&
                cached.Length == fileInfo.Length &&
                cached.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc &&
                cached.LargeMetadata != null &&
                cached.LargeFilePages != null)
            {
                return new LargeYamlInfo(cached.LargeMetadata, cached.LargeFilePages);
            }

            var largeInfo = ReadLargeYamlInfo(yamlPath);
            YamlCache[yamlPath] = new CachedYaml(fileInfo.Length, fileInfo.LastWriteTimeUtc,
                largeInfo.Metadata, largeInfo.FilePages);
            return largeInfo;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            if (fileInfo?.Exists == true)
            {
                YamlCache[yamlPath] = new CachedYaml(fileInfo.Length, fileInfo.LastWriteTimeUtc,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            }

            return null;
        }
    }

    private static LargeYamlInfo ReadLargeYamlInfo(string yamlPath)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var filePages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var inMeta = false;
        var inFiles = false;
        string? currentFile = null;

        foreach (var cappedLine in ReadCappedLines(yamlPath, MaxLargeYamlLinePrefixChars))
        {
            var line = cappedLine.Text;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (TryParseIndentedKey(line, 0, out var rootKey))
            {
                inMeta = string.Equals(rootKey, "meta", StringComparison.OrdinalIgnoreCase);
                inFiles = string.Equals(rootKey, "files", StringComparison.OrdinalIgnoreCase);
                currentFile = null;
                continue;
            }

            if (inFiles)
            {
                if (!char.IsWhiteSpace(line[0]))
                {
                    inFiles = false;
                    currentFile = null;
                    continue;
                }

                if (TryParseIndentedKey(line, 4, out var fileName))
                {
                    currentFile = UnquoteYamlScalar(fileName);
                    continue;
                }

                if (currentFile != null && !cappedLine.IsTruncated &&
                    TryParseIndentedScalar(line, 8, "page", out var pageText) &&
                    int.TryParse(UnquoteYamlScalar(pageText), NumberStyles.None, CultureInfo.InvariantCulture, out var page) &&
                    page > 0)
                {
                    filePages[currentFile] = page;
                }

                continue;
            }

            if (inMeta)
            {
                if (!char.IsWhiteSpace(line[0]))
                {
                    inMeta = false;
                    continue;
                }
                if (cappedLine.IsTruncated) continue;

                if (!TryParseIndentedScalar(line, 4, out var key, out var value)) continue;
                metadata[key] = UnquoteYamlScalar(value);
            }
        }

        return new LargeYamlInfo(metadata, filePages);
    }

    private static IEnumerable<CappedLine> ReadCappedLines(string path, int maxChars)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024);

        var builder = new StringBuilder(Math.Min(maxChars, 1024));
        var truncated = false;

        while (reader.Read() is var current && current >= 0)
        {
            var ch = (char) current;
            if (ch == '\r') continue;

            if (ch == '\n')
            {
                yield return new CappedLine(builder.ToString(), truncated);
                builder.Clear();
                truncated = false;
                continue;
            }

            if (builder.Length < maxChars)
            {
                builder.Append(ch);
            }
            else
            {
                truncated = true;
            }
        }

        if (builder.Length > 0 || truncated)
        {
            yield return new CappedLine(builder.ToString(), truncated);
        }
    }

    private static void Apply(Func<string, string?> metadata, string key, Action<string> setter)
    {
        var value = metadata(key);
        if (string.IsNullOrWhiteSpace(value)) return;
        setter(value);
    }

    private static bool TryParseIndentedScalar(string line, int indent, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        if (!HasIndent(line, indent)) return false;

        var trimmed = line.Trim();
        var separatorIndex = trimmed.IndexOf(':');
        if (separatorIndex <= 0) return false;

        key = trimmed[..separatorIndex].Trim();
        value = trimmed[(separatorIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value);
    }

    private static string ParseAgeRating(string value)
    {
        if (int.TryParse(value, out var rating) && Enum.IsDefined(typeof(AgeRating), rating))
        {
            return ((AgeRating) rating).ToDescription();
        }

        return value;
    }

    private static string BuildTitleFromFileName(string filePath)
    {
        var title = Path.GetFileNameWithoutExtension(filePath);
        title = Regex.Replace(title, @"\s*#\d+\s*$", string.Empty);
        title = Regex.Replace(title, @"\s*\((?:리디|ridi|ridibooks?|알라딘|교보|네이버|카카오)[^)]*\)\s*$",
            string.Empty, RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s*\[[^\]]+\]", string.Empty);
        title = Regex.Replace(title, @"\s{2,}", " ");
        return title.Trim();
    }

    private readonly record struct CappedLine(string Text, bool IsTruncated);

    private sealed record LargeYamlInfo(Dictionary<string, string> Metadata, Dictionary<string, int> FilePages);

    private sealed record CachedYaml(long Length, DateTime LastWriteTimeUtc,
        Dictionary<string, string>? LargeMetadata, Dictionary<string, int>? LargeFilePages);
}
