using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Reading;
using Kavita.Models.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Services.Tests;

public class OpdsPrefetchServiceTests
{
    [Fact]
    public async Task PersistentPressureHasBoundedRetriesAndHonorsCancellation()
    {
        var checks = 0;
        using var worker = new OpdsPrefetchService(Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<OpdsPrefetchService>>())
        {
            HeadroomAttempts = 3,
            HeadroomRetryDelay = TimeSpan.Zero,
            HeadroomCheck = _ => { checks++; return false; }
        };
        Assert.False(await worker.WaitForHeadroomAsync("/unused", CancellationToken.None));
        Assert.Equal(3, checks);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitForHeadroomAsync("/unused", cancelled.Token));
        Assert.Equal(3, checks);
    }

    [Fact]
    public void PressureUsesOwnCgroupAndHandlesContainerNamespaceWithoutEscapingMount()
    {
        var root = Path.Combine(Path.GetTempPath(), "opds-pressure-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "session.scope"));
        try
        {
            var host = Path.Combine(root, "io.pressure");
            var own = Path.Combine(root, "session.scope", "io.pressure");
            File.WriteAllText(host, "full avg10=80.00");
            File.WriteAllText(own, "full avg10=0.00");
            Assert.Equal(own, OpdsPrefetchService.ResolveIoPressurePath(root, "0::/session.scope\n"));
            Assert.Equal(host, OpdsPrefetchService.ResolveIoPressurePath(root, "0::/\n"));
            Assert.Equal(host, OpdsPrefetchService.ResolveIoPressurePath(root, "0::/../../ancestor/container\n"));
            File.Delete(host);
            Assert.Equal("/proc/pressure/io", OpdsPrefetchService.ResolveIoPressurePath(root, "0::/missing\n"));
            Assert.Equal("/proc/pressure/io", OpdsPrefetchService.ResolveIoPressurePath(root, "5:memory:/legacy\n"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    public async Task PreparesOnlyAccessibleSuccessorWhenResourcesPermit(bool accessible, bool headroom, bool recovers)
    {
        var uow = Substitute.For<IUnitOfWork>();
        var reader = Substitute.For<IReaderService>();
        var cache = Substitute.For<ICacheService>();
        var dirs = Substitute.For<IDirectoryService>();
        dirs.CacheDirectory.Returns("/unused");
        uow.UserRepository.HasAccessToChapter(7, 10, Arg.Any<CancellationToken>()).Returns(true);
        uow.ChapterRepository.GetChapterAsync(10, ct: Arg.Any<CancellationToken>()).Returns(new Chapter { Id = 10, VolumeId = 5, Range = "1" });
        uow.ChapterRepository.GetSeriesIdForChapter(10, Arg.Any<CancellationToken>()).Returns((int?)3);
        reader.GetNextChapterIdAsync(3, 5, 10, 7).Returns(11);
        var visited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uow.UserRepository.HasAccessToChapter(7, 11, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (!accessible) visited.TrySetResult();
            return Task.FromResult(accessible);
        });
        cache.Ensure(11, true, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            visited.TrySetResult();
            return Task.FromResult<Chapter?>(new Chapter { Id = 11, Range = "2" });
        });
        using var provider = new ServiceCollection().AddSingleton(uow).AddSingleton(reader)
            .AddSingleton(cache).AddSingleton(dirs).BuildServiceProvider();
        var checks = 0;
        using var worker = new OpdsPrefetchService(provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<OpdsPrefetchService>>())
        {
            HeadroomRetryDelay = TimeSpan.FromMilliseconds(5),
            HeadroomCheck = _ => { checks++; if (!headroom && !recovers) visited.TrySetResult(); return headroom || (recovers && checks > 1); }
        };
        // Concurrent page-prefetch requests for the current book must not duplicate work.
        for (var i = 0; i < 50; i++) worker.TryQueue(7, 10);
        await worker.StartAsync(CancellationToken.None);
        await visited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);
        await cache.Received(accessible && (headroom || recovers) ? 1 : 0).Ensure(11, true, Arg.Any<CancellationToken>());
        await cache.DidNotReceive().Ensure(10, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await reader.Received(1).GetNextChapterIdAsync(3, 5, 10, 7);
        // Preparing the successor does not recursively request another successor.
        await reader.DidNotReceive().GetNextChapterIdAsync(3, 5, 11, 7);
    }
}
