using System.IO.Compression;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.Database;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.Reader;
using Kavita.Models.Entities.Enums;
using Kavita.Server.Controllers;
using Kavita.Services.Builders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Kavita.Server.Tests.Controllers;

public class BookPageCountTests
{
    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(480, 0, false)]
    [InlineData(480, 0, true)]
    [InlineData(480, 1, true)]
    [InlineData(1, 1, true)]
    public async Task ZeroPageMetadata_UpdatesSelectedFileAndTotalsExactlyOnce(int pages, int previousChapterPages, bool fileAlreadyRepaired)
    {
        var root = Path.Join(Path.GetTempPath(), "book-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Join(root, "book.epub");
        try
        {
            using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
                Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='content.opf'/></rootfiles></container>");
                Add("content.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='2.0'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>Fixture</dc:title></metadata><manifest><item id='body' href='body.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='body'/></spine></package>");
                Add("body.xhtml", "<html><body><p>Readable</p></body></html>");
            }
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new DataContext(new DbContextOptionsBuilder<DataContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var chapter = new ChapterBuilder("1").WithFile(new MangaFileBuilder(source, MangaFormat.Epub).Build()).Build();
            chapter.Pages = previousChapterPages;
            var selected = chapter.Files.Single(); selected.Pages = fileAlreadyRepaired ? pages : 0; selected.Bytes = new FileInfo(source).Length;
            var unrelated = new MangaFileBuilder(Path.Join(root, "alternate.txt"), MangaFormat.Text).Build(); unrelated.Pages = 7;
            chapter.Files.Add(unrelated);
            var volume = new VolumeBuilder("1").Build(); volume.Chapters.Add(chapter); volume.Pages = 100;
            var series = new SeriesBuilder("Fixture").Build(); series.Volumes.Add(volume); series.Pages = 100;
            var library = new LibraryBuilder("Fixture").Build(); library.Series.Add(series);
            db.Library.Add(library); await db.SaveChangesAsync();
            var unit = Substitute.For<IUnitOfWork>(); unit.DataContext.Returns(db);
            unit.ChapterRepository.GetChapterInfoDtoAsync(chapter.Id, Arg.Any<CancellationToken>()).Returns(new ChapterInfoDto
            { SeriesFormat = MangaFormat.Epub, Pages = previousChapterPages, SeriesId = series.Id, VolumeId = volume.Id, LibraryId = library.Id });
            var cache = Substitute.For<ICacheService>(); cache.Ensure(chapter.Id, false, Arg.Any<CancellationToken>()).Returns(chapter);
            cache.GetCachedFile(chapter).Returns(source);
            var books = Substitute.For<IBookService>(); books.GetNumberOfPages(source).Returns(pages);
            var directory = Substitute.For<IDirectoryService>(); directory.TempDirectory.Returns(root);
            var controller = new BookController(books, unit, cache, Substitute.For<ILocalizationService>(), directory)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            for (var i = 0; i < 2; i++)
            {
                var response = await controller.GetBookInfo(chapter.Id);
                Assert.Equal(pages, Assert.IsType<BookInfoDto>(Assert.IsType<OkObjectResult>(response.Result).Value).Pages);
            }
            db.ChangeTracker.Clear();
            Assert.Equal(pages, (await db.MangaFile.SingleAsync(f => f.Id == selected.Id)).Pages);
            Assert.Equal(7, (await db.MangaFile.SingleAsync(f => f.Id == unrelated.Id)).Pages);
            Assert.Equal(pages, (await db.Chapter.SingleAsync()).Pages);
            Assert.Equal(100 + pages - previousChapterPages, (await db.Volume.SingleAsync()).Pages);
            Assert.Equal(100 + pages - previousChapterPages, (await db.Series.SingleAsync()).Pages);
        }
        finally { Directory.Delete(root, true); }
    }
}
