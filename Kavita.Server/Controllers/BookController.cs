using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kavita.API.Attributes;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.Common;
using Kavita.Models.Constants;
using Kavita.Models.DTOs.Reader;
using Kavita.Models.Entities.Enums;
using Kavita.Server.Attributes;
using Kavita.Services;
using Kavita.Services.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VersOne.Epub;

namespace Kavita.Server.Controllers;

public class BookController(
    IBookService bookService,
    IUnitOfWork unitOfWork,
    ICacheService cacheService,
    ILocalizationService localizationService,
    IDirectoryService directoryService)
    : BaseApiController
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> PageCountGates = new();

    private Task<EpubBookOpener.Lease> OpenEpubBookAsync(string path) => Task.FromResult(
        EpubBookOpener.Open(path, Path.Join(directoryService.TempDirectory, "epub-manifest-repair"), BookService.LenientBookReaderOptions));

    /// <summary>
    /// Retrieves information for the PDF and Epub reader. This will cache the file.
    /// </summary>
    /// <remarks>This only applies to Epub or PDF files</remarks>
    /// <param name="chapterId"></param>
    /// <returns></returns>
    [HttpGet("{chapterId}/book-info")]
    [ChapterAccess]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<BookInfoDto>> GetBookInfo(int chapterId)
    {
        var dto = await unitOfWork.ChapterRepository.GetChapterInfoDtoAsync(chapterId);
        if (dto == null) return BadRequest(await localizationService.TranslateAsync(UserId, "chapter-doesnt-exist"));
        var bookTitle = string.Empty;


        switch (dto.SeriesFormat)
        {
            case MangaFormat.Epub:
            {
                var chapter = await cacheService.Ensure(chapterId);
                if (chapter == null) return NotFound();

                var file = cacheService.GetCachedFile(chapter);
                using var bookLease = await OpenEpubBookAsync(file);
                var book = bookLease.Book;
                if (book == null) return NotFound();

                bookTitle = book.Title;
                var pageCount = bookService.GetNumberOfPages(file);
                if (pageCount <= 0) throw new InvalidDataException("EPUB has no readable pages");
                if (dto.Pages <= 1)
                {
                    dto.Pages = await UpdateBookPageCountAsync(chapterId, dto.VolumeId, dto.SeriesId,
                        ChapterFileSelector.GetBestReadingFile(chapter.Files)!.Id, pageCount);
                }

                break;
            }
            case MangaFormat.Pdf:
            {
                var chapter = await cacheService.Ensure(chapterId);
                if (chapter == null) return NotFound();

                var file = cacheService.GetCachedFile(chapter);
                if (string.IsNullOrEmpty(bookTitle))
                {
                    // Override with filename
                    bookTitle = Path.GetFileNameWithoutExtension(file);
                }

                break;
            }
            case MangaFormat.Image:
            case MangaFormat.Archive:
            case MangaFormat.Unknown:
            default:
                break;
        }

        var info = new BookInfoDto()
        {
            ChapterNumber = dto.ChapterNumber,
            VolumeNumber = dto.VolumeNumber,
            VolumeId = dto.VolumeId,
            BookTitle = bookTitle,
            SeriesName = dto.SeriesName,
            SeriesFormat = dto.SeriesFormat,
            SeriesId = dto.SeriesId,
            LibraryId = dto.LibraryId,
            IsSpecial = dto.IsSpecial,
            Pages = dto.Pages,
        };


        return Ok(info);
    }

    private async Task<int> UpdateBookPageCountAsync(int chapterId, int volumeId, int seriesId, int selectedFileId, int pageCount)
    {
        var gate = PageCountGates.GetOrAdd(seriesId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(HttpContext.RequestAborted);
        try
        {
            var db = unitOfWork.DataContext;
            await using var transaction = await db.Database.BeginTransactionAsync(HttpContext.RequestAborted);
            var selected = await db.MangaFile.AsNoTracking().SingleAsync(f => f.Id == selectedFileId && f.ChapterId == chapterId);
            var oldPages = await db.Chapter.AsNoTracking().Where(c => c.Id == chapterId).Select(c => c.Pages).SingleAsync();
            if (oldPages > 1) return oldPages;
            // A prior cover save may have left the selected file correct but its chapter stale.
            // Reconcile the selected reading count, preserving alternative-file metadata.
            var delta = pageCount - oldPages;
            await db.MangaFile.Where(f => f.Id == selectedFileId).ExecuteUpdateAsync(u => u.SetProperty(f => f.Pages, pageCount));
            await db.Chapter.Where(c => c.Id == chapterId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Pages, pageCount));
            await db.Volume.Where(v => v.Id == volumeId).ExecuteUpdateAsync(u => u.SetProperty(v => v.Pages, v => v.Pages + delta));
            await db.Series.Where(v => v.Id == seriesId).ExecuteUpdateAsync(u => u.SetProperty(v => v.Pages, v => v.Pages + delta));
            await transaction.CommitAsync(HttpContext.RequestAborted);
            return oldPages + delta;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// This is an entry point to fetch resources from within an epub chapter/book.
    /// </summary>
    /// <param name="chapterId"></param>
    /// <param name="file"></param>
    /// <returns></returns>
    [ChapterAccess]
    [SkipDeviceTracking]
    [HttpGet("{chapterId}/book-resources")]
    [ResponseCache(CacheProfileName = ResponseCacheProfiles.FiveMinute, VaryByQueryKeys = ["chapterId", "file"])]
    public async Task<ActionResult> GetBookPageResources(int chapterId, [FromQuery] string file)
        => await ReadBookResource(chapterId, file, true);

    private async Task<ActionResult> ReadBookResource(int chapterId, string file, bool retryEvictedCache)
    {
        if (chapterId <= 0) return BadRequest(await localizationService.GetAsync("en", "chapter-doesnt-exist"));

        var chapter = await cacheService.Ensure(chapterId);
        if (chapter == null) return BadRequest(await localizationService.GetAsync("en", "chapter-doesnt-exist"));

        var mangaFile = ChapterFileSelector.GetBestReadingFile(chapter.Files);
        if (mangaFile == null) return BadRequest(await localizationService.GetAsync("en", "chapter-doesnt-exist"));

        var cachedFilePath = Path.Join(cacheService.GetCachePath(chapterId), Path.GetFileName(mangaFile.FilePath));
        Kavita.Models.DTOs.Reader.BookResourceResultDto result;
        try { result = await bookService.GetResourceAsync(cachedFilePath, file); }
        catch (IOException ex) when (retryEvictedCache && ex is FileNotFoundException or DirectoryNotFoundException &&
            !System.IO.File.Exists(cachedFilePath) && System.IO.File.Exists(mangaFile.FilePath))
        {
            return await ReadBookResource(chapterId, file, false);
        }

        if (!result.IsSuccess) return BadRequest(await localizationService.GetAsync("en", result.ErrorMessage));

        return File(result.Content, result.ContentType, $"{chapterId}-{file}");
    }

    /// <summary>
    /// This will return a list of mappings from ID -> page num. ID will be the xhtml key and page num will be the reading order
    /// this is used to rewrite anchors in the book text so that we always load properly in our reader.
    /// </summary>
    /// <remarks>This is essentially building the table of contents</remarks>
    /// <param name="chapterId"></param>
    /// <returns></returns>
    [HttpGet("{chapterId}/chapters")]
    [ChapterAccess]
    public async Task<ActionResult<ICollection<BookChapterItem>>> GetBookChapters(int chapterId)
    {
        if (chapterId <= 0) return BadRequest(await localizationService.TranslateAsync(UserId, "chapter-doesnt-exist"));

        var chapter = await unitOfWork.ChapterRepository.GetChapterAsync(chapterId);
        if (chapter == null) return BadRequest(await localizationService.TranslateAsync(UserId, "chapter-doesnt-exist"));

        try
        {
            return Ok(await bookService.GenerateTableOfContents(chapter));
        }
        catch (KavitaException ex)
        {
            return BadRequest(ex.Message);
        }
    }


    /// <summary>
    /// This returns a single page within the epub book. All html will be rewritten to be scoped within our reader,
    /// all css is scoped, etc.
    /// </summary>
    /// <param name="chapterId"></param>
    /// <param name="page"></param>
    /// <returns></returns>
    [HttpGet("{chapterId}/book-page")]
    [ChapterAccess]
    public Task<ActionResult<string>> GetBookPage(int chapterId, [FromQuery] int page)
        => ReadBookPage(chapterId, page, retryEvictedCache: true);

    private async Task<ActionResult<string>> ReadBookPage(int chapterId, int page, bool retryEvictedCache)
    {
        var chapter = await cacheService.Ensure(chapterId);
        if (chapter == null) return BadRequest(await localizationService.TranslateAsync(UserId, "chapter-doesnt-exist"));
        var path = cacheService.GetCachedFile(chapter);

        var baseUrl = "//" + Request.Host + Request.PathBase + "/api/";

        try
        {
            if (ChapterFileSelector.GetBestReadingFile(chapter.Files)?.Format == MangaFormat.Text)
            {
                return Ok(await bookService.GetBookPageText(page, chapterId, path));
            }

            var ptocBookmarks =
                await unitOfWork.UserTableOfContentRepository.GetPersonalToCForPage(UserId, chapterId, page);
            var annotations = await unitOfWork.UserRepository.GetAnnotationsByPage(UserId, chapter.Id, page);

            return Ok(await bookService.GetBookPage(UserId, page, chapterId, path, baseUrl, ptocBookmarks, annotations));
        }
        catch (IOException ex) when (retryEvictedCache &&
            ex is FileNotFoundException or DirectoryNotFoundException &&
            !System.IO.File.Exists(path) &&
            System.IO.File.Exists(ChapterFileSelector.GetBestReadingFile(chapter.Files)?.FilePath))
        {
            // A completed scan can purge the cache between Ensure and opening the EPUB/TXT.
            // Re-extract once; never retry missing source files or persistent I/O failures.
            return await ReadBookPage(chapterId, page, retryEvictedCache: false);
        }
        catch (KavitaException ex)
        {
            return BadRequest(await localizationService.TranslateAsync(UserId, ex.Message));
        }
    }
}
