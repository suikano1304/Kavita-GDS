using System.IO.Compression;
using Kavita.Services.Helpers;

namespace Kavita.Services.Tests.Helpers;

public sealed class EpubManifestRepairHelperTests : IDisposable
{
    private readonly string _testDirectory = Path.Join(Path.GetTempPath(), "kavita-epub-repair-tests", Guid.NewGuid().ToString("N"));

    public EpubManifestRepairHelperTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void TryCreateDeduplicatedManifestCopy_ShouldNormalizeImageJpgMediaType()
    {
        var epubPath = CreateEpub("""
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata><meta name="cover" content="cover"/></metadata>
              <manifest>
                <item id="cover" href="Images/cover.jpg" media-type="image/jpg" />
                <item id="chapter" href="Text/chapter.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine><itemref idref="chapter" /></spine>
            </package>
            """, ("OEBPS/Images/cover.jpg", "image"), ("OEBPS/Text/chapter.xhtml", "<html />"));

        var repaired = EpubManifestRepairHelper.TryCreateDeduplicatedManifestCopy(epubPath, _testDirectory,
            out var repairedPath);

        Assert.True(repaired);
        Assert.Contains("media-type=\"image/jpeg\"", ReadOpf(repairedPath));
    }

    [Fact]
    public void TryCreateDeduplicatedManifestCopy_ShouldAddManifestItemForExistingGuideReference()
    {
        var epubPath = CreateEpub("""
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata />
              <manifest>
                <item id="chapter" href="Text/chapter.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine><itemref idref="chapter" /></spine>
              <guide><reference type="cover" href="Text/COVERPAGE.xhtml" /></guide>
            </package>
            """, ("OEBPS/Text/chapter.xhtml", "<html />"), ("OEBPS/Text/COVERPAGE.xhtml", "<html />"));

        var repaired = EpubManifestRepairHelper.TryCreateDeduplicatedManifestCopy(epubPath, _testDirectory,
            out var repairedPath);

        Assert.True(repaired);
        var opf = ReadOpf(repairedPath);
        Assert.Contains("href=\"Text/COVERPAGE.xhtml\"", opf);
        Assert.Contains("media-type=\"application/xhtml+xml\"", opf);
    }

    [Fact]
    public void TryCreateDeduplicatedManifestCopy_ShouldRemoveMissingSpineItemRefsWhenValidSpineRemains()
    {
        var epubPath = CreateEpub("""
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata />
              <manifest>
                <item id="chapter" href="Text/chapter.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine>
                <itemref idref="missing" />
                <itemref idref="chapter" />
              </spine>
            </package>
            """, ("OEBPS/Text/chapter.xhtml", "<html />"));

        var repaired = EpubManifestRepairHelper.TryCreateDeduplicatedManifestCopy(epubPath, _testDirectory,
            out var repairedPath);

        Assert.True(repaired);
        var opf = ReadOpf(repairedPath);
        Assert.DoesNotContain("idref=\"missing\"", opf);
        Assert.Contains("idref=\"chapter\"", opf);
    }

    [Fact]
    public void TryCreateDeduplicatedManifestCopy_ShouldContinueWhenDuplicateIdsRemain()
    {
        var epubPath = CreateEpub("""
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata />
              <manifest>
                <item id="chapter" href="Text/chapter.xhtml" media-type="application/xhtml+xml" />
                <item id="blank" />
                <item id="blank" />
              </manifest>
              <spine><itemref idref="chapter" /></spine>
              <guide><reference type="cover" href="Text/COVERPAGE.xhtml" /></guide>
            </package>
            """, ("OEBPS/Text/chapter.xhtml", "<html />"), ("OEBPS/Text/COVERPAGE.xhtml", "<html />"));

        var repaired = EpubManifestRepairHelper.TryCreateDeduplicatedManifestCopy(epubPath, _testDirectory,
            out var repairedPath);

        Assert.True(repaired);
        Assert.Contains("href=\"Text/COVERPAGE.xhtml\"", ReadOpf(repairedPath));
    }

    private string CreateEpub(string opf, params (string Path, string Content)[] entries)
    {
        var epubPath = Path.Join(_testDirectory, $"{Guid.NewGuid():N}.epub");
        using var archive = ZipFile.Open(epubPath, ZipArchiveMode.Create);

        AddEntry(archive, "META-INF/container.xml", """
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
              </rootfiles>
            </container>
            """);
        AddEntry(archive, "OEBPS/content.opf", opf);
        foreach (var entry in entries)
        {
            AddEntry(archive, entry.Path, entry.Content);
        }

        return epubPath;
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string ReadOpf(string epubPath)
    {
        using var archive = ZipFile.OpenRead(epubPath);
        var entry = archive.GetEntry("OEBPS/content.opf");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }
}
