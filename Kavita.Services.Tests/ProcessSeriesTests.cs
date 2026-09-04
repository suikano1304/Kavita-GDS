using Kavita.Services.Scanner;

namespace Kavita.Services.Tests;

public class ProcessSeriesTests
{
    [Theory]
    [InlineData("/mnt/gds/Series/Series 01권 [1080x] (리디)#198.zip", 198)]
    [InlineData("/mnt/gds/Series/Series 7화#44.cbz", 44)]
    [InlineData("/mnt/gds/Series/Series 2부 12권 (완결)#99999.zip", 99999)]
    [InlineData("/mnt/gds/GDRIVE/READING/만화/완결A/바/Sample/2부20권#116.zip", 116)]
    [InlineData("/mnt/gds/GDRIVE/READING/만화/완결A/가/Sample/Sample 12권 (리디)#124.zip", 124)]
    public void TryGetGdsFilenamePageHint_ShouldParseTrailingHashNumberAfterGdsNumberingMarker(string filePath, int expectedPages)
    {
        var result = ProcessSeries.TryGetGdsFilenamePageHint(filePath, out var pages);

        Assert.True(result);
        Assert.Equal(expectedPages, pages);
    }

    [Theory]
    [InlineData("/mnt/gds/Series/Series #12 extra.zip")]
    [InlineData("/mnt/gds/Series/Series #12.zip")]
    [InlineData("/mnt/gds/Series/Series #12화.zip")]
    [InlineData("/mnt/gds/Series/Series 01권#0.zip")]
    [InlineData("/mnt/gds/Series/Series 01권#123456.zip")]
    [InlineData("/mnt/gds/Series/Series 01권.zip")]
    public void TryGetGdsFilenamePageHint_ShouldRejectNonTrailingOrInvalidHints(string filePath)
    {
        var result = ProcessSeries.TryGetGdsFilenamePageHint(filePath, out var pages);

        Assert.False(result);
        Assert.Equal(0, pages);
    }

    #region UpdateSeriesMetadata



    #endregion

    #region UpdateVolumes



    #endregion

    #region UpdateChapters



    #endregion

    #region AddOrUpdateFileForChapter



    #endregion

    #region UpdateChapterFromComicInfo

    // public void UpdateChapterFromComicInfo_()
    // {
    //     // TODO: Do this
    //     var file = Path.Join(Directory.GetCurrentDirectory(), "../../../Test Data/ScannerService/Library/Manga/Hajime no Ippo/Hajime no Ippo Chapter 1.cbz");
    //     // Chapter and ComicInfo
    //     var chapter = new ChapterBuilder("1")
    //         .WithId(0)
    //         .WithFile(new MangaFileBuilder(file, MangaFormat.Archive).Build())
    //         .Build();
    //
    //     var ps = new ProcessSeries(Substitute.For<IUnitOfWork>(), Substitute.For<ILogger<ProcessSeries>>(),
    //         Substitute.For<IEventHub>(), Substitute.For<IDirectoryService>()
    //         , Substitute.For<ICacheHelper>(), Substitute.For<IReadingItemService>(), Substitute.For<IFileService>(),
    //         Substitute.For<IMetadataService>(),
    //         Substitute.For<IWordCountAnalyzerService>(),
    //         Substitute.For<ICollectionTagService>(), Substitute.For<IReadingListService>());
    //
    //     ps.UpdateChapterFromComicInfo(chapter, new ComicInfo()
    //     {
    //
    //     });
    // }

    #endregion
}
