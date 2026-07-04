using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Kavita.API.Services;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Metadata;
using Kavita.Models.Parser;

namespace Kavita.Services.Scanner;

/// <summary>
/// Parser for by275/soju GDS libraries. Series comes from the parent folder and volume from Korean-style filenames.
/// </summary>
public class GdsParser(IDirectoryService directoryService, IDefaultParser imageParser) : DefaultParser(directoryService)
{
    private const RegexOptions MatchOptions =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex EndMarkerRangeRegex = new(
        @"(?:^|[^\d#])(?<Range>\d+(?:\.\d+)?-\d+(?:\.\d+)?)(?=$|[\s_@~\-\[\]\(\)])",
        MatchOptions, Parser.RegexTimeout);

    private static readonly Regex KoreanPartVolumeRegex = new(
        @"(?<!\d)(?<Part>\d{1,3})\s*부\s*(?<Volume>\d{1,4}(?:\.\d+)?)\s*권",
        MatchOptions, Parser.RegexTimeout);

    private static readonly HashSet<string> FormatFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive", "archives", "book", "books", "cbz", "comic", "comics", "epub", "image", "images",
        "pdf", "rar", "text", "txt", "zip"
    };

    public override ParserInfo? Parse(string filePath, string rootPath, string libraryRoot, LibraryType type,
        bool enableMetadata = true, ComicInfo? comicInfo = null)
    {
        var fileName = directoryService.FileSystem.Path.GetFileNameWithoutExtension(filePath);

        if (Parser.IsCoverImage(directoryService.FileSystem.Path.GetFileName(filePath))) return null;

        if (Parser.IsImage(filePath))
        {
            return imageParser.Parse(filePath, rootPath, libraryRoot, LibraryType.Image, enableMetadata, comicInfo);
        }

        var ret = new ParserInfo
        {
            Filename = Path.GetFileName(filePath),
            Format = Parser.ParseFormat(filePath),
            Title = Parser.RemoveExtensionIfSupported(fileName)!,
            FullFilePath = Parser.NormalizePath(filePath),
            Series = string.Empty,
            ComicInfo = comicInfo,
            Chapters = Parser.DefaultChapter,
            Volumes = Parser.ParseVolume(fileName, type),
            Edition = string.Empty,
            HasEndMarker = Parser.HasEndMarker(fileName),
        };

        if (ret.HasEndMarker && Parser.IsLooseLeafVolume(ret.Volumes))
        {
            ret.Chapters = ParseEndMarkerRange(fileName);
        }

        var parentFolder = GetSeriesFolderName(filePath);
        parentFolder = Regex.Replace(parentFolder, @"\[.*?\]", string.Empty, RegexOptions.None, Parser.RegexTimeout).Trim();
        parentFolder = Regex.Replace(parentFolder, @"\s-{1,2}$", string.Empty, RegexOptions.None, Parser.RegexTimeout).Trim();
        parentFolder = Regex.Replace(parentFolder, @"\s~{1,2}$", string.Empty, RegexOptions.None, Parser.RegexTimeout).Trim();
        ret.Series = parentFolder;

        if (TryParseKoreanPart(fileName, out var part) && !SeriesAlreadyHasPartMarker(parentFolder, part))
        {
            ret.Series = $"{parentFolder} {part}부";
        }

        ret.IsSpecial = ret.Volumes == Parser.LooseLeafVolume && Parser.IsDefaultChapter(ret.Chapters);
        if (Path.Exists(Path.Join(libraryRoot, ".special")) ||
            Path.Exists(Path.Join(Path.GetDirectoryName(filePath), ".special")))
        {
            ret.IsSpecial = true;
            ret.Volumes = Parser.LooseLeafVolume;
        }

        FinalizeNumbers(ret);
        return ret.Series == string.Empty ? null : ret;
    }

    private static string ParseEndMarkerRange(string fileName)
    {
        var match = EndMarkerRangeRegex.Match(fileName.Replace("_", " "));
        return match.Success ? match.Groups["Range"].Value : Parser.DefaultChapter;
    }

    private static string GetSeriesFolderName(string filePath)
    {
        var parentPath = Path.GetDirectoryName(filePath);
        var parentFolder = Path.GetFileName(parentPath) ?? string.Empty;

        if (!FormatFolderNames.Contains(parentFolder)) return parentFolder;

        var seriesPath = Path.GetDirectoryName(parentPath);
        return Path.GetFileName(seriesPath) ?? parentFolder;
    }

    private static bool TryParseKoreanPart(string fileName, out string part)
    {
        part = string.Empty;
        var match = KoreanPartVolumeRegex.Match(fileName);
        if (!match.Success) return false;

        part = match.Groups["Part"].Value.TrimStart('0');
        if (string.IsNullOrWhiteSpace(part)) part = "0";
        return part != "0";
    }

    private static bool SeriesAlreadyHasPartMarker(string seriesName, string part)
    {
        return Regex.IsMatch(seriesName, $@"(?<!\d)0*{Regex.Escape(part)}\s*부(?!\d)", MatchOptions,
            Parser.RegexTimeout);
    }

    public override bool IsApplicable(string filePath, LibraryType type)
    {
        return type == LibraryType.GDS;
    }
}
