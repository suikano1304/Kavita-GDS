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
        };

        var parentFolder = GetSeriesFolderName(filePath);
        parentFolder = Regex.Replace(parentFolder, @"\[.*?\]", string.Empty, RegexOptions.None, Parser.RegexTimeout).Trim();
        parentFolder = Regex.Replace(parentFolder, @"\s-{1,2}$", string.Empty, RegexOptions.None, Parser.RegexTimeout).Trim();
        parentFolder = Regex.Replace(parentFolder, @"\s~{1,2}$", string.Empty, RegexOptions.None, Parser.RegexTimeout).Trim();
        ret.Series = parentFolder;

        ret.IsSpecial = ret.Volumes == Parser.LooseLeafVolume;
        if (Path.Exists(Path.Join(libraryRoot, ".special")) ||
            Path.Exists(Path.Join(Path.GetDirectoryName(filePath), ".special")))
        {
            ret.IsSpecial = true;
            ret.Volumes = Parser.LooseLeafVolume;
        }

        return ret.Series == string.Empty ? null : ret;
    }

    private static string GetSeriesFolderName(string filePath)
    {
        var parentPath = Path.GetDirectoryName(filePath);
        var parentFolder = Path.GetFileName(parentPath) ?? string.Empty;

        if (!FormatFolderNames.Contains(parentFolder)) return parentFolder;

        var seriesPath = Path.GetDirectoryName(parentPath);
        return Path.GetFileName(seriesPath) ?? parentFolder;
    }

    public override bool IsApplicable(string filePath, LibraryType type)
    {
        return type == LibraryType.GDS;
    }
}
