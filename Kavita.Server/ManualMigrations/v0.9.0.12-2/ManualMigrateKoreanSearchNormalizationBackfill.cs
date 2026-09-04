using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kavita.Common.Extensions;
using Kavita.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kavita.Server.ManualMigrations.v0._9._0._12_2;

/// <summary>
/// GDS 0.9.0.12-2 introduced Unicode NFC normalization to <see cref="StringExtensions.ToNormalized"/>
/// (fixes Korean spacing/decomposed-Hangul search mismatches) and added new normalized search fields:
/// <c>Chapter.NormalizedTitleName</c> and <c>Library.NormalizedName</c>.
///
/// This migration recomputes every field that is derived from <see cref="StringExtensions.ToNormalized"/>
/// so that existing data benefits from the new normalization without requiring a full library rescan.
/// </summary>
public class ManualMigrateKoreanSearchNormalizationBackfill : ManualMigration
{
    protected override string MigrationName => "ManualMigrateKoreanSearchNormalizationBackfill";

    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(DataContext context, ILogger<Program> logger)
    {
        await BackfillSeriesAsync(context, logger);
        await BackfillChaptersAsync(context, logger);
        await BackfillLibrariesAsync(context, logger);
        await BackfillTagsAsync(context, logger);
        await BackfillGenresAsync(context, logger);
        await BackfillPeopleAsync(context, logger);
        await BackfillReadingListsAsync(context, logger);
        await BackfillCollectionsAsync(context, logger);
    }

    private static async Task BackfillSeriesAsync(DataContext context, ILogger<Program> logger)
    {
        var total = 0;
        var ids = await context.Series.Select(s => s.Id).ToListAsync();
        foreach (var chunk in Chunk(ids))
        {
            var series = await context.Series.Where(s => chunk.Contains(s.Id)).ToListAsync();
            foreach (var s in series)
            {
                s.NormalizedName = s.Name.ToNormalized();
                s.NormalizedLocalizedName = s.LocalizedName.ToNormalized();
            }
            total += series.Count;
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }
        logger.LogInformation("[ManualMigrateKoreanSearchNormalizationBackfill] Backfilled {Count} Series", total);
    }

    private static async Task BackfillChaptersAsync(DataContext context, ILogger<Program> logger)
    {
        var total = 0;
        var ids = await context.Chapter.Select(c => c.Id).ToListAsync();
        foreach (var chunk in Chunk(ids))
        {
            var chapters = await context.Chapter.Where(c => chunk.Contains(c.Id)).ToListAsync();
            foreach (var c in chapters)
            {
                c.NormalizedTitleName = c.TitleName.ToNormalized();
            }
            total += chapters.Count;
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }
        logger.LogInformation("[ManualMigrateKoreanSearchNormalizationBackfill] Backfilled {Count} Chapters", total);
    }

    private static async Task BackfillLibrariesAsync(DataContext context, ILogger<Program> logger)
    {
        var libraries = await context.Library.ToListAsync();
        foreach (var l in libraries)
        {
            l.NormalizedName = l.Name.ToNormalized();
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        logger.LogInformation("[ManualMigrateKoreanSearchNormalizationBackfill] Backfilled {Count} Libraries", libraries.Count);
    }

    private static async Task BackfillTagsAsync(DataContext context, ILogger<Program> logger)
    {
        var tags = await context.Tag.ToListAsync();
        foreach (var t in tags)
        {
            t.NormalizedTitle = t.Title.ToNormalized();
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        logger.LogInformation("[ManualMigrateKoreanSearchNormalizationBackfill] Backfilled {Count} Tags", tags.Count);
    }

    private static async Task BackfillGenresAsync(DataContext context, ILogger<Program> logger)
    {
        var genres = await context.Genre.ToListAsync();
        foreach (var g in genres)
        {
            g.NormalizedTitle = g.Title.ToNormalized();
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        logger.LogInformation("[ManualMigrateKoreanSearchNormalizationBackfill] Backfilled {Count} Genres", genres.Count);
    }

    private static async Task BackfillPeopleAsync(DataContext context, ILogger<Program> logger)
    {
        var people = await context.Person.Include(p => p.Aliases).ToListAsync();
        foreach (var p in people)
        {
            p.NormalizedName = p.Name.ToNormalized();
            foreach (var alias in p.Aliases)
            {
                alias.NormalizedAlias = alias.Alias.ToNormalized();
            }
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        logger.LogInformation("[ManualMigrateKoreanSearchNormalizationBackfill] Backfilled {Count} People", people.Count);
    }

    private static async Task BackfillReadingListsAsync(DataContext context, ILogger<Program> logger)
    {
        var readingLists = await context.ReadingList.ToListAsync();
        foreach (var rl in readingLists)
        {
            rl.NormalizedTitle = rl.Title.ToNormalized();
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        logger.LogInformation("[ManualMigrateKoreanSearchNormalizationBackfill] Backfilled {Count} ReadingLists", readingLists.Count);
    }

    private static async Task BackfillCollectionsAsync(DataContext context, ILogger<Program> logger)
    {
        var collections = await context.AppUserCollection.ToListAsync();
        foreach (var c in collections)
        {
            c.NormalizedTitle = c.Title.ToNormalized();
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        logger.LogInformation("[ManualMigrateKoreanSearchNormalizationBackfill] Backfilled {Count} Collections", collections.Count);
    }

    private static IEnumerable<List<int>> Chunk(List<int> ids)
    {
        for (var i = 0; i < ids.Count; i += BatchSize)
        {
            yield return ids.Skip(i).Take(BatchSize).ToList();
        }
    }
}
