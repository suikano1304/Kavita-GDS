using System.IO.Abstractions;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using HtmlAgilityPack;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.Models.DTOs.Archive;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using VersOne.Epub;

namespace Kavita.Services.Tests;

public sealed class IntegratedReaderRegressionTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "kavita-integrated-tests", Guid.NewGuid().ToString("N"));
    private readonly BookService _books;
    private readonly ArchiveService _archives;
    private readonly DirectoryService _directories;
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4u0AAAAASUVORK5CYII=");

    public IntegratedReaderRegressionTests()
    {
        Directory.CreateDirectory(_root);
        _directories = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), new FileSystem());
        _books = new BookService(Substitute.For<ILogger<BookService>>(), _directories,
            Substitute.For<IImageService>(), Substitute.For<IMediaErrorService>(), Substitute.For<IUnitOfWork>());
        _archives = new ArchiveService(Substitute.For<ILogger<ArchiveService>>(), _directories,
            Substitute.For<IImageService>(), Substitute.For<IMediaErrorService>());
    }

    private string Epub(string manifest, string spine, string extra = "", bool epub3 = false, params (string, string)[] files)
    {
        var path = Path.Join(_root, Guid.NewGuid() + ".epub");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(content); }
        Add("mimetype", "application/epub+zip");
        Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OEBPS/content.opf'/></rootfiles></container>");
        Add("OEBPS/content.opf", $"<package xmlns='http://www.idpf.org/2007/opf' version='{(epub3 ? "3.0" : "2.0")}' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>fixture</dc:identifier><dc:title>Fixture</dc:title><dc:language>en</dc:language>{extra}</metadata><manifest>{manifest}</manifest><spine>{spine}</spine></package>");
        Add("OEBPS/chapter.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Body</title></head><body><p>Readable text</p></body></html>");
        foreach (var (name, content) in files) Add(name, content);
        using var image = zip.CreateEntry("OEBPS/Images/cover image.png").Open(); image.Write(Png);
        return path;
    }
    private const string ChapterItem = "<item id='c' href='chapter.xhtml' media-type='application/xhtml+xml'/>";

    [Fact]
    public async Task DuplicateSpine_PreservesEveryOccurrenceAndLinksToFirst()
    {
        var path = Epub(ChapterItem + "<item id='alias' href='chapter.xhtml' media-type='application/xhtml+xml'/>", "<itemref idref='c'/><itemref idref='alias'/>");
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        using var lease = EpubBookOpener.Open(path, _root, BookService.LenientBookReaderOptions);
        Assert.Equal(2, lease.Book.GetReadingOrder().Count);
        Assert.Equal(0, (await _books.CreateKeyToPageMappingAsync(lease.Book))["OEBPS/chapter.xhtml"]);
        Assert.Equal(2, _books.GetNumberOfPages(path));
        foreach (var page in lease.Book.GetReadingOrder()) Assert.Contains("Readable text", await page.ReadContentAsync());
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task DeclaredAbsentNav_UsesReadableSpineAndReusesRepair()
    {
        var path = Epub(ChapterItem + "<item id='nav' href='missing.xhtml' properties='nav' media-type='application/xhtml+xml'/>", "<itemref idref='c'/>", epub3: true);
        using var first = EpubBookOpener.Open(path, _root, BookService.LenientBookReaderOptions);
        Assert.Single(first.Book.GetReadingOrder());
        Assert.NotEmpty(await first.Book.GetNavigationAsync());
        using var second = EpubBookOpener.Open(path, _root, BookService.LenientBookReaderOptions);
        Assert.Equal(first.Path, second.Path);
        Assert.NotEqual(path, first.Path);
    }

    [Theory]
    [InlineData("Text/../Images/cover%20image.png")]
    [InlineData("Images/COVER%20IMAGE.PNG")]
    public void HtmlCover_ResolvesActualImageWithoutChangingSource(string href)
    {
        var path = Epub(ChapterItem + "<item id='cover' href='cover.xhtml' media-type='application/xhtml+xml'/>", "<itemref idref='c'/>", "<meta name='cover' content='cover'/>", false,
            ("OEBPS/cover.xhtml", $"<html><body><img src='{href}'/></body></html>"));
        using var lease = EpubBookOpener.Open(path, _root, BookService.LenientBookReaderOptions);
        Assert.Equal(Png, lease.Book.ReadCover());
        Assert.Single(lease.Book.GetReadingOrder());
    }

    [Fact]
    public void NestedAnchorSlices_PreserveAncestorsAndDisjointText()
    {
        var results = new List<string>();
        foreach (var (start, end) in new[] { ("a", "b"), ("b", "") })
        {
            var doc = new HtmlDocument(); doc.LoadHtml("<body><section class='layout'><div><h1 id='a'>First</h1><p>Alpha</p><h1 id='b'>Second</h1><p>Beta</p></div></section></body>");
            var body = doc.DocumentNode.SelectSingleNode("//body");
            typeof(BookService).GetMethod("TrimBodyToVirtualPage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [body, start, end]);
            Assert.Contains("class='layout'", body.InnerHtml);
            results.Add(body.InnerText);
        }
        Assert.Equal(new[] { "FirstAlpha", "SecondBeta" }, results);
    }

    [Theory]
    [InlineData("A&B.woff2", "A%26B.woff2")]
    [InlineData("A%23B.woff2", "A%23B.woff2")]
    [InlineData("A+B.woff2", "A%2BB.woff2")]
    public void FontQuery_EncodesWholeValueOnce(string name, string expected)
    {
        object[] args = { $"@font-face {{ src: url('../Fonts/{name}'); }}", "https://test.invalid/?file=", "OEBPS/Styles/" };
        typeof(BookService).GetMethod("EscapeFontFamilyReferences", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
        Assert.Contains("file=OEBPS%2FFonts%2F" + expected, (string)args[0]);
    }

    [Fact]
    public void ZipDuplicatePaths_AllImagesSurviveWithStableNaturalOrder()
    {
        var source = Path.Join(_root, "pages.cbz");
        var names = new[] { "10.png", "2.png", "2.png", "folder/1.png", "00000000_00000001.png" };
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            foreach (var (name, index) in names.Select((name, index) => (name, index)))
            { using var output = zip.CreateEntry(name).Open(); output.Write(Png); output.WriteByte((byte)index); }
        var destination = Path.Join(_root, "pages");
        _archives.ExtractArchive(source, destination);
        var pages = Directory.GetFiles(destination, "*.png").Order().ToList();
        Assert.Equal(5, _archives.GetNumberOfPagesFromArchive(source));
        Assert.Equal(5, pages.Count);
        Assert.Equal(new byte[] { 4, 1, 2, 0, 3 }, pages.Select(p => File.ReadAllBytes(p)[^1]));
        // An incomplete cache with a page but without completion must be rebuilt.
        File.Delete(Path.Join(destination, ".archive-complete"));
        File.Delete(pages[2]);
        _archives.ExtractArchive(source, destination);
        Assert.Equal(5, Directory.GetFiles(destination, "*.png").Length);
    }

    [Fact]
    public async Task DownloadRequests_AreFreshDistinctCompleteAndFailureCleansOutput()
    {
        var source = Path.Join(_root, "source.txt"); File.WriteAllText(source, "old");
        var progress = new List<float>();
        var entries = new[] { new DownloadArchiveEntry(source, "Book_01.txt"), new DownloadArchiveEntry(source, "Book_02.txt") };
        var first = await _archives.CreateDownloadArchiveAsync(entries, p => { progress.Add(p.Item2); return Task.CompletedTask; });
        File.WriteAllText(source, "new");
        var second = await _archives.CreateDownloadArchiveAsync(entries, _ => Task.CompletedTask);
        try
        {
            Assert.NotEqual(first, second);
            Assert.All(progress, v => Assert.InRange(v, 0f, 0.99f));
            using var zip = ZipFile.OpenRead(second);
            Assert.Equal(2, zip.Entries.Count);
            foreach (var e in zip.Entries) { using var reader = new StreamReader(e.Open()); Assert.Equal("new", reader.ReadToEnd()); }
            var before = Directory.GetFiles(_directories.TempDirectory, "download-*.zip").Order().ToArray();
            await Assert.ThrowsAnyAsync<IOException>(() => _archives.CreateDownloadArchiveAsync(
                [entries[0], new DownloadArchiveEntry(Path.Join(_root, "missing"), "missing.txt")], _ => Task.CompletedTask));
            Assert.Equal(before, Directory.GetFiles(_directories.TempDirectory, "download-*.zip").Order());
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedSources_PreserveArchivesAndLooseImagesInBothOrders(bool imageFirst)
    {
        var zipPath = Path.Join(_root, "book.cbz");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        { using var output = zip.CreateEntry("1.png").Open(); output.Write(Png); output.WriteByte(1); }
        var loose = Path.Join(_root, "1.png"); File.WriteAllBytes(loose, [..Png, 2]);
        var files = new List<MangaFile> { new() {Id = 1, FilePath = zipPath, Format = MangaFormat.Archive}, new() {Id = 2, FilePath = loose, Format = MangaFormat.Image} };
        if (imageFirst) files.Reverse();
        var reader = Substitute.For<IReadingItemService>();
        reader.When(r => r.Extract(Arg.Any<string>(), Arg.Any<string>(), MangaFormat.Archive, 1))
            .Do(call => _archives.ExtractArchive(call.ArgAt<string>(0), call.ArgAt<string>(1)));
        var cache = new CacheService(Substitute.For<ILogger<CacheService>>(), Substitute.For<IUnitOfWork>(), _directories,
            reader, Substitute.For<IBookmarkService>(), Substitute.For<ILocalizationService>());
        var destination = Path.Join(_root, "mixed");
        await cache.ExtractChapterFiles(destination, files);
        Assert.Equal(new byte[] {2, 1},
            Directory.GetFiles(destination, "*.png").Order().Select(p => File.ReadAllBytes(p)[^1]));
    }

    [Fact]
    public async Task CancelledPackaging_DoesNotPublishPartialZip()
    {
        var source = Path.Join(_root, "source.bin"); File.WriteAllBytes(source, new byte[1024]);
        using var cancellation = new CancellationTokenSource();
        var before = Directory.GetFiles(_directories.TempDirectory, "download-*.zip").Order().ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _archives.CreateDownloadArchiveAsync(
            [new(source, "first.bin"), new(source, "second.bin")], _ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(before, Directory.GetFiles(_directories.TempDirectory, "download-*.zip").Order());
    }

    [Fact]
    public async Task LiveLogSnapshot_IsBoundedAndTemporaryResponseDeletesItsFile()
    {
        var source = Path.Join(_root, "active.log");
        await using var writer = new FileStream(source, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var initial = new byte[1024 * 1024];
        await writer.WriteAsync(initial); await writer.FlushAsync();
        var packaging = _archives.CreateDownloadArchiveAsync([new(source, "active.log", false)], _ => Task.CompletedTask);
        await writer.WriteAsync(Png); await writer.FlushAsync();
        var result = await packaging;
        using (var zip = ZipFile.OpenRead(result)) Assert.Equal(initial.Length, zip.Entries.Single().Length);
        await using (var response = new ActivityFileStream(result, true, CacheActivityGate.Enter()))
            Assert.True(response.Length > 0);
        Assert.False(File.Exists(result));
    }

    [Theory]
    [InlineData("1", "01권")]
    [InlineData("13.5", "13.5권")]
    [InlineData("1부 2", "1부 2권")]
    public void MetadataVolumeLabel_PreservesPartsAndDecimals(string value, string expected) => Assert.Equal(expected, DownloadFileName.VolumeLabel(value));

    [Fact]
    public async Task GlobalPurge_WaitsForActiveReaderLease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var purged = false;
        var lease = CacheActivityGate.Enter();
        var purge = Task.Run(() => { entered.SetResult(); CacheActivityGate.Purge(() => purged = true); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(purged);
        lease.Dispose();
        await purge.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(purged);
    }

    [Fact]
    public void BrokenSecondZipEntry_DoesNotPublishAndRetryRecoversAllPages()
    {
        var source = Path.Join(_root, "broken.cbz");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            foreach (var name in new[] { "1.png", "2.png" })
            { using var output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open(); output.Write(Png); }
        var original = File.ReadAllBytes(source);
        var broken = original.ToArray();
        // Unsupported compression in the second central directory entry fails after page one was written.
        var count = 0;
        for (var i = 0; i < broken.Length - 46; i++)
            if (BitConverter.ToUInt32(broken, i) == 0x02014b50 && ++count == 2)
            { broken[i + 10] = 99; break; }
        File.WriteAllBytes(source, broken);
        var destination = Path.Join(_root, "recovered");
        Assert.ThrowsAny<Exception>(() => _archives.ExtractArchive(source, destination));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetDirectories(_root, "recovered.staging-*"));
        File.WriteAllBytes(source, original);
        _archives.ExtractArchive(source, destination);
        Assert.Equal(2, Directory.GetFiles(destination, "*.png").Length);
    }

    [Fact]
    public void ChangedEpub_UsesNewRepairWhileExistingReaderStaysValid()
    {
        var path = Epub(ChapterItem + "<item id='nav' href='missing.xhtml' properties='nav' media-type='application/xhtml+xml'/>", "<itemref idref='c'/>", epub3: true);
        using var old = EpubBookOpener.Open(path, _root, BookService.LenientBookReaderOptions);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        { using var writer = new StreamWriter(zip.CreateEntry("changed.txt").Open()); writer.Write("new version"); }
        using var current = EpubBookOpener.Open(path, _root, BookService.LenientBookReaderOptions);
        Assert.NotEqual(old.Path, current.Path);
        Assert.Contains("Readable text", old.Book.GetReadingOrder()[0].ReadContent());
        Assert.Contains("Readable text", current.Book.GetReadingOrder()[0].ReadContent());
    }

    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("book:?. ", "book__")]
    [InlineData("LPT¹", "_LPT¹")]
    public void DownloadNames_AreWindowsSafeAndCaseInsensitiveUnique(string raw, string expected)
    {
        Assert.Equal(expected, DownloadFileName.Clean(raw));
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Book.cbz", DownloadFileName.Unique("Book", ".cbz", used));
        Assert.Equal("book_02.cbz", DownloadFileName.Unique("book", ".cbz", used));
    }

    public void Dispose() { Directory.Delete(_root, true); }
}
