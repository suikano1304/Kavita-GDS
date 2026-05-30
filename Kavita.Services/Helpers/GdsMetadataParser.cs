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

        var yaml = Deserializer.Deserialize<Dictionary<object, object?>>(File.ReadAllText(yamlPath));
        if (!TryGetMap(yaml, "meta", out var meta)) return baseInfo;

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

    public static bool TryGetCoverBase64(string filePath, out string encodedImage)
    {
        encodedImage = string.Empty;

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory)) return false;

        var yamlPath = GetMetadataPath(directory);
        if (string.IsNullOrEmpty(yamlPath)) return false;

        var fileName = Path.GetFileName(filePath);
        var quotedFileName = "'" + fileName.Replace("'", "''") + "':";
        var plainFileName = fileName + ":";

        var inFiles = false;
        var inTargetFile = false;
        var linesAfterFiles = 0;
        foreach (var line in File.ReadLines(yamlPath))
        {
            var trimmed = line.Trim();
            if (!inFiles)
            {
                inFiles = string.Equals(trimmed, "files:", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            linesAfterFiles++;
            if (linesAfterFiles > 40) return false;

            if (trimmed.StartsWith("cover:", StringComparison.OrdinalIgnoreCase))
            {
                encodedImage = trimmed["cover:".Length..].Trim();
                return !string.IsNullOrWhiteSpace(encodedImage);
            }

            if (!inTargetFile)
            {
                inTargetFile = string.Equals(trimmed, quotedFileName, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(trimmed, plainFileName, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) return false;
            if (line.StartsWith("    ", StringComparison.Ordinal) && !line.StartsWith("        ", StringComparison.Ordinal)) return false;
            if (!trimmed.StartsWith("cover:", StringComparison.OrdinalIgnoreCase)) continue;

            encodedImage = trimmed["cover:".Length..].Trim();
            return !string.IsNullOrWhiteSpace(encodedImage);
        }

        return false;
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
