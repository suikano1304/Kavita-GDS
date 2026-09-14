using System.IO.Abstractions.TestingHelpers;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Plus;
using Kavita.API.Services.Reading;
using Kavita.API.Services.SignalR;
using Kavita.Database;
using Kavita.Database.Repositories;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.Progress;
using Kavita.Models.Entities.User;
using Kavita.Services.Reading;
using Kavita.Services.Builders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests;

public class OpdsProgressTests(ITestOutputHelper output) : AbstractDbTest(output)
{
    private static ReaderService Reader(IUnitOfWork uow) => new(uow,
        Substitute.For<ILogger<ReaderService>>(), Substitute.For<IEventHub>(),
        Substitute.For<IImageService>(), new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), new MockFileSystem()),
        Substitute.For<IScrobblingService>(), Substitute.For<IReadingSessionService>(),
        Substitute.For<IClientInfoAccessor>(), Substitute.For<IEntityNamingService>(),
        Substitute.For<ILocalizationService>(), Substitute.For<IBookService>());

    private static async Task Seed(DataContext context)
    {
        context.AppUser.AddRange(new AppUser {UserName = "reader-a"}, new AppUser {UserName = "reader-b"});
        var builder = new SeriesBuilder("Sequential series").WithLibraryId(1);
        foreach (var n in Enumerable.Range(1, 3))
            builder.WithVolume(new VolumeBuilder(n.ToString())
                .WithChapter(new ChapterBuilder(n.ToString()).WithPages(184).Build()).Build());
        context.Series.Add(builder.Build());
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static ProgressDto Progress(int chapter, int page) => new()
    { ChapterId = chapter, VolumeId = chapter, SeriesId = 1, LibraryId = 1, PageNum = page };

    [Fact]
    public async Task CompletedChapterSurvivesLeafNeighbourRequestsWithoutTimestampOrSessionChanges()
    {
        var (uow, context, _) = await CreateDatabase();
        await Seed(context);
        var reader = Reader(uow);
        await reader.SaveReadingProgress(Progress(1, 184), 1, false);
        var before = await context.AppUserProgresses.AsNoTracking().SingleAsync();
        var jobs = Hangfire.JobStorage.Current.GetMonitoringApi().EnqueuedCount("default");
        foreach (var page in Enumerable.Range(159, 25).Reverse())
            Assert.True(await reader.SaveOpdsProgress(Progress(1, page), 1));
        var after = await context.AppUserProgresses.AsNoTracking().SingleAsync();
        Assert.Equal(184, after.PagesRead);
        Assert.Equal(before.LastModifiedUtc, after.LastModifiedUtc);
        Assert.Equal(before.TotalReads, after.TotalReads);
        Assert.Equal(jobs, Hangfire.JobStorage.Current.GetMonitoringApi().EnqueuedCount("default"));
    }

    [Fact]
    public async Task OpdsAdvancesOnlyItsUserButExplicitWebRewindStillWorks()
    {
        var (uow, context, _) = await CreateDatabase();
        await Seed(context);
        var reader = Reader(uow);
        await reader.SaveReadingProgress(Progress(1, 80), 2, false);
        foreach (var page in new[] {0, 30, 5, 60, 30})
            await reader.SaveOpdsProgress(Progress(1, page), 1);
        Assert.Equal(60, (await context.AppUserProgresses.AsNoTracking().SingleAsync(p => p.AppUserId == 1)).PagesRead);
        Assert.Equal(80, (await context.AppUserProgresses.AsNoTracking().SingleAsync(p => p.AppUserId == 2)).PagesRead);
        context.ChangeTracker.Clear();
        await reader.SaveReadingProgress(Progress(1, 5), 1, false);
        Assert.Equal(5, (await context.AppUserProgresses.AsNoTracking().SingleAsync(p => p.AppUserId == 1)).PagesRead);
        await reader.SaveReadingProgress(Progress(1, 0), 1, false);
        context.ChangeTracker.Clear();
        await reader.SaveOpdsProgress(Progress(1, 10), 1);
        Assert.Equal(10, (await context.AppUserProgresses.AsNoTracking().SingleAsync(p => p.AppUserId == 1)).PagesRead);
    }

    [Theory]
    [InlineData(165, 2)] // Below 90%, no rounding up.
    [InlineData(166, 3)] // Above 90%, retain the page while recommending the next chapter.
    [InlineData(184, 3)]
    public async Task RecommendationUsesFurthestProgressAndDoesNotRewriteIt(int pages, int expected)
    {
        var (uow, context, _) = await CreateDatabase();
        await Seed(context);
        var reader = Reader(uow);
        await reader.SaveReadingProgress(Progress(2, pages), 1, false);
        await reader.SaveReadingProgress(Progress(1, 184), 1, false); // Newer manual edit must not move backwards.
        Assert.Equal(expected, (await reader.GetContinuePoint(1, 1))!.Id);
        Assert.Equal(pages, (await context.AppUserProgresses.AsNoTracking().SingleAsync(p => p.ChapterId == 2)).PagesRead);
        Assert.Equal(1, (await reader.GetContinuePoint(1, 2))!.Id);
        await reader.SaveReadingProgress(Progress(3, 184), 1, false);
        Assert.Null(await reader.GetContinuePoint(1, 1));
    }

    [Theory]
    [InlineData(89, 2)]
    [InlineData(90, 3)]
    public async Task ExactNinetyPercentBoundaryDoesNotChangeCompletionSync(int pages, int expected)
    {
        var (uow, context, _) = await CreateDatabase();
        await Seed(context);
        var chapter = await context.Chapter.SingleAsync(c => c.Id == 2);
        chapter.Pages = 100;
        await context.SaveChangesAsync();
        var reader = Reader(uow);
        await reader.SaveReadingProgress(Progress(2, pages), 1, false);
        Assert.Equal(expected, (await reader.GetContinuePoint(1, 1))!.Id);
        Assert.Equal(2, (await reader.GetContinuePoint(1, 1, useRecommendationThreshold: false))!.Id);
        Assert.Equal(pages, (await context.AppUserProgresses.AsNoTracking().SingleAsync()).PagesRead);
    }

    [Fact]
    public async Task ConcurrentFirstRequestsCreateOneRowAndKeepTheHighestPage()
    {
        var (_, context, mapper) = await CreateDatabase();
        await Seed(context);
        var path = Path.Join(Path.GetTempPath(), "opds-progress-" + Guid.NewGuid() + ".db");
        try
        {
            await using (var copy = new SqliteConnection("Data Source=" + path))
            {
                await copy.OpenAsync();
                ((SqliteConnection)context.Database.GetDbConnection()).BackupDatabase(copy);
            }
            await Task.WhenAll(Enumerable.Range(159, 25).Reverse().Select(page => Task.Run(async () =>
            {
                await using var concurrent = new DataContext(new DbContextOptionsBuilder<DataContext>()
                    .UseSqlite("Data Source=" + path + ";Pooling=False").Options);
                await new AppUserProgressRepository(concurrent, mapper).AdvanceOpdsProgressAsync(Progress(1, page), 1);
            })));
            await using var verify = new DataContext(new DbContextOptionsBuilder<DataContext>()
                .UseSqlite("Data Source=" + path + ";Pooling=False").Options);
            var row = await verify.AppUserProgresses.SingleAsync();
            Assert.Equal(183, row.PagesRead);
            Assert.Equal(0, row.TotalReads);
        }
        finally { File.Delete(path); }
    }
}
