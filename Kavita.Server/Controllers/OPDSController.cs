using System;
using System.IO;
using System.Threading;
using Kavita.Models.DTOs;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Kavita.API.Database;
using Kavita.API.Errors;
using Kavita.API.Services;
using Kavita.API.Services.Reading;
using Kavita.Common;
using Kavita.Common.Extensions;
using Kavita.Services.Scanner;
using Kavita.Models.Constants;
using Kavita.Models.DTOs.OPDS;
using Kavita.Models.DTOs.OPDS.Requests;
using Kavita.Models.DTOs.Progress;
using Kavita.Models.Entities.Enums;
using Kavita.Server.Attributes;
using Kavita.Services;
using Kavita.Services.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MimeTypes;

namespace Kavita.Server.Controllers;

[Authorize]
public class OpdsController(
    IUnitOfWork unitOfWork,
    IDownloadService downloadService,
    IDirectoryService directoryService,
    ICacheService cacheService,
    IReaderService readerService,
    ILocalizationService localizationService,
    IOpdsService opdsService,
    OpdsPrefetchService prefetchService)
    : BaseApiController
{
    private static readonly SemaphoreSlim[] DownloadLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private readonly XmlSerializer _xmlOpenSearchSerializer = new(typeof(OpenSearchDescription));


    /// <summary>
    /// Returns the Catalogue for Kavita's OPDS Service
    /// </summary>
    /// <param name="apiKey"></param>
    /// <returns></returns>
    [HttpPost("{apiKey}")]
    [HttpGet("{apiKey}")]
    [Produces("application/xml")]
    public async Task<IActionResult> Get(string apiKey)
    {
        var (baseUrl, prefix) = await GetPrefix();

        var feed = await opdsService.GetCatalogue(new OpdsCatalogueRequest
        {
            ApiKey = apiKey,
            Prefix = prefix,
            BaseUrl =  baseUrl,
            Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
            UserId = UserId
        });


        return CreateXmlResult(opdsService.SerializeXml(feed));
    }

    private async Task<Tuple<string, string>> GetPrefix()
    {
        var baseUrl = (await unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.BaseUrl)).Value;
        var prefix = OpdsService.DefaultApiPrefix;
        if (!Configuration.DefaultBaseUrl.Equals(baseUrl, StringComparison.InvariantCultureIgnoreCase))
        {
            // We need to update the Prefix to account for baseUrl
            prefix = baseUrl.TrimEnd('/') + OpdsService.DefaultApiPrefix;
        }

        return new Tuple<string, string>(baseUrl, prefix);
    }

    /// <summary>
    /// Get the User's Smart Filter - Supports Pagination
    /// </summary>
    /// <remarks>Smart filters have different entity types, this will resolve to the underlying entity</remarks>
    /// <returns></returns>
    [Produces("application/xml")]
    [HttpGet("{apiKey}/smart-filters/{filterId}")]
    public async Task<IActionResult> GetSmartFilter(string apiKey, int filterId, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        var userId = UserId;
        var (baseUrl, prefix) = await GetPrefix();

        var feed = await opdsService.ResolveSmartFilter(new OpdsItemsFromEntityIdRequest()
        {
            ApiKey = apiKey,
            Prefix =  prefix,
            BaseUrl = baseUrl,
            EntityId = filterId,
            UserId = userId,
            Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
            PageNumber = pageNumber
        });


        return CreateXmlResult(opdsService.SerializeXml(feed));
    }

    /// <summary>
    /// Get the User's Smart Filters (Dashboard Context) - Supports Pagination
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [Produces("application/xml")]
    [HttpGet("{apiKey}/smart-filters")]
    public async Task<IActionResult> GetSmartFilters(string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        try
        {
            var userId = UserId;
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetSmartFilters(new OpdsPaginatedCatalogueRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = userId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Get the User's Libraries - No Pagination Support
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [HttpGet("{apiKey}/libraries")]
    [Produces("application/xml")]
    public async Task<IActionResult> GetLibraries(string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetLibraries(new OpdsPaginatedCatalogueRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Get the User's Want to Read list - Supports Pagination
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [Produces("application/xml")]
    [HttpGet("{apiKey}/want-to-read")]
    public async Task<IActionResult> GetWantToRead(string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetWantToRead(new OpdsPaginatedCatalogueRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Get all Collections - Supports Pagination
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [Produces("application/xml")]
    [HttpGet("{apiKey}/collections")]
    public async Task<IActionResult> GetCollections(string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetCollections(new OpdsPaginatedCatalogueRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Get Series for a given Collection - Supports Pagination
    /// </summary>
    /// <param name="collectionId"></param>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [Produces("application/xml")]
    [HttpGet("{apiKey}/collections/{collectionId}")]
    public async Task<IActionResult> GetCollection(int collectionId, string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetSeriesFromCollection(new OpdsItemsFromEntityIdRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber,
                EntityId = collectionId
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Get a User's Reading Lists - Supports Pagination
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [Produces("application/xml")]
    [HttpGet("{apiKey}/reading-list")]
    public async Task<IActionResult> GetReadingLists(string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetReadingLists(new OpdsPaginatedCatalogueRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Returns individual items (chapters) from Reading List by ID - Supports Pagination
    /// </summary>
    /// <param name="readingListId"></param>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [Produces("application/xml")]
    [HttpGet("{apiKey}/reading-list/{readingListId}")]
    public async Task<IActionResult> GetReadingListItems(int readingListId, string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber, [FromQuery] bool continueReading = false)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetReadingListItems(new OpdsItemsFromEntityIdRequest()
            {
                ContinueReading = continueReading,
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber,
                EntityId = readingListId
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }


    /// <summary>
    /// Returns Series from the Library - Supports Pagination
    /// </summary>
    /// <param name="libraryId"></param>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [Produces("application/xml")]
    [HttpGet("{apiKey}/libraries/{libraryId}")]
    public async Task<IActionResult> GetSeriesForLibrary(int libraryId, string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetSeriesFromLibrary(new OpdsItemsFromEntityIdRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber,
                EntityId = libraryId
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Returns Recently Added (Dashboard Feed) - Supports Pagination
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [Produces("application/xml")]
    [HttpGet("{apiKey}/recently-added")]
    public async Task<IActionResult> GetRecentlyAdded(string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();
            var feed = await opdsService.GetRecentlyAdded(new OpdsPaginatedCatalogueRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber,
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }


    /// <summary>
    /// Get the Recently Updated Series (Dashboard) - Pagination available, total pages will not be filled due to underlying implementation
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [Produces("application/xml")]
    [HttpGet("{apiKey}/recently-updated")]
    public async Task<IActionResult> GetRecentlyUpdated(string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();
            var feed = await opdsService.GetRecentlyUpdated(new OpdsPaginatedCatalogueRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber,
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Get the On Deck (Dashboard) - Supports Pagination
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="pageNumber"></param>
    /// <returns></returns>
    [HttpGet("{apiKey}/on-deck")]
    [Produces("application/xml")]
    public async Task<IActionResult> GetOnDeck(string apiKey, [FromQuery] int pageNumber = OpdsService.FirstPageNumber)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();
            var feed = await opdsService.GetOnDeck(new OpdsPaginatedCatalogueRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                PageNumber = pageNumber,
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// OPDS Search endpoint
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="query"></param>
    /// <returns></returns>
    [HttpGet("{apiKey}/series")]
    [Produces("application/xml")]
    public async Task<IActionResult> SearchSeries(string apiKey, [FromQuery] string query)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();
            var feed = await opdsService.Search(new OpdsSearchRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                Query = query,
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("{apiKey}/search")]
    [Produces("application/xml")]
    public async Task<IActionResult> GetSearchDescriptor(string apiKey)
    {
        var userId = UserId;
        var (_, prefix) = await GetPrefix();

        var feed = new OpenSearchDescription()
        {
            ShortName = await localizationService.TranslateAsync(userId, "search"),
            Description = await localizationService.TranslateAsync(userId, "search-description"),
            Url = new SearchLink()
            {
                Type = FeedLinkType.AtomAcquisition,
                Template = $"{prefix}{apiKey}/series?query=" + "{searchTerms}"
            }
        };

        await using var sm = new StringWriter();
        _xmlOpenSearchSerializer.Serialize(sm, feed);

        return CreateXmlResult(sm.ToString().Replace("utf-16", "utf-8"));
    }

    /// <summary>
    /// Returns the items within a Series (Series Detail)
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="seriesId"></param>
    /// <returns></returns>
    [SeriesAccess]
    [HttpGet("{apiKey}/series/{seriesId}")]
    [Produces("application/xml")]
    public async Task<IActionResult> GetSeriesDetail(string apiKey, int seriesId, [FromQuery] bool continueReading = false)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetSeriesDetail(new OpdsItemsFromEntityIdRequest()
            {
                ContinueReading = continueReading,
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                EntityId = seriesId
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Returns items for a given Volume
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="seriesId"></param>
    /// <param name="volumeId"></param>
    /// <returns></returns>
    [VolumeAccess]
    [Produces("application/xml")]
    [HttpGet("{apiKey}/series/{seriesId}/volume/{volumeId}")]
    public async Task<IActionResult> GetVolume(string apiKey, int seriesId, int volumeId, [FromQuery] bool continueReading = false)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetItemsFromVolume(new OpdsItemsFromCompoundEntityIdsRequest()
            {
                ContinueReading = continueReading,
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                SeriesId = seriesId,
                VolumeId = volumeId
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Gets items for a given Chapter
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="seriesId"></param>
    /// <param name="volumeId"></param>
    /// <param name="chapterId"></param>
    /// <returns></returns>
    [ChapterAccess]
    [Produces("application/xml")]
    [HttpGet("{apiKey}/series/{seriesId}/volume/{volumeId}/chapter/{chapterId}")]
    public async Task<IActionResult> GetChapter(string apiKey, int seriesId, int volumeId, int chapterId)
    {
        try
        {
            var (baseUrl, prefix) = await GetPrefix();

            var feed = await opdsService.GetItemsFromChapter(new OpdsItemsFromCompoundEntityIdsRequest()
            {
                BaseUrl = baseUrl,
                Prefix = prefix,
                UserId = UserId,
                Preferences = await unitOfWork.UserRepository.GetOpdsPreferences(UserId),
                ApiKey = apiKey,
                SeriesId = seriesId,
                VolumeId = volumeId,
                ChapterId = chapterId
            });

            return CreateXmlResult(opdsService.SerializeXml(feed));
        }
        catch (OpdsException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Downloads a file (user must have download permission)
    /// </summary>
    /// <param name="apiKey">User's API Key</param>
    /// <param name="seriesId"></param>
    /// <param name="volumeId"></param>
    /// <param name="chapterId"></param>
    /// <param name="filename">Not used. Only for Chunky to allow download links</param>
    /// <returns></returns>
    [ChapterAccess]
    [Authorize(PolicyGroups.DownloadPolicy)]
    [HttpGet("{apiKey}/series/{seriesId}/volume/{volumeId}/chapter/{chapterId}/download/{filename}")]
    public async Task<ActionResult> DownloadFile(string apiKey, int seriesId, int volumeId, int chapterId, string filename)
    {
        var files = (await unitOfWork.ChapterRepository.GetFilesForChapterAsync(chapterId)).ToList();

        if (files.Count == 0) return NotFound();
        var download = OpdsDownloadDescriptor.Create(chapterId, files.Select(f => new MangaFileDto
        {
            Id = f.Id, FilePath = f.FilePath, Format = f.Format, Pages = f.Pages, Bytes = f.Bytes
        }));
        if (!download.BuildCbz)
        {
            var (path, contentType, _) = downloadService.GetFirstFileDownload(files.OrderBy(f => f.Id));
            return PhysicalFile(path, download.IsCbz ? OpdsDownloadDescriptor.ComicBookMime : contentType, download.Filename, true);
        }

        using var activity = Kavita.Services.Helpers.CacheActivityGate.Enter();
        // Serialize cache extraction and packaging. Never expose a partially written ZIP to another request.
        var gate = DownloadLocks[(int)((uint)chapterId % DownloadLocks.Length)];
        await gate.WaitAsync(HttpContext.RequestAborted);
        var outputPath = Path.Join(directoryService.TempDirectory, $"kavita_opds_{chapterId}_{Guid.NewGuid():N}.cbz");
        try
        {
            Directory.CreateDirectory(directoryService.TempDirectory);
            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
            {
                var pageNumber = 0;
                foreach (var source in files.OrderByNatural(f => f.FilePath, StringComparer.OrdinalIgnoreCase))
                {
                    // Extract each source independently so duplicate page basenames cannot overwrite another file.
                    var extractPath = outputPath + ".pages";
                    try
                    {
                        HttpContext.RequestAborted.ThrowIfCancellationRequested();
                        await cacheService.ExtractChapterFiles(extractPath, [source]);
                        var pages = directoryService.GetFilesWithExtension(extractPath, Parser.ImageFileExtensions)
                            .OrderByNatural(Path.GetFileNameWithoutExtension).ToList();
                        if (pages.Count == 0) throw new IOException("OPDS source contains no readable images");
                        foreach (var page in pages)
                        {
                            HttpContext.RequestAborted.ThrowIfCancellationRequested();
                            var entry = archive.CreateEntry($"{++pageNumber:D8}{Path.GetExtension(page)}", CompressionLevel.NoCompression);
                            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                            await using var input = System.IO.File.OpenRead(page);
                            await using var output = entry.Open();
                            await input.CopyToAsync(output, HttpContext.RequestAborted);
                        }
                    }
                    finally
                    {
                        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                    }
                }
            }
            // DeleteOnClose ties cleanup to response disposal, including disconnected clients and Range requests.
            var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return File(stream, OpdsDownloadDescriptor.ComicBookMime, download.Filename, true);
        }
        catch
        {
            System.IO.File.Delete(outputPath);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private ContentResult CreateXmlResult(string xml)
    {
        Response.Headers.CacheControl = "private, no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        return new ContentResult
        {
            ContentType = "application/xml",
            Content = xml,
            StatusCode = 200
        };
    }


    /// <summary>
    /// This returns a streamed image following OPDS-PS v1.2
    /// </summary>
    /// <param name="apiKey"></param>
    /// <param name="libraryId"></param>
    /// <param name="seriesId"></param>
    /// <param name="volumeId"></param>
    /// <param name="chapterId"></param>
    /// <param name="pageNumber"></param>
    /// <param name="saveProgress">Optional parameter. Can pass false and progress saving will be suppressed</param>
    /// <returns></returns>
    [ChapterAccess]
    [HttpGet("{apiKey}/image")]
    public async Task<ActionResult> GetPageStreamedImage(string apiKey, [FromQuery] int libraryId, [FromQuery] int seriesId,
        [FromQuery] int volumeId,[FromQuery] int chapterId, [FromQuery] int pageNumber, [FromQuery] bool saveProgress = true)
    {
        var userId = UserId;
        if (pageNumber < 0) return BadRequest(await localizationService.TranslateAsync(userId, "greater-0", "Page"));
        var chapter = await cacheService.Ensure(chapterId, true);
        if (chapter == null) return BadRequest(await localizationService.TranslateAsync(userId, "cache-file-find"));
        if (chapter.Pages > 0 && pageNumber >= chapter.Pages)
            return BadRequest(await localizationService.TranslateAsync(userId, "no-image-for-page", pageNumber));

        try
        {
            var path = cacheService.GetCachedPagePath(chapter.Id, pageNumber);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                return BadRequest(await localizationService.TranslateAsync(userId, "no-image-for-page", pageNumber));

            var content = await directoryService.ReadFileAsync(path);
            var format = Path.GetExtension(path);

            // Save progress for the user (except Panels, they will use a direct connection)
            var userAgent = Request.Headers.UserAgent.ToString();

            if (!userAgent.StartsWith("Panels", StringComparison.InvariantCultureIgnoreCase) && saveProgress)
            {
                // OPDS-PSE image indexes are zero-based; lastRead/count are one-based.
                // This remains a speculative high-water mark, not proof of viewport
                // visibility: SaveOpdsProgress never creates sessions or TotalReads.
                await readerService.SaveOpdsProgress(new ProgressDto()
                {
                    ChapterId = chapterId,
                    PageNum = pageNumber == int.MaxValue ? int.MaxValue : pageNumber + 1,
                    SeriesId = seriesId,
                    VolumeId = volumeId,
                    LibraryId =libraryId
                }, userId);
            }

            Response.OnCompleted(() =>
            {
                prefetchService.TryQueue(userId, chapterId);
                return Task.CompletedTask;
            });
            return CachedContent(content, MimeTypeMap.GetMimeType(format));
        }
        catch (Exception)
        {
            cacheService.CleanupChapters([chapterId]);
            throw;
        }
    }

    [HttpGet("{apiKey}/favicon")]
    [ResponseCache(CacheProfileName = ResponseCacheProfiles.Month)]
    public async Task<ActionResult> GetFavicon(string apiKey)
    {
        var files = directoryService.GetFilesWithExtension(Path.Join(Directory.GetCurrentDirectory(), ".."), @"\.ico");
        if (files.Length == 0) return BadRequest(await localizationService.TranslateAsync(UserId, "favicon-doesnt-exist"));

        var path = files[0];
        var content = await directoryService.ReadFileAsync(path);
        var format = Path.GetExtension(path);

        return File(content, MimeTypeMap.GetMimeType(format));
    }
}
