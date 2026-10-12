using Hangfire;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Scanner;
using Kavita.Services.Scanner;
using Kavita.Services.Tests.Helpers;
using Xunit.Abstractions;

namespace Kavita.Services.Tests;

[CollectionDefinition("Scan folder jobs", DisableParallelization = true)]
public class ScanFolderJobCollection;

[Collection("Scan folder jobs")]
public class ScanFolderSchedulingTests(ITestOutputHelper output) : AbstractDbTest(output)
{
    private readonly ITestOutputHelper _testOutputHelper = output;
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ScanFolder_SharedParentSchedulesOnlyRequestedSeries(bool abort, bool includeOriginal)
    {
        var (uow, context, _) = await CreateDatabase();
        var library = new LibraryBuilder("Folder request fixture", LibraryType.GDS)
            .WithFolders([new FolderPath {Path = Parser.NormalizePath(Path.GetFullPath("/library/completed/"))}]).Build();
        var requested = new SeriesBuilder("Series A").Build();
        var sibling = new SeriesBuilder("Series B").Build();
        requested.FolderPath = sibling.FolderPath = Parser.NormalizePath(Path.GetFullPath("/library/completed/category"));
        requested.LowestFolderPath = Parser.NormalizePath(Path.GetFullPath("/library/completed/category/series-a"));
        sibling.LowestFolderPath = Parser.NormalizePath(Path.GetFullPath("/library/completed/category/series-b"));
        library.Series = [requested, sibling];
        context.Library.Add(library);
        await context.SaveChangesAsync();

        var scanner = new ScannerHelper(uow, _testOutputHelper).CreateServices();
        await scanner.ScanFolder(new ScanFolderRequest(requested.FolderPath,
            includeOriginal ? requested.LowestFolderPath : string.Empty, abort));

        var jobs = ScanJobQueue.Read().Jobs.Where(job => job.IsWaiting).ToList();
        if (!includeOriginal && abort)
        {
            Assert.Empty(jobs);
            return;
        }
        var job = Assert.Single(jobs);
        Assert.Equal(includeOriginal ? requested.Id : null, job.Target.SeriesId);
        Assert.Equal(library.Id, job.Target.LibraryId);
    }

}
