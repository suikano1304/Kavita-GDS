using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Store;
using Kavita.Models.DTOs.Reader;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Server.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Kavita.Server.Tests.Controllers;

public class BookCacheEvictionTests
{
    [Theory]
    [InlineData(MangaFormat.Epub, false)]
    [InlineData(MangaFormat.Text, false)]
    [InlineData(MangaFormat.Epub, true)]
    [InlineData(MangaFormat.Text, true)]
    public async Task EvictedCacheIsRecreatedOnce(MangaFormat format, bool persistentFailure)
    {
        var source = Path.GetTempFileName();
        var cached = source + ".cached";
        try
        {
            var chapter = new Chapter {Id = 1, Range = "1", Files = [new MangaFile {Id = 1, FilePath = source, Format = format}]};
            var cache = Substitute.For<ICacheService>();
            cache.Ensure(1, false, Arg.Any<CancellationToken>()).Returns(chapter);
            cache.GetCachedFile(chapter).Returns(cached);
            var book = Substitute.For<IBookService>();
            var calls = 0;
            string Read()
            {
                if (++calls == 1 || persistentFailure) throw new FileNotFoundException("Evicted cache", cached);
                return "<p>Restored content</p>";
            }
            book.GetBookPageText(0, 1, cached, Arg.Any<CancellationToken>()).Returns(_ => Read());
            book.GetBookPage(7, 0, 1, cached, Arg.Any<string>(), Arg.Any<List<PersonalToCDto>>(), Arg.Any<List<AnnotationDto>>(), Arg.Any<CancellationToken>()).Returns(_ => Read());
            var user = Substitute.For<IUserContext>();user.GetUserIdOrThrow().Returns(7);
            var controller = new BookController(book, Substitute.For<IUnitOfWork>(), cache,
                Substitute.For<ILocalizationService>(), Substitute.For<IDirectoryService>())
            {
                ControllerContext = new ControllerContext {HttpContext = new DefaultHttpContext
                {RequestServices = new ServiceCollection().AddSingleton(user).BuildServiceProvider()}}
            };
            if (persistentFailure) await Assert.ThrowsAsync<FileNotFoundException>(() => controller.GetBookPage(1, 0));
            else Assert.Equal("<p>Restored content</p>", Assert.IsType<OkObjectResult>((await controller.GetBookPage(1, 0)).Result).Value);
            Assert.Equal(2, calls);
            await cache.Received(2).Ensure(1, false, Arg.Any<CancellationToken>());
        }
        finally {File.Delete(source);}
    }
}
