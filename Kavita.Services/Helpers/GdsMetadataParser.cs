using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Kavita.Common.Extensions;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Metadata;
using Kavita.Services.Extensions;
using YamlDotNet.Serialization;

namespace Kavita.Services.Helpers;

public static class GdsMetadataParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    public static ComicInfo? GetComicInfo(string filePath, ComicInfo? baseInfo = null)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory)) return baseInfo;

        var yamlPath = GetMetadataPath(directory);
        if (string.IsNullOrEmpty(yamlPath)) return baseInfo;

        Dictionary<object, object?>? yaml;
        try
        {
            yaml = Deserializer.Deserialize<Dictionary<object, object?>>(File.ReadAllText(yamlPath));
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or IOException or InvalidOperationException or ArgumentException)
        {
            return BuildFallbackComicInfo(filePath, baseInfo);
        }

        if (yaml == null || !TryGetMap(yaml, "meta", out var meta)) return baseInfo;

        var info = baseInfo ?? new ComicInfo();

        if (string.IsNullOrWhiteSpace(info.Title))
        {
            info.Title = BuildTitleFromFileName(filePath);
        }

        Apply(meta, "Summary", value => info.Summary = value);
        Apply(meta, "Genres", value => info.Genre = value);
        Apply(meta, "Tags", value => info.Tags = value);
        Apply(meta, "Language", value => info.LanguageISO = value);
        Apply(meta, "Web Links", value => info.Web = value);
        Apply(meta, "Person Writers", value => info.Writer = value);
        Apply(meta, "Writer", value => info.Writer = value);
        Apply(meta, "Person Translator", value => info.Translator = value);
        Apply(meta, "Person Publisher", value => info.Publisher = value);
        Apply(meta, "Person Penciller", value => info.Penciller = value);
        Apply(meta, "Person Inker", value => info.Inker = value);
        Apply(meta, "Person Colorist", value => info.Colorist = value);
        Apply(meta, "Person Letterer", value => info.Letterer = value);
        Apply(meta, "Person CoverArtist", value => info.CoverArtist = value);
        Apply(meta, "Person Editor", value => info.Editor = value);
        Apply(meta, "Person Imprint", value => info.Imprint = value);
        Apply(meta, "Person Character", value => info.Characters = value);
        Apply(meta, "Person Team", value => info.Teams = value);
        Apply(meta, "Person Location", value => info.Locations = value);
        Apply(meta, "Age Rating", value => info.AgeRating = ParseAgeRating(value));

        Apply(meta, "Release Date", value =>
        {
            if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                info.Year = date.Year;
                info.Month = date.Month;
                info.Day = date.Day;
            }
        });
        Apply(meta, "Year", value =>
        {
            if (int.TryParse(value, out var year)) info.Year = year;
        });
        Apply(meta, "Month", value =>
        {
            if (int.TryParse(value, out var month)) info.Month = month;
        });
        Apply(meta, "Day", value =>
        {
            if (int.TryParse(value, out var day)) info.Day = day;
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
        try
        {
            var yaml = Deserializer.Deserialize<Dictionary<object, object?>>(File.ReadAllText(yamlPath));
            if (!TryGetMap(yaml, "files", out var files)) return false;

            foreach (var (sourceKey, value) in files)
            {
                if (!string.Equals(sourceKey.ToString()?.Trim(), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (value is not Dictionary<object, object?> fileMetadata) return false;
                if (!TryGetScalar(fileMetadata, "cover", out var cover)) return false;

                return TryNormalizeBase64Cover(cover, out encodedImage);
            }
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return TryGetCoverBase64FromLines(yamlPath, fileName, out encodedImage);
        }

        return TryGetCoverBase64FromLines(yamlPath, fileName, out encodedImage);
    }

    private static bool TryGetCoverBase64FromLines(string yamlPath, string fileName, out string encodedImage)
    {
        encodedImage = string.Empty;
        var inFiles = false;
        var inTargetFile = false;

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
        if (string.Equals(encodedImage, "TEXT", StringComparison.OrdinalIgnoreCase)) return false;
        if (encodedImage.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            encodedImage.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
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

    private static void Apply(IReadOnlyDictionary<object, object?> map, string key, Action<string> setter)
    {
        if (!TryGetScalar(map, key, out var value)) return;
        setter(value);
    }

    private static bool TryGetMap(IReadOnlyDictionary<object, object?> source, string key,
        out Dictionary<object, object?> map)
    {
        map = [];
        foreach (var (sourceKey, value) in source)
        {
            if (!string.Equals(sourceKey.ToString(), key, StringComparison.OrdinalIgnoreCase)) continue;
            if (value is not Dictionary<object, object?> nested) return false;
            map = nested;
            return true;
        }

        return false;
    }

    private static bool TryGetScalar(IReadOnlyDictionary<object, object?> source, string key, out string value)
    {
        value = string.Empty;
        foreach (var (sourceKey, rawValue) in source)
        {
            if (!string.Equals(sourceKey.ToString(), key, StringComparison.OrdinalIgnoreCase)) continue;
            value = rawValue?.ToString()?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
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
}
