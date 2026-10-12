using AutoMapper;
using EasyCaching.Core;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Scanner;
using Kavita.API.Services.SignalR;
using Kavita.Models.DTOs;
using Kavita.Models.Entities.User;
using Kavita.Models.Scanner;
using Kavita.Server.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Server.Tests.Controllers;

public class ScanFolderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiPreservesOriginalPathThroughScheduler(bool abort)
    {
        var scheduler = Substitute.For<ITaskScheduler>();
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

        var response = await controller.ScanFolder(new ScanFolderDto
        {
            ApiKey = "fixture-key", FolderPath = "/library/completed/category/series-a", AbortOnNoSeriesMatch = abort
        });
        Assert.IsType<OkResult>(response);

        await scheduler.Received(1).EnqueueScanFolderAsync(Arg.Is<ScanFolderRequest>(request =>
            request.Folder == "/library/completed/category" &&
            request.ChangedPath == "/library/completed/category/series-a" &&
            request.AbortOnNoSeriesMatch == abort), TimeSpan.Zero);
    }
}
