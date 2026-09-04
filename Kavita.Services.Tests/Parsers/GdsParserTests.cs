using System.IO.Abstractions.TestingHelpers;
using Kavita.Database.Tests;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Helpers;
using Kavita.Services.Scanner;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Services.Tests.Parsers;

public class GdsParserTests : AbstractFsTest
{
    private readonly GdsParser _parser;
    private readonly string _rootDirectory;

    public GdsParserTests()
    {
        var fileSystem = CreateFileSystem();
        _rootDirectory = Path.Join(DataDirectory, "GDS/");
        fileSystem.AddDirectory(_rootDirectory);

        var ds = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), fileSystem);
        _parser = new GdsParser(ds, new ImageParser(ds));
    }

    [Theory]
    [InlineData("완결 표식 작품/완결 표식 작품 12권 (완결).epub", 12)]
    [InlineData("완결 표식 작품/완결 표식 작품 276화 (완).txt", 276)]
    [InlineData("완결 표식 작품/완결 표식 작품 4권(完).pdf", 4)]
    [InlineData("완결 표식 작품/완결 표식 작품 226-230 @ 完.txt", 230)]
    [InlineData("완결 표식 작품/08권 完.epub", 8)]
    [InlineData("완결 표식 작품/000-200 完[txt].zip", 200)]
    [InlineData("완결 표식 작품/완결 표식 작품 151-171 완결.txt", 171)]
    public void Parse_GdsLibrary_EndMarker_ShouldSetTotalCount(string relativePath, int expectedTotalCount)
    {
        var filePath = Path.Join(_rootDirectory, relativePath);
        var rootPath = Path.GetDirectoryName(filePath)!;
        var actual = _parser.Parse(filePath, rootPath, _rootDirectory, LibraryType.GDS);

        Assert.NotNull(actual);
        Assert.True(actual.HasEndMarker);
        Assert.Equal(expectedTotalCount, ParsedCountHelper.GetTotalCount(actual));
    }

    [Fact]
    public void Parse_GdsLibrary_EndMarkerInSeriesName_ShouldNotSetTotalCount()
    {
        var filePath = Path.Join(_rootDirectory, "데스완노트완결/데스완노트완결 12권.epub");
        var rootPath = Path.GetDirectoryName(filePath)!;
        var actual = _parser.Parse(filePath, rootPath, _rootDirectory, LibraryType.GDS);

        Assert.NotNull(actual);
        Assert.False(actual.HasEndMarker);
        Assert.Null(ParsedCountHelper.GetTotalCount(actual));
    }

    [Fact]
    public void Parse_GdsLibrary_EndMarkerEmbeddedInFileTitle_ShouldNotSetTotalCount()
    {
        var filePath = Path.Join(_rootDirectory, "완결판 작품/완결판 작품 12권.epub");
        var rootPath = Path.GetDirectoryName(filePath)!;
        var actual = _parser.Parse(filePath, rootPath, _rootDirectory, LibraryType.GDS);

        Assert.NotNull(actual);
        Assert.False(actual.HasEndMarker);
        Assert.Null(ParsedCountHelper.GetTotalCount(actual));
    }

    [Theory]
    [InlineData("슛 Shoot/슛! 1부 01권 [1080x] (예스)#200.zip", "슛 Shoot 1부", "1")]
    [InlineData("슛 Shoot/슛! 2부 01권 [1080x] (예스)#186.zip", "슛 Shoot 2부", "1")]
    [InlineData("슛 Shoot 1부/슛! 1부 01권 [1080x] (예스)#200.zip", "슛 Shoot 1부", "1")]
    public void Parse_GdsLibrary_KoreanPartVolume_ShouldSplitSeriesByPart(string relativePath, string expectedSeries,
        string expectedVolume)
    {
        var filePath = Path.Join(_rootDirectory, relativePath);
        var rootPath = Path.GetDirectoryName(filePath)!;
        var actual = _parser.Parse(filePath, rootPath, _rootDirectory, LibraryType.GDS);

        Assert.NotNull(actual);
        Assert.Equal(expectedSeries, actual.Series);
        Assert.Equal(expectedVolume, actual.Volumes);
    }
}
