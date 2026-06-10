using System.IO.Abstractions;
using Kavita.API.Database;
using Kavita.API.Repositories;
using Kavita.API.Services;
using Kavita.API.Services.Helpers;
using Kavita.API.Services.SignalR;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Builders;
using Kavita.Services.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Services.Tests;

public sealed class GdsCoverServiceTests : IDisposable
{
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=";

    private readonly string _testDirectory = Path.Join(Path.GetTempPath(), "kavita-gds-cover-tests", Guid.NewGuid().ToString("N"));
    private readonly string _coverDirectory;
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IImageService _imageService = Substitute.For<IImageService>();
    private readonly IReadingItemService _readingItemService = Substitute.For<IReadingItemService>();
    private readonly GdsCoverService _service;

    public GdsCoverServiceTests()
    {
        _coverDirectory = Path.Join(_testDirectory, "covers");
        Directory.CreateDirectory(_coverDirectory);

        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.FileSystem.Returns(new FileSystem());
        directoryService.CoverImageDirectory.Returns(_coverDirectory);

        var eventHub = Substitute.For<IEventHub>();
        eventHub.SendMessageAsync(Arg.Any<string>(), Arg.Any<SignalRMessage>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _unitOfWork.SeriesRepository.Returns(Substitute.For<ISeriesRepository>());
        _unitOfWork.VolumeRepository.Returns(Substitute.For<IVolumeRepository>());
        _unitOfWork.ChapterRepository.Returns(Substitute.For<IChapterRepository>());

        _service = new GdsCoverService(_unitOfWork, Substitute.For<ILogger<GdsCoverService>>(), eventHub,
            new CacheHelper(new FileService(new FileSystem())), _readingItemService, directoryService, _imageService);
    }

    [Fact]
    public async Task ProcessSeriesCoverGen_TextOnlyWithYamlBase64_PrefersYamlCoverOverTextTitle()
    {
        var textFile = CreateMediaFile("text.txt", MangaFormat.Text, bytes: 100);
        WriteYamlCover("text.txt", $"data:image/png;base64,{PngBase64}");
        File.WriteAllText(Path.Join(_coverDirectory, "v1_c1.png"), string.Empty);
        _imageService.CreateThumbnailFromBase64(PngBase64, "v1_c1", EncodeFormat.PNG, Arg.Any<int>(), Arg.Any<string?>())
            .Returns("yaml-cover.png");
        var chapter = CreateChapter(1, "1", textFile);
        var volume = CreateVolume(1, chapter);
        var series = CreateSeries(1, volume);

        var result = await _service.ProcessSeriesCoverGen(series, false, EncodeFormat.PNG, CoverImageSize.Default);

        Assert.True(result.Handled);
        Assert.Equal("yaml-cover.png", chapter.CoverImage);
        Assert.Equal("yaml-cover.png", volume.CoverImage);
        Assert.Equal("yaml-cover.png", series.CoverImage);
    }

    [Fact]
    public async Task ProcessSeriesCoverGen_TextOnlyWithTextCoverHint_UsesTitleFallback()
    {
        var textFile = CreateMediaFile("text.txt", MangaFormat.Text, bytes: 100);
        WriteYamlCover("text.txt", "TEXT");
        File.WriteAllText(Path.Join(_coverDirectory, "v1_c1.png"), string.Empty);
        var chapter = CreateChapter(1, "1", textFile);
        var volume = CreateVolume(1, chapter);
        var series = CreateSeries(1, volume);

        var result = await _service.ProcessSeriesCoverGen(series, false, EncodeFormat.PNG, CoverImageSize.Default);

        Assert.True(result.Handled);
        Assert.Equal("v1_c1.png", chapter.CoverImage);
        Assert.Equal("v1_c1.png", volume.CoverImage);
        Assert.Equal("v1_c1.png", series.CoverImage);
    }

    [Fact]
    public async Task ProcessSeriesCoverGen_TextOnlyWithInvalidYamlCover_UsesTitleFallback()
    {
        var textFile = CreateMediaFile("text.txt", MangaFormat.Text, bytes: 100);
        WriteYamlCover("text.txt", "not-base64");
        File.WriteAllText(Path.Join(_coverDirectory, "v1_c1.png"), string.Empty);
        var chapter = CreateChapter(1, "1", textFile);
        var volume = CreateVolume(1, chapter);
        var series = CreateSeries(1, volume);

        var result = await _service.ProcessSeriesCoverGen(series, false, EncodeFormat.PNG, CoverImageSize.Default);

        Assert.True(result.Handled);
        Assert.Equal("v1_c1.png", chapter.CoverImage);
        Assert.Equal("v1_c1.png", volume.CoverImage);
        Assert.Equal("v1_c1.png", series.CoverImage);
    }

    [Fact]
    public async Task ProcessSeriesCoverGen_ForceRefreshWithExistingTextCover_ReplacesWithYamlCover()
    {
        var textFile = CreateMediaFile("text.txt", MangaFormat.Text, bytes: 100);
        WriteYamlCover("text.txt", $"data:image/png;base64,{PngBase64}");
        File.WriteAllText(Path.Join(_coverDirectory, "old-title.png"), string.Empty);
        _imageService.CreateThumbnailFromBase64(PngBase64, "v1_c1", EncodeFormat.PNG, Arg.Any<int>(), Arg.Any<string?>())
            .Returns("new-yaml-cover.png");
        var chapter = CreateChapter(1, "1", textFile);
        chapter.CoverImage = "old-title.png";
        var volume = CreateVolume(1, chapter);
        volume.CoverImage = "old-title.png";
        var series = CreateSeries(1, volume);
        series.CoverImage = "old-title.png";

        var result = await _service.ProcessSeriesCoverGen(series, true, EncodeFormat.PNG, CoverImageSize.Default);

        Assert.True(result.Handled);
        Assert.Equal("new-yaml-cover.png", chapter.CoverImage);
        Assert.Equal("new-yaml-cover.png", volume.CoverImage);
        Assert.Equal("new-yaml-cover.png", series.CoverImage);
    }

    [Fact]
    public async Task ProcessSeriesCoverGen_MixedTextAndEpub_PrefersEpubMediaCoverForSeriesAndVolume()
    {
        var textFile = CreateMediaFile("text.txt", MangaFormat.Text, bytes: 100);
        var epubFile = CreateMediaFile("book.epub", MangaFormat.Epub, bytes: 100);
        File.WriteAllText(Path.Join(_coverDirectory, "v1_c1.png"), string.Empty);
        _readingItemService.GetCoverImage(epubFile.FilePath, "v1_c2", MangaFormat.Epub, EncodeFormat.PNG, CoverImageSize.Default)
            .Returns("epub-cover.png");

        var textChapter = CreateChapter(1, "1", textFile);
        var epubChapter = CreateChapter(2, "2", epubFile);
        var volume = CreateVolume(1, textChapter, epubChapter);
        var series = CreateSeries(1, volume);

        var result = await _service.ProcessSeriesCoverGen(series, false, EncodeFormat.PNG, CoverImageSize.Default);

        Assert.True(result.Handled);
        Assert.Equal("v1_c1.png", textChapter.CoverImage);
        Assert.Equal("epub-cover.png", epubChapter.CoverImage);
        Assert.Equal("epub-cover.png", volume.CoverImage);
        Assert.Equal("epub-cover.png", series.CoverImage);
    }

    [Fact]
    public async Task ProcessSeriesCoverGen_TextOnly_UsesTextTitleCoverAsRepresentativeWhenNoMediaExists()
    {
        var textFile = CreateMediaFile("text.txt", MangaFormat.Text, bytes: 100);
        File.WriteAllText(Path.Join(_coverDirectory, "v1_c1.png"), string.Empty);
        var chapter = CreateChapter(1, "1", textFile);
        var volume = CreateVolume(1, chapter);
        var series = CreateSeries(1, volume);

        var result = await _service.ProcessSeriesCoverGen(series, false, EncodeFormat.PNG, CoverImageSize.Default);

        Assert.True(result.Handled);
        Assert.Equal("v1_c1.png", chapter.CoverImage);
        Assert.Equal("v1_c1.png", volume.CoverImage);
        Assert.Equal("v1_c1.png", series.CoverImage);
    }

    [Fact]
    public async Task ProcessSeriesCoverGen_FolderCover_PreservesSeriesCoverButDoesNotOverrideChapterOrVolume()
    {
        var folder = Path.Join(_testDirectory, "series");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Join(folder, "cover.jpg"), "folder-cover");

        var epubFile = CreateMediaFile("book.epub", MangaFormat.Epub, bytes: 100);
        _readingItemService.GetCoverImage(epubFile.FilePath, "v1_c1", MangaFormat.Epub, EncodeFormat.PNG, CoverImageSize.Default)
            .Returns("epub-cover.png");
        var chapter = CreateChapter(1, "1", epubFile);
        var volume = CreateVolume(1, chapter);
        var series = CreateSeries(1, volume);
        series.FolderPath = folder;

        var result = await _service.ProcessSeriesCoverGen(series, false, EncodeFormat.PNG, CoverImageSize.Default);

        Assert.True(result.Handled);
        Assert.Equal("_s1.jpg", series.CoverImage);
        Assert.Equal("epub-cover.png", chapter.CoverImage);
        Assert.Equal("epub-cover.png", volume.CoverImage);
    }

