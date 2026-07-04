using Kavita.Services.Helpers;

namespace Kavita.Services.Tests.Helpers;

public class GdsMetadataParserTests : IDisposable
{
    private readonly string _testDirectory = Path.Join(Path.GetTempPath(), "kavita-gds-yaml-tests", Guid.NewGuid().ToString("N"));
    private readonly string _bookPath;

    public GdsMetadataParserTests()
    {
        Directory.CreateDirectory(_testDirectory);
        _bookPath = Path.Join(_testDirectory, "sample.epub");
        File.WriteAllText(_bookPath, string.Empty);
    }

    [Fact]
    public void TryGetCoverBase64_ShouldReturnFalse_WhenYamlIsEmpty()
    {
        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), string.Empty);

        var result = GdsMetadataParser.TryGetCoverBase64(_bookPath, out var encodedImage);

        Assert.False(result);
        Assert.Equal(string.Empty, encodedImage);
    }

    [Fact]
    public void TryGetCoverBase64_ShouldReturnFalse_WhenYamlIsNulFilled()
    {
        File.WriteAllBytes(Path.Join(_testDirectory, "kavita.yaml"), new byte[1024]);

        var result = GdsMetadataParser.TryGetCoverBase64(_bookPath, out var encodedImage);

        Assert.False(result);
        Assert.Equal(string.Empty, encodedImage);
    }

    [Fact]
    public void TryGetCoverBase64_ShouldReturnFalse_WhenYamlCoverIsInvalidBase64()
    {
        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), """
            files:
                'sample.epub':
                    cover: not-base64
                    page: 1
            """);

        var result = GdsMetadataParser.TryGetCoverBase64(_bookPath, out var encodedImage);

        Assert.False(result);
        Assert.Equal(string.Empty, encodedImage);
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("https://example.test/cover.jpg")]
    public void TryGetCoverBase64_ShouldReturnFalse_WhenYamlCoverIsNotEmbeddedImage(string cover)
    {
        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), $$"""
            files:
                'sample.epub':
                    cover: {{cover}}
                    page: 1
            """);

        var result = GdsMetadataParser.TryGetCoverBase64(_bookPath, out var encodedImage);

        Assert.False(result);
        Assert.Equal(string.Empty, encodedImage);
    }

    [Fact]
    public void TryGetCoverBase64_ShouldUseExactFileNameMatch()
    {
        const string pngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=";
        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), $"""
            files:
                'not-sample.epub':
                    cover: data:image/png;base64,{pngBase64}
                    page: 1
            """);

        var result = GdsMetadataParser.TryGetCoverBase64(_bookPath, out var encodedImage);

        Assert.False(result);
        Assert.Equal(string.Empty, encodedImage);
    }

    [Fact]
    public void TryGetCoverBase64_ShouldUseLineFallback_WhenYamlParserCannotReadDocument()
    {
        const string pngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=";
        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), $"""
            files:
                'sample.epub':
                    cover: data:image/png;base64,{pngBase64}
                    page: 1
            broken: [unclosed
            """);

        var result = GdsMetadataParser.TryGetCoverBase64(_bookPath, out var encodedImage);

        Assert.True(result);
        Assert.Equal(pngBase64, encodedImage);
    }

    [Fact]
    public void TryGetCoverBase64_ShouldReturnTrue_WhenYamlHasValidBase64Cover()
    {
        const string pngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=";
        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), $"""
            files:
                'sample.epub':
                    cover: data:image/png;base64,{pngBase64}
                    page: 1
            """);

        var result = GdsMetadataParser.TryGetCoverBase64(_bookPath, out var encodedImage);

        Assert.True(result);
        Assert.Equal(pngBase64, encodedImage);
    }

    [Fact]
    public void GetComicInfo_ShouldApplyTopLevelMetaFields()
    {
        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), """
            meta:
                Summary: yaml summary
                Genres: sports, drama
                Tags: classic, scan
                Language: ko
                Writer: author name
                Release Date: 20260704
            """);

        var result = GdsMetadataParser.GetComicInfo(_bookPath);

        Assert.NotNull(result);
        Assert.Equal("yaml summary", result.Summary);
        Assert.Equal("sports, drama", result.Genre);
        Assert.Equal("classic, scan", result.Tags);
        Assert.Equal("ko", result.LanguageISO);
        Assert.Equal("author name", result.Writer);
        Assert.Equal(2026, result.Year);
        Assert.Equal(7, result.Month);
        Assert.Equal(4, result.Day);
    }

    [Fact]
    public void GetComicInfo_ShouldApplyTopLevelMetaFields_WhenYamlIsLarge()
    {
        var yamlPath = Path.Join(_testDirectory, "kavita.yaml");
        File.WriteAllText(yamlPath, """
            meta:
                Summary: large yaml summary
                Genres: sports, drama
                Tags: classic, scan
                Language: ko
                Writer: large author
                Release Date: 20260704
            files:
            """);
        File.AppendAllText(yamlPath, new string('x', 2 * 1024 * 1024 + 1));

        var result = GdsMetadataParser.GetComicInfo(_bookPath);

        Assert.NotNull(result);
        Assert.Equal("large yaml summary", result.Summary);
        Assert.Equal("sports, drama", result.Genre);
        Assert.Equal("classic, scan", result.Tags);
        Assert.Equal("ko", result.LanguageISO);
        Assert.Equal("large author", result.Writer);
        Assert.Equal(2026, result.Year);
        Assert.Equal(7, result.Month);
        Assert.Equal(4, result.Day);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryGetPageCount_ShouldReadFilePageFromYaml(bool largeYaml)
    {
        var yamlPath = Path.Join(_testDirectory, "kavita.yaml");
        File.WriteAllText(yamlPath, """
            action:
                first_cover: false
            files:
                sample.epub:
                    cover: TEXT
                    page: 321
                    wordcount: 0
            meta:
                Summary: yaml summary
            """);

        if (largeYaml)
        {
            File.AppendAllText(yamlPath, new string('x', 2 * 1024 * 1024 + 1));
        }

        var result = GdsMetadataParser.TryGetPageCount(_bookPath, out var pages);

        Assert.True(result);
        Assert.Equal(321, pages);
    }

    [Fact]
    public void GetComicInfoAndPageCount_ShouldReadRealGdsShapeWithoutCachingCoverPayload()
    {
        var filePath = Path.Join(_testDirectory, "Sample 01권 (리디)#124.zip");
        File.WriteAllText(filePath, string.Empty);
        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), $$"""
            action:
                first_cover: false
            files:
                Sample 01권 (리디)#124.zip:
                    cover: {{new string('A', 2 * 1024 * 1024 + 32)}}
                    page: 124
                    wordcount: 0
            meta:
                Age Rating: '3'
                Day: '06'
                Genres: 만화 e북,극화,액션
                Language: ko
                Month: 09
                Name: Sample
                Person Publisher: KOCN
                Person Writers: Writer Name
                Publication Status: '2'
                Release Date: '20130906'
                Summary: "summary with escaped\nline"
                Tags: 완결,액션물,한국
                Web Links: https://example.test/books/1
                Year: '2013'
            search:
                q: Sample
            """);

        var info = GdsMetadataParser.GetComicInfo(filePath);
        var hasPages = GdsMetadataParser.TryGetPageCount(filePath, out var pages);

        Assert.NotNull(info);
        Assert.Equal("summary with escaped\\nline", info.Summary);
        Assert.Equal("만화 e북,극화,액션", info.Genre);
        Assert.Equal("완결,액션물,한국", info.Tags);
        Assert.Equal("ko", info.LanguageISO);
        Assert.Equal("Writer Name", info.Writer);
        Assert.Equal("KOCN", info.Publisher);
        Assert.Equal("https://example.test/books/1", info.Web);
        Assert.Equal(2013, info.Year);
        Assert.Equal(9, info.Month);
        Assert.Equal(6, info.Day);
        Assert.True(hasPages);
        Assert.Equal(124, pages);
    }

    [Fact]
    public void GetComicInfo_ShouldIgnoreInvalidDatePartsWithoutDroppingMetadata()
    {
        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), """
            action:
                first_cover: false
            files:
                sample.epub:
                    page: 22
            meta:
                Age Rating: '3'
                Day: '31'
                Genres: 만화 e북,스포츠
                Language: ko
                Month: '13'
                Person Publisher: Publisher Name
                Person Writers: Writer Name
                Summary: "metadata should survive invalid date"
                Tags: 완결,한국
                Year: '2026'
            """);

        var info = GdsMetadataParser.GetComicInfo(_bookPath);
        var hasPages = GdsMetadataParser.TryGetPageCount(_bookPath, out var pages);

        Assert.NotNull(info);
        Assert.Equal("metadata should survive invalid date", info.Summary);
        Assert.Equal("만화 e북,스포츠", info.Genre);
        Assert.Equal("완결,한국", info.Tags);
        Assert.Equal("ko", info.LanguageISO);
        Assert.Equal("Writer Name", info.Writer);
        Assert.Equal("Publisher Name", info.Publisher);
        Assert.Equal(2026, info.Year);
        Assert.Equal(0, info.Month);
        Assert.Equal(31, info.Day);
        Assert.True(hasPages);
        Assert.Equal(22, pages);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }
}
