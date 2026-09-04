using Hangfire;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
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
            .WithFolders([new FolderPath {Path = "/library/completed/"}]).Build();
        var requested = new SeriesBuilder("Series A").Build();
        var sibling = new SeriesBuilder("Series B").Build();
        requested.FolderPath = sibling.FolderPath = "/library/completed/category";
        requested.LowestFolderPath = "/library/completed/category/series-a";
        sibling.LowestFolderPath = "/library/completed/category/series-b";
        library.Series = [requested, sibling];
        context.Library.Add(library);
        await context.SaveChangesAsync();

        var scanner = new ScannerHelper(uow, _testOutputHelper).CreateServices();
        await scanner.ScanFolder(requested.FolderPath, includeOriginal ? requested.LowestFolderPath : string.Empty, abort);

        var jobs = JobStorage.Current.GetMonitoringApi().ScheduledJobs(0, 100);
        if (!includeOriginal && abort)
        {
            Assert.Empty(jobs);
            return;
        }
        var job = Assert.Single(jobs).Value.Job;
        Assert.Equal(includeOriginal ? nameof(ScannerService.ScanSeries) : nameof(ScannerService.ScanLibrary), job.Method.Name);
        Assert.Equal(includeOriginal ? requested.Id : library.Id, job.Args[0]);
    }

}
