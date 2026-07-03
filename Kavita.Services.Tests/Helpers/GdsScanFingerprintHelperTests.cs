using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
using Kavita.Services.Helpers;

namespace Kavita.Services.Tests.Helpers;

public sealed class GdsScanFingerprintHelperTests : IDisposable
{
    private readonly string _testDirectory = Path.Join(Path.GetTempPath(), "kavita-gds-fingerprint-tests", Guid.NewGuid().ToString("N"));
    private readonly string _bookPath;

    public GdsScanFingerprintHelperTests()
    {
        Directory.CreateDirectory(_testDirectory);
        _bookPath = Path.Join(_testDirectory, "sample.epub");
        File.WriteAllText(_bookPath, "book");
    }

    [Fact]
    public void Calculate_ShouldUseFileTimestampsForContentLastModified()
    {
        var createdUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var modifiedUtc = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(_bookPath, createdUtc);
        File.SetLastWriteTimeUtc(_bookPath, modifiedUtc);

        var state = GdsScanFingerprintHelper.Calculate([CreateParserInfo()]);

        Assert.Equal(modifiedUtc, state.ContentLastModifiedUtc);
    }

    [Fact]
    public void Calculate_ShouldChangeFingerprintButNotContentDate_WhenYamlChanges()
    {
        var fileModifiedUtc = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(_bookPath, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(_bookPath, fileModifiedUtc);

        var before = GdsScanFingerprintHelper.Calculate([CreateParserInfo()]);

        var yamlPath = Path.Join(_testDirectory, "kavita.yaml");
        File.WriteAllText(yamlPath, "files: {}\n");
        File.SetCreationTimeUtc(yamlPath, new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(yamlPath, new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var after = GdsScanFingerprintHelper.Calculate([CreateParserInfo()]);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.Equal(fileModifiedUtc, after.ContentLastModifiedUtc);
    }

    [Fact]
    public void Calculate_ShouldIgnoreYaml_WhenSidecarsAreExcluded()
    {
        var before = GdsScanFingerprintHelper.Calculate([CreateParserInfo()], includeSidecars: false);

        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), "files: {}\n");

        var after = GdsScanFingerprintHelper.Calculate([CreateParserInfo()], includeSidecars: false);

        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    private ParserInfo CreateParserInfo()
    {
        return new ParserInfo
        {
            Series = "Sample",
            Filename = Path.GetFileName(_bookPath),
            FullFilePath = _bookPath,
            Format = MangaFormat.Epub,
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }
}
