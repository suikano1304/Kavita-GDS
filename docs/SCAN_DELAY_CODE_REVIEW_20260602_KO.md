# Kavita-GDS scan delay code review - 2026-06-02

## Context

- Production container: `kavita`
- Production image: `local/kavita-gds:9.0.6-1`
- Production image id: `sha256:<redacted>`
- Comparable official image available locally: `ghcr.io/kareadita/kavita:nightly-0.9.0.6`
- Official image source revision label: `<redacted-hash>`
- Local GDS source reviewed: `<local-kavita-source>`
- Local GDS HEAD: `<redacted-hash>`

## Production observation

- `2026-06-02 20:50:43` production started `production-library-a` scan.
- `2026-06-02 20:51:35` production logged:
  - `Finished scan in 51734 milliseconds.`
  - `Finished library scan of 312 files and 4394 series in 51845 milliseconds for production-library-a`
- `2026-06-02 20:51:49` a new scan request was delayed:
  - `A Scan is already running, rescheduling ScanLibrary in 3 hours`
  - request path was `/api/library/scan?libraryId=<redacted>&force=false`
- Library id mapping at that time:
  - `<redacted>`: `production-library-a`
  - `2`: `연재중`
  - `<redacted>`: `production-library-a`
- Container still showed about one full CPU core in use after the finish log.

## Official comparison

`Kavita.Services/TaskScheduler.cs` has no meaningful GDS change versus official `c7e9555`.
The 3-hour delay policy is official behavior:

- `ScanLibrary()` first checks whether the same library already has a scan job.
- It then checks whether any scanner task is running.
- If yes, it schedules the requested scan 3 hours later.

The observed issue is therefore not a custom change to the scheduler message itself. The risk is that the GDS scan path keeps the scanner job alive longer than the `Finished library scan` log suggests, or leaves expensive post-scan work running while the user reasonably believes the scan is done.

## Findings

### 1. `Finished library scan` is not the actual end of `ScanLibrary`

File: `<local-kavita-source>/Kavita.Services/Scanner/ScannerService.cs`

After the `Finished library scan` log, the method still executes:

- `RemoveSeriesNotFound(parsedSeries, library)`
- `eventHub.SendMessageAsync(...Ended...)`
- `metadataService.RemoveAbandonedMetadataKeys()`
- `BackgroundJob.Enqueue(() => directoryService.ClearDirectory(directoryService.CacheDirectory))`

This makes the log misleading for manual operations. A user can start the next scan after seeing the finish log, but Hangfire may still consider the scan job active.

Impact:

- Explains a delay request immediately after a finish log.
- Makes manual chained scans unreliable.

Candidate fix:

- Move or add a final log after all awaited post-scan work completes.
- Consider sending a separate `Finished import/update` versus `Finished scanner job` message.

### 2. GDS sequential path holds the scan job during cover generation

File: `<local-kavita-source>/Kavita.Services/Scanner/ScannerService.cs`

GDS libraries take a custom path:

- `ProcessParserInfo()` delegates to `ProcessParserInfoSequential()` for `LibraryType.GDS`.
- That method processes each series and then immediately awaits `GenerateCoversForSeries(...)` for each changed series.

This is safer for memory, but it means the scan job includes per-series cover generation in-line. For large remote/rclone-backed libraries, cover generation can extend the scanner job lifetime significantly.

Impact:

- Increases the window where `TaskScheduler` rejects another scan as "already running".
- Makes one slow cover extraction or image path block the whole scan queue.

Candidate fix:

- Keep DB mutation sequential, but move cover generation to a bounded follow-up queue or a limited worker after the scanner job releases.
- If in-line cover generation is kept, log per-stage start/end and expose the post-scan stage clearly.

### 3. GDS EPUB/PDF/TXT page count shortcut can create `1/1` chapters

File: `<local-kavita-source>/Kavita.Services/Scanner/ProcessSeries.cs`

`GetGdsPageCount()` returns:

