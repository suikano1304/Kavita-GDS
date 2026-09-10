using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kavita.Models.Entities.Enums;

namespace Kavita.Models.DTOs.OPDS;

/// <summary>Metadata shared by acquisition feeds and the download endpoint. Does not open media.</summary>
public sealed record OpdsDownloadDescriptor(bool BuildCbz, bool IsCbz, string Filename, string UrlFilename, int Pages, long? Bytes)
{
    public const string ComicBookMime = "application/vnd.comicbook+zip";

    public static OpdsDownloadDescriptor Create(int chapterId, IEnumerable<MangaFileDto> source)
    {
        var files = source.OrderBy(f => f.Id).ToList();
        var first = files.First();
        var comic = files.All(f => f.Format is MangaFormat.Archive or MangaFormat.Image);
        var build = comic && (files.Count > 1 || first.Format == MangaFormat.Image);
        var extension = Path.GetExtension(first.FilePath);
        var cbz = build || (first.Format == MangaFormat.Archive &&
            (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase)));
        var filename = build ? $"chapter-{chapterId}.cbz" : cbz ? Path.ChangeExtension(Path.GetFileName(first.FilePath), ".cbz") : Path.GetFileName(first.FilePath);
        return new(build, cbz, filename, cbz ? Path.ChangeExtension(filename, ".zip") : filename,
            build ? files.Sum(f => f.Pages) : first.Pages, build || first.Bytes <= 0 ? null : first.Bytes);
    }
}
