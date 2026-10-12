using System;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Interfaces;

namespace Kavita.API.Services.Helpers;

public interface ICacheHelper
{
    bool ShouldUpdateCoverImage(string coverPath, bool sourceChanged, bool forceUpdate = false, bool isCoverLocked = false);
    bool ShouldUpdateCoverImage(string coverPath, MangaFile? firstFile, DateTime chapterCreated,
        bool forceUpdate = false, bool isCoverLocked = false);
    bool IsFileUnmodifiedSinceCreationOrLastScan(IEntityDate chapter, bool forceUpdate, MangaFile? firstFile);

    bool CoverImageExists(string path);
}