- existing page count if it is already greater than zero on `/mnt/gds`
- real page count for archives
- real page count for local non-GDS EPUB/PDF/TXT
- otherwise `1` for GDS EPUB/PDF/TXT

This means a new or rebuilt remote GDS EPUB/TXT/PDF can be persisted with `Pages = 1`.

Impact:

- Directly matches the recurrent `page 1/1` symptom for EPUB/TXT/PDF when a file has no previous page count.
- Tests can miss this if they reuse a DB that already has valid page counts.

Candidate fix:

- Do not default GDS EPUB/TXT/PDF to `1` during scan.
- Prefer accurate page count for EPUB/TXT/PDF when a file is new or `Pages <= 1`.
- If performance is a concern, store a pending page-count state and compute it asynchronously, but do not present `1/1` as final.

### 4. Invalid GDS YAML metadata can drop the whole file from scan

File: `<local-kavita-source>/Kavita.Services/Helpers/GdsMetadataParser.cs`

`GetComicInfo()` reads and deserializes `kavita.yaml` without local exception handling.
`ReadingItemService.ParseFile()` catches the exception at the outer level and returns `null`, so a metadata parse error can skip the media file entirely.

Production evidence during the delayed scan window:

- `영어 학원 전쟁 01권 (리디)#197.zip`
- `영어 학원 전쟁 02권 (완결) (리디)#195.zip`
- Both failed with `YamlDotNet.Core.SemanticErrorException`.

Impact:

- Bad metadata can prevent otherwise readable archives from being imported/updated.
- Repeated scans may keep encountering the same error.

Candidate fix:

- Catch YAML exceptions inside `GdsMetadataParser.GetComicInfo()`.
- Return `baseInfo` or filename-derived info when GDS metadata is malformed.
- Keep the media file in the scan, and report metadata-only degradation separately.

### 5. Folder cover application can be overwritten immediately

File: `<local-kavita-source>/Kavita.Services/MetadataService.cs`

`ProcessSeriesCoverGen()` calls `TryApplyGdsFolderCover(...)`, but ignores the return value and continues into `ProcessGdsSeriesCoverGen(...)`.

`ProcessGdsSeriesCoverGen()` later calls `UpdateSeriesCoverImage(...)`, which can set the series cover from the first volume/chapter and overwrite the folder cover that was just applied.

Impact:

- `cover.jpg/png/webp` at series folder may not stay as series cover.
- Can contribute to unexpected "all covers look like first volume/chapter" behavior depending on layout.

Candidate fix:

- Decide precedence explicitly:
  - folder cover should be series-only and survive volume/chapter generation, or
  - folder cover should cascade intentionally.
- If folder cover has priority, avoid the later `UpdateSeriesCoverImage()` overwrite.

### 6. GDS bottom-up directory scan has O(n^2) descendant checks

File: `<local-kavita-source>/Kavita.Services/Scanner/ParseScannedFiles.cs`

`ScanDirectoriesBottomUp()` calls `HasProcessedDescendant(processedDirs, directory)`, which scans all previously processed directories for every directory.

Impact:

- On very large GDS trees, CPU can be spent in path comparisons even when little file work remains.
- This may not explain the exact 14-second post-finish delay alone, but it is a scalability risk.

Candidate fix:

- Replace repeated linear descendant checks with a trie/path-prefix structure or sort-and-skip traversal state.
- Add timing logs around directory enumeration, parse, DB update, cover generation, and post-scan cleanup.

## Priority

1. Fix `GetGdsPageCount()` for EPUB/TXT/PDF to avoid new `1/1` chapters.
2. Make `GdsMetadataParser.GetComicInfo()` tolerant of malformed YAML.
3. Add true final scan-job logging after all awaited post-scan work.
4. Decouple or bound GDS cover generation so scan locks are released sooner.
5. Clarify folder cover precedence.
6. Optimize bottom-up directory descendant checks if CPU profiling confirms it matters.

## No code changes made in this review

This document records the comparison and review findings only. No production container restart, image rebuild, commit, push, or release was performed for this review.
