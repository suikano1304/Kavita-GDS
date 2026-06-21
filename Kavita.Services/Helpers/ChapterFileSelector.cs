using System.Collections.Generic;
using System.Linq;
using Kavita.Models.Entities;

namespace Kavita.Services.Helpers;

public static class ChapterFileSelector
{
    public static MangaFile? GetBestReadingFile(IEnumerable<MangaFile>? files)
    {
        var fileList = files?.ToList();
        if (fileList == null || fileList.Count == 0) return null;

        if (fileList.All(f => f.Bytes <= 0 && f.Pages <= 0))
        {
            return fileList[0];
        }

        return fileList
            .OrderByDescending(f => f.Bytes > 0 && f.Pages > 0)
            .ThenByDescending(f => f.Bytes > 0)
            .ThenByDescending(f => f.Pages > 0)
            .ThenBy(f => f.Id)
            .First();
    }
}
