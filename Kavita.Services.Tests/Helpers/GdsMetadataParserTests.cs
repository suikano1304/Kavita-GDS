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

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }
}
