using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Helpers;
using Kavita.API.Services.SignalR;
using Kavita.Database;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Builders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Services.Tests;

public class CoverPageCountConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoverSave_PreservesReaderPageRepairAfterCoverGraphWasLoaded(bool representativeOnly)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new DataContext(new DbContextOptionsBuilder<DataContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var chapter = new ChapterBuilder("1").WithFile(new MangaFileBuilder("fixture.epub", MangaFormat.Epub).Build()).Build();
        var volume = new VolumeBuilder("1").Build(); volume.Chapters.Add(chapter);
        var series = new SeriesBuilder("Fixture").Build(); series.Volumes.Add(volume);
        var library = new LibraryBuilder("Fixture", LibraryType.GDS).WithSeries(series).Build();
        chapter.Pages = volume.Pages = series.Pages = chapter.Files.Single().Pages = 0;
        db.Library.Add(library); await db.SaveChangesAsync();
        var unit = Substitute.For<IUnitOfWork>(); unit.DataContext.Returns(db); unit.HasChanges().Returns(true);
        unit.SeriesRepository.GetFullSeriesForSeriesIdAsync(series.Id, Arg.Any<CancellationToken>()).Returns(series);
        unit.CommitAsync(Arg.Any<CancellationToken>()).Returns(async call => await db.SaveChangesAsync(call.Arg<CancellationToken>()) > 0);
        var covers = Substitute.For<IGdsCoverService>();
        async Task<GdsCoverGenerationResult> FinishSlowCover()
        {
            // An independent reader updates the database while the cover job retains its old graph.
            await db.MangaFile.ExecuteUpdateAsync(x => x.SetProperty(f => f.Pages, 480));
            await db.Chapter.ExecuteUpdateAsync(x => x.SetProperty(c => c.Pages, 480));
            await db.Volume.ExecuteUpdateAsync(x => x.SetProperty(v => v.Pages, 480));
            await db.Series.ExecuteUpdateAsync(x => x.SetProperty(s => s.Pages, 480));
            Assert.Equal(0, chapter.Pages);
            chapter.CoverImage = volume.CoverImage = series.CoverImage = "new-cover.webp";
            db.Entry(chapter).State = EntityState.Modified;
            db.Entry(volume).State = EntityState.Modified;
            db.Entry(series).State = EntityState.Modified;
            return new GdsCoverGenerationResult(true, []);
        }
        covers.ProcessSeriesCoverGen(series, Arg.Any<bool>(), Arg.Any<EncodeFormat>(), Arg.Any<CoverImageSize>(), Arg.Any<bool>())
            .Returns(_ => FinishSlowCover());
        covers.ProcessSeriesRepresentativeCoverGen(series, Arg.Any<bool>(), Arg.Any<EncodeFormat>(), Arg.Any<CoverImageSize>(), Arg.Any<bool>())
            .Returns(_ => FinishSlowCover());
        var events = Substitute.For<IEventHub>();
        events.SendMessageAsync(Arg.Any<string>(), Arg.Any<SignalRMessage>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var service = new MetadataService(Substitute.For<IServiceScopeFactory>(), unit, Substitute.For<ILogger<MetadataService>>(),
            events, Substitute.For<ICacheHelper>(), Substitute.For<IReadingItemService>(), Substitute.For<IDirectoryService>(),
            Substitute.For<IImageService>(), covers);
        if (representativeOnly)
            await service.GenerateRepresentativeCoverForSeries(new ServerSettingDto { EncodeMediaAs = EncodeFormat.WEBP }, library.Id, series.Id);
        else
            await service.GenerateCoversForSeries(series, EncodeFormat.WEBP, CoverImageSize.Default);
        db.ChangeTracker.Clear();
        Assert.Equal(480, (await db.MangaFile.SingleAsync()).Pages);
        Assert.Equal(480, (await db.Chapter.SingleAsync()).Pages);
        Assert.Equal(480, (await db.Volume.SingleAsync()).Pages);
        Assert.Equal(480, (await db.Series.SingleAsync()).Pages);
        Assert.Equal("new-cover.webp", (await db.Chapter.SingleAsync()).CoverImage);
        Assert.Equal("new-cover.webp", (await db.Volume.SingleAsync()).CoverImage);
        Assert.Equal("new-cover.webp", (await db.Series.SingleAsync()).CoverImage);
    }
}
