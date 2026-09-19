using System.Collections.Generic;
using System.Linq;
using Kavita.Models.DTOs;
using Kavita.Services.Comparators;
using Kavita.Services.Extensions;

namespace Kavita.Services.Reading;

/// <summary>Selection over a canonical sequence; recommendations never rewrite completion.</summary>
public static class ReadingContinuation
{
    public static List<ChapterDto> Order(IEnumerable<VolumeDto> volumes) => volumes
        .OrderBy(v => v.IsSpecial())
        .ThenBy(v => v.MinNumber, ChapterSortComparerDefaultLast.Default)
        .ThenBy(v => v.Id)
        .SelectMany(v => v.Chapters.OrderBy(c => c.SortOrder).ThenBy(c => c.Id)).ToList();

    public static ChapterDto? Select(IList<ChapterDto> chapters, bool useRecommendationThreshold = true)
    {
        var last = -1;
        for (var i = 0; i < chapters.Count; i++)
            if (chapters[i].PagesRead > 0) last = i;
        var threshold = useRecommendationThreshold ? 90 : 100;
        return chapters.Skip(System.Math.Max(0, last)).FirstOrDefault(c => c.Pages <= 0 ||
            (long)c.PagesRead * 100 < (long)c.Pages * threshold);
    }
}
