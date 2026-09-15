using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Scanner;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Services.Tests;

public sealed class BookNavigationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kavita-navigation", Guid.NewGuid().ToString("N"));
    private readonly BookService _service;

    public BookNavigationTests()
    {
        Directory.CreateDirectory(_directory);
        var directories = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), new FileSystem());
        _service = new BookService(Substitute.For<ILogger<BookService>>(), directories,
            new ImageService(Substitute.For<ILogger<ImageService>>(), directories),
            Substitute.For<IMediaErrorService>(), Substitute.For<IUnitOfWork>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingOptionalNavigationTarget_PreservesReadableChapters(bool epub3, bool singleSpine)
    {
        var path = CreateBook(epub3, singleSpine);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var chapter = new Chapter { Range = "1", Pages = 2, Files = [new MangaFile { FilePath = path, Format = MangaFormat.Epub, Pages = 2 }] };
        var toc = await _service.GenerateTableOfContents(chapter);
        Assert.Equal(new[] { "First", "Second" }, toc.Select(item => item.Title));
        Assert.Equal(new[] { 0, 1 }, toc.Select(item => item.Page));
        Assert.Equal(2, _service.GetNumberOfPages(path));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    private string CreateBook(bool epub3, bool singleSpine)
    {
        var path = Path.Combine(_directory, "navigation.epub");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string value)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(value);
        }
        Add("mimetype", "application/epub+zip");
        Add("META-INF/container.xml", """
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
            """);
        var second = singleSpine ? "first.xhtml#second" : "second.xhtml";
        var extraManifest = singleSpine ? "" : "<item id=\"second\" href=\"second.xhtml\" media-type=\"application/xhtml+xml\"/>";
        var extraSpine = singleSpine ? "" : "<itemref idref=\"second\"/>";
        var navigation = epub3
            ? "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>"
            : "<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>";
        var spineToc = epub3 ? "" : " toc=\"ncx\"";
        Add("OEBPS/content.opf", $"""
            <package xmlns="http://www.idpf.org/2007/opf" version="{(epub3 ? "3.0" : "2.0")}" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">test</dc:identifier><dc:title>Navigation fixture</dc:title><dc:language>en</dc:language></metadata>
              <manifest><item id="first" href="first.xhtml" media-type="application/xhtml+xml"/>{extraManifest}{navigation}</manifest>
              <spine{spineToc}><itemref idref="first"/>{extraSpine}</spine>
            </package>
            """);
        Add("OEBPS/first.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>First</title></head><body><h1 id=\"first\">First</h1><p>Readable text.</p><h1 id=\"second\">Second</h1><p>More text.</p></body></html>");
        if (!singleSpine) Add("OEBPS/second.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Second</title></head><body><p>Second text.</p></body></html>");
        if (epub3) Add("OEBPS/nav.xhtml", $"""
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="copyright.xhtml">Missing</a></li><li><a href="first.xhtml#first">First</a></li><li><a href="{second}">Second</a></li></ol></nav></body></html>
            """);
        else Add("OEBPS/toc.ncx", $"""
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head/><docTitle><text>Contents</text></docTitle><navMap>
            <navPoint id="missing" playOrder="1"><navLabel><text>Missing</text></navLabel><content src="copyright.xhtml"/></navPoint>
            <navPoint id="first" playOrder="2"><navLabel><text>First</text></navLabel><content src="first.xhtml#first"/></navPoint>
            <navPoint id="second" playOrder="3"><navLabel><text>Second</text></navLabel><content src="{second}"/></navPoint>
            </navMap></ncx>
            """);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
