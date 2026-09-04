using AutoMapper;
using EasyCaching.Core;
using Hangfire;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Scanner;
using Kavita.API.Services.SignalR;
using Kavita.Models.DTOs;
using Kavita.Models.Entities.User;
using Kavita.Server.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Scheduler = Kavita.Services.TaskScheduler;

namespace Kavita.Server.Tests.Controllers;

public class ScanFolderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiPreservesOriginalPathThroughScheduler(bool abort)
    {
        GlobalConfiguration.Configuration.UseInMemoryStorage();
        var scanner = Substitute.For<IScannerService>();
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.ScanFolder(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()).Returns(scheduled.Task);
        var constructor = typeof(Scheduler).GetConstructors().Single();
        var dependencies = constructor.GetParameters().Select(p =>
            p.ParameterType == typeof(IScannerService) ? scanner : Substitute.For([p.ParameterType], [])).ToArray();
        var scheduler = (Scheduler)constructor.Invoke(dependencies);
        var directory = new Kavita.Services.DirectoryService(
            Substitute.For<ILogger<Kavita.Services.DirectoryService>>(), new System.IO.Abstractions.FileSystem());
        var uow = Substitute.For<IUnitOfWork>();
        var user = new AppUser();
        uow.UserRepository.GetUserByAuthKey("fixture-key", ct: Arg.Any<CancellationToken>()).Returns(user);
        uow.UserRepository.IsUserAdminAsync(user, Arg.Any<CancellationToken>()).Returns(true);
        uow.LibraryRepository.GetLibraryDtosAsync(Arg.Any<CancellationToken>()).Returns(new List<LibraryDto>
        {
            new() {Folders = ["/library/completed/"]}
        });
        var controller = new LibraryController(directory, Substitute.For<ILogger<LibraryController>>(),
            Substitute.For<IMapper>(), scheduler, uow, Substitute.For<IEventHub>(),
            Substitute.For<ILibraryWatcher>(), Substitute.For<IEasyCachingProviderFactory>(),
            Substitute.For<ILocalizationService>())
        {
            ControllerContext = new ControllerContext {HttpContext = new DefaultHttpContext()}
        };

        var response = controller.ScanFolder(new ScanFolderDto
        {
            ApiKey = "fixture-key", FolderPath = "/library/completed/category/series-a", AbortOnNoSeriesMatch = abort
        });
        Assert.False(response.IsCompleted);
        scheduled.SetResult();
        Assert.IsType<OkResult>(await response);
        await scanner.Received(1).ScanFolder("/library/completed/category",
            "/library/completed/category/series-a", abort);
    }
}