    private void WriteYamlCover(string fileName, string cover)
    {
        File.WriteAllText(Path.Join(_testDirectory, "kavita.yaml"), $"""
            files:
                '{fileName}':
                    cover: {cover}
                    page: 1
            """);
    }

    private MangaFile CreateMediaFile(string fileName, MangaFormat format, long bytes)
    {
        var path = Path.Join(_testDirectory, fileName);
        File.WriteAllText(path, string.Empty);
        return new MangaFileBuilder(path, format)
            .WithBytes(bytes)
            .WithLastModified(DateTime.UtcNow.AddMinutes(-5))
            .Build();
    }

    private static Chapter CreateChapter(int id, string number, MangaFile file)
    {
        var chapter = new ChapterBuilder(number)
            .WithId(id)
            .WithCreated(DateTime.UtcNow.AddMinutes(-10))
            .WithLastModified(DateTime.UtcNow.AddMinutes(-10))
            .WithFile(file)
            .Build();
        chapter.VolumeId = 1;
        return chapter;
    }

    private static Volume CreateVolume(int id, params Chapter[] chapters)
    {
        var volume = new VolumeBuilder("1")
            .WithChapters(chapters.ToList())
            .WithCreated(DateTime.UtcNow.AddMinutes(-10))
            .WithLastModified(DateTime.UtcNow.AddMinutes(-10))
            .Build();
        volume.Id = id;
        foreach (var chapter in chapters)
        {
            chapter.VolumeId = id;
        }

        return volume;
    }

    private static Series CreateSeries(int id, Volume volume)
    {
        var series = new SeriesBuilder("Series")
            .WithLibraryId(1)
            .WithFormat(MangaFormat.Epub)
            .WithVolume(volume)
            .Build();
        series.Id = id;
        return series;
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }
}
