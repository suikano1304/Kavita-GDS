using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Claims;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Reading;
using Kavita.API.Store;
using Microsoft.Extensions.DependencyInjection;
using Kavita.Models.DTOs;
using Kavita.Models.DTOs.OPDS;
using Kavita.Models.DTOs.Progress;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Server.Controllers;
using Kavita.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Server.Tests.Controllers;

public class OpdsDownloadTests
{
    [Theory]
    [InlineData("OPDSy", 0, 1)]
    [InlineData("OPDSy", 183, 184)]
    [InlineData("KOReader", 0, 1)]
    [InlineData("KOReader", 183, 184)]
    public async Task StreamIndexMapsToOneBasedProgress(string agent, int index, int expected)
    {
        var page = Path.GetTempFileName();
        try
        {
            var cache = Substitute.For<ICacheService>();
            var reader = Substitute.For<IReaderService>();
            var directory = Substitute.For<IDirectoryService>();
            cache.Ensure(1, true, Arg.Any<CancellationToken>()).Returns(new Chapter {Id=1,Range="1",Pages=184});
            cache.GetCachedPagePath(1,index).Returns(page);
            directory.ReadFileAsync(page).Returns(new byte[]{1,2,3});
            var controller = Create(Substitute.For<IUnitOfWork>(),directory,cache,reader);
            controller.Request.Headers.UserAgent=agent;
            await controller.GetPageStreamedImage("fixture",1,1,1,1,index);
            await reader.Received(1).SaveOpdsProgress(Arg.Is<ProgressDto>(p=>p.PageNum==expected),7);
        }
        finally { File.Delete(page); }
    }

    [Theory]
    [InlineData("Panels", true, false)]
    [InlineData("Panels", false, false)]
    [InlineData("Moon", true, true)]
    [InlineData("Moon", false, false)]
    public async Task ProgressRequiresExplicitPermissionAndNonPanels(string agent, bool save, bool expected)
    {
        var page = Path.GetTempFileName();
        try
        {
            var uow = Substitute.For<IUnitOfWork>();
            var cache = Substitute.For<ICacheService>();
            var reader = Substitute.For<IReaderService>();
            var directory = Substitute.For<IDirectoryService>();
            cache.Ensure(1, true, Arg.Any<CancellationToken>()).Returns(new Chapter {Id = 1, Range = "1"});
            cache.GetCachedPagePath(1, 0).Returns(page);
            directory.ReadFileAsync(page).Returns(new byte[] {1, 2, 3});
            var controller = Create(uow, directory, cache, reader);
            controller.Request.Headers.UserAgent = agent;
            await controller.GetPageStreamedImage("fixture", 1, 1, 1, 1, 0, save);
            await reader.Received(expected ? 1 : 0).SaveOpdsProgress(Arg.Any<ProgressDto>(), 7);
            await reader.DidNotReceive().SaveOpdsProgress(Arg.Any<ProgressDto>(), Arg.Is<int>(i => i != 7));
        }
        finally { File.Delete(page); }
    }

    [Theory]
    [InlineData(".zip", MangaFormat.Archive, true, false)]
    [InlineData(".cbz", MangaFormat.Archive, true, false)]
    [InlineData(".jpg", MangaFormat.Image, true, true)]
    [InlineData(".epub", MangaFormat.Epub, false, false)]
    [InlineData(".txt", MangaFormat.Text, false, false)]
    [InlineData(".pdf", MangaFormat.Pdf, false, false)]
    public void DescriptorMatchesReturnedFormat(string extension, MangaFormat format, bool cbz, bool build)
    {
        var result = OpdsDownloadDescriptor.Create(1, [new MangaFileDto {FilePath = "book" + extension, Format = format, Pages = 6, Bytes = 42}]);
        Assert.Equal(cbz, result.IsCbz);
        Assert.Equal(build, result.BuildCbz);
        Assert.Equal(build ? "chapter-1.zip" : cbz ? "book.zip" : "book" + extension, result.UrlFilename);
        Assert.Equal(build ? null : (long?)42, result.Bytes);
    }

    [Fact]
    public async Task ConcurrentMergesIncludeEverySourceInNaturalOrderAndDeleteTemporaryFiles()
    {
        var root = Path.Join(Path.GetTempPath(), "kavita-opds-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var uow = Substitute.For<IUnitOfWork>();
            var cache = Substitute.For<ICacheService>();
            var directory = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), new FileSystem());
            // Use an isolated temp directory through the interface while reusing real directory enumeration.
            var io = Substitute.For<IDirectoryService>();
            io.TempDirectory.Returns(root);
            io.GetFilesWithExtension(Arg.Any<string>(), Arg.Any<string>()).Returns(call => directory.GetFilesWithExtension(call.ArgAt<string>(0), call.ArgAt<string>(1)));
            var sources = new List<MangaFile>
            {
                new() {Id = 1, FilePath = "volume10.zip", Format = MangaFormat.Archive, Pages = 3},
                new() {Id = 2, FilePath = "volume2.zip", Format = MangaFormat.Archive, Pages = 3}
            };
            uow.ChapterRepository.GetFilesForChapterAsync(1, Arg.Any<CancellationToken>()).Returns(sources);
            cache.ExtractChapterFiles(Arg.Any<string>(), Arg.Any<IReadOnlyList<MangaFile>>(), false).Returns(call =>
            {
                var dir = call.ArgAt<string>(0);
                Directory.CreateDirectory(dir);
                var file = call.ArgAt<IReadOnlyList<MangaFile>>(1)[0];
                foreach (var n in new[] {10, 2, 1}) File.WriteAllText(Path.Join(dir, $"{n}.jpg"), file.FilePath + ":" + n);
                return Task.CompletedTask;
            });
            var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Create(uow, io, cache, Substitute.For<IReaderService>()).DownloadFile("fixture", 1, 1, 1, "chapter.zip")));
            var hashes = new List<byte[]>();
            foreach (var result in results)
            {
                var response = Assert.IsType<FileStreamResult>(result);
                Assert.True(response.EnableRangeProcessing);
                Assert.Equal(OpdsDownloadDescriptor.ComicBookMime, response.ContentType);
                Assert.EndsWith(".cbz", response.FileDownloadName);
                using (response.FileStream)
                {
                    hashes.Add(System.Security.Cryptography.SHA256.HashData(response.FileStream));
                    response.FileStream.Position = 0;
                    using var zip = new ZipArchive(response.FileStream, ZipArchiveMode.Read, true);
                    Assert.Equal(6, zip.Entries.Count);
                    var contents = zip.Entries.Select(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); }).ToArray();
                    Assert.Equal(new[] {"volume2.zip:1", "volume2.zip:2", "volume2.zip:10", "volume10.zip:1", "volume10.zip:2", "volume10.zip:10"}, contents);
                }
            }
            Assert.All(hashes, h => Assert.Equal(hashes[0], h));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private static OpdsController Create(IUnitOfWork uow, IDirectoryService directory, ICacheService cache, IReaderService reader)
    {
        var userContext = Substitute.For<IUserContext>();
        userContext.GetUserIdOrThrow().Returns(7);
        var services = new ServiceCollection().AddSingleton(userContext).BuildServiceProvider();
        return new OpdsController(uow, new DownloadService(), directory, cache, reader,
            Substitute.For<ILocalizationService>(), Substitute.For<IOpdsService>(),
            new OpdsPrefetchService(services.GetRequiredService<IServiceScopeFactory>(), Substitute.For<ILogger<OpdsPrefetchService>>()))
        {
            ControllerContext = new ControllerContext {HttpContext = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7")], "test"))
            }}
        };
    }
}
