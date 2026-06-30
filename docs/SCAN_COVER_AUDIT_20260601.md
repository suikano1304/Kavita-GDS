# 2026-06-01 scan cover audit

## Scope

- Target libraries:
  - `production-library-a` (`LibraryId=1`)
  - `연재중` (`LibraryId=2`)
- Observed scan finish: `2026-06-01 02:24:02 KST`
- Kavita log summary: `1146 files`, `4395 series`
- Scan duration: `8631763 ms` for the library scan
- Audit window used for detailed cover verification:
  - `2026-05-31 16:45:00` to `2026-05-31 17:25:00` UTC
  - This corresponds to the final user-observed scan segment around `65%`, including the last 50 processed series in the log.
- Additional completed scan:
  - `연재중` finished at `2026-06-01 03:17:45 KST`
  - Kavita log summary: `307 files`, `1933 series`
  - Scan duration: `3221926 ms` for the library scan
  - Audit window: `2026-05-31 17:45:00` to `2026-05-31 18:18:30` UTC

No database rows, cover files, source files, media files, or container settings were changed for this audit.

## Findings

The final scan segment completed, but 2nd-and-later volume/chapter covers did not populate correctly for newly processed multi-file series.

Aggregate DB check:

`production-library-a` final observed segment:

- Multi-chapter series checked: `48`
- Chapters after the first chapter: `340`
- Missing `Chapter.CoverImage` after first chapter: `340`
- Missing `Volume.CoverImage` after first chapter: `337`

`연재중` completed scan:

- Multi-chapter series checked: `40`
- Chapters after the first chapter: `249`
- Missing `Chapter.CoverImage` after first chapter: `249`
- Missing `Volume.CoverImage` after first chapter: `249`

This means every checked chapter after the first one is missing a chapter cover in both audited scan windows.

During the `연재중` scan, a burst of `VIPS-WARNING iCCP profile ... ICC profile tag start not a multiple of 4` messages appeared while processing image covers. The scan continued afterward, so this warning is noted separately from the missing 2nd-and-later chapter cover issue.

## Evidence

Example pattern from DB:

```text
1st file:
  Volume.CoverImage  = v..._c....png
  Chapter.CoverImage = v..._c....png

2nd and later files:
  Volume.CoverImage  = empty
  Chapter.CoverImage = empty
```

The cover job logs are also suspiciously short for multi-volume archive series:

```text
Updated covers ... in 43-124 milliseconds
```

For series with 10-32 ZIP files, that duration is too short to indicate that every archive was opened and a thumbnail was extracted. It is consistent with only first-cover propagation or most chapter cover extraction being skipped.

## Likely Cause

The likely issue is in cover generation/update flow, not in rclone or the original media files.

Relevant code path:

- `Kavita.Services/MetadataService.cs`
- `ProcessSeriesCoverGen`
- `UpdateChapterCoverImage`
- `UpdateVolumeCoverImage`

The GDS library does enter normal cover generation unless a folder-level `cover.jpg/png/webp` is found. The observed state suggests:

1. First chapter cover is generated or applied.
2. Later chapters keep empty `CoverImage`.
3. Volume cover then copies the corresponding chapter cover, so later volumes also remain empty.

The `CacheHelper.ShouldUpdateCoverImage(...)` decision path should be reviewed for the empty-cover case. A false skip when `CoverImage` is empty would explain the observed pattern.

## Related Separate Issue

The EPUB reader error seen for another library item is separate from this cover issue.

That EPUB contains a duplicated manifest item ID in `OEBPS/content.opf`:

```text
duplicate id: Section0001.xhtml
```

Kavita fails because `VersOne.Epub` rejects the invalid EPUB manifest. This is an EPUB packaging problem, not the GDS manga cover issue.

## Suggested Next Investigation

Do not change production DB directly. For a fix branch or test container:

1. Reproduce with a small GDS series containing 2-3 ZIP files and no folder-level cover file.
2. Add debug logging around `UpdateChapterCoverImage`:
   - chapter id
   - current `CoverImage`
   - first file path
   - result of `ShouldUpdateCoverImage`
   - result of `readingItemService.GetCoverImage`
3. Verify that every chapter after the first actually calls archive cover extraction.
4. If fixed, rescan or regenerate covers for a small sample before applying to the full library.

## 2026-06-01 Test Fix Prepared

Prepared in source tree:

- `<local-kavita-source>/Kavita.Services/MetadataService.cs`
- `<local-kavita-source>/Kavita.Services/ArchiveService.cs`
- `<local-kavita-source>/Kavita.Services/Helpers/GdsMetadataParser.cs`
- `<local-kavita-worktree>/Dockerfile.0906-gds-test`

Build image:

- official baseline: `ghcr.io/kareadita/kavita:nightly-0.9.0.6`
- official revision: `<redacted-hash>`
- test image: `local/kavita-gds:9.0.6-1-test`
- Image ID: `<redacted-hash>`

Root cause found while preparing the test build:

- The GDS-specific cover generation path returned after applying the YAML or first-cover path.
- That left later chapters and volumes in multi-file GDS series without `Chapter.CoverImage` and `Volume.CoverImage`.

Code changes prepared for test validation:

- `ProcessGdsSeriesCoverGen` now processes all non-text GDS volumes and chapters instead of returning after the first cover path.
- Text series still use the existing GDS text title-cover path.
- Archive cover extraction now returns an empty cover result instead of throwing when an archive has no candidate cover entry.

Production has not been changed. The test image is staged for `kavita-test` validation only.

## 2026-06-01 06:08 KST Test Validation

The cover issue was reproduced in `LOCAL-FIXTURES`: generated cover filenames were distinct, but 2nd-and-later CBZ/ZIP files initially had identical image hashes to volume 1. The source `kavita.yaml` file-level `cover` values were distinct, so the bug was in GDS cover selection/fallback rather than the fixture source.

Additional root cause:

- The earlier `kavita.yaml` parser could fall back to archive extraction when it did not resolve the target file's YAML cover.
- Archive extraction then selected the same embedded `cover`/first image pattern for multiple files.
- Forced metadata refresh is required to overwrite already-generated cover files; a scan alone may leave unchanged cached covers in place.

Validated fix in `kavita-test`:

```text
sample-a volume-1  <redacted-cover-file>  <redacted-hash>
sample-a volume-2  <redacted-cover-file>  <redacted-hash>
sample-a volume-3  <redacted-cover-file>  <redacted-hash>
sample-b volume-1  <redacted-cover-file>  <redacted-hash>
sample-b volume-2  <redacted-cover-file>  <redacted-hash>
sample-c volume-1  <redacted-cover-file>  <redacted-hash>
sample-c volume-2  <redacted-cover-file>  <redacted-hash>
```

TXT cover rendering issue:

- Test image initially lacked Korean fonts from the official runtime path.
- Added `fontconfig` and NanumGothic TTFs to the test runtime image.
- Kept SVG `font-family` unquoted so fontconfig/Pango resolves `NanumGothic`.
- Verified `series24530.png` renders Korean text normally instead of square-box tofu glyphs.

EPUB duplicate manifest follow-up:

- Added a test-only tolerant reader path for exact duplicate OPF manifest items.
- The original EPUB is not modified; Kavita creates a temporary repaired EPUB copy and removes exact duplicate `manifest/item` entries where `id`, `href`, and `media-type` all match.
- Validated `book-info` for all three `epub-problem` chapters returned HTTP 200.
- Validated forced cover refresh and `series/analyze` completed for `epub-problem`; word counts were written:

```text
<redacted-series-id>|epub-problem|589835|problem-chapter-a|180283
<redacted-series-id>|epub-problem|589835|problem-chapter-b|207839
<redacted-series-id>|epub-problem|589835|problem-chapter-c|201713
```

The duplicate manifest repair path now logs the recovery message without an exception stack when repair succeeds.

## 2026-06-01 06:55 KST Additional Cover Regression Check

User reported a second possible cover symptom: covers after volume/chapter 1 may all be generated as the same image as volume/chapter 1.

The latest test image was rebuilt and redeployed:

- test image: `local/kavita-gds:9.0.6-1-test`
- Image ID: `<redacted-hash>`
- `kavita-test` container image hash matched the image tag hash.

After a forced `LOCAL-FIXTURES` scan, the DB and cover image hashes were checked across EPUB, CBZ, and ZIP multi-file samples.

Result:

- EPUB/CBZ/ZIP chapter cover filenames are distinct per chapter.
- EPUB/CBZ/ZIP cover file hashes are distinct per chapter.
- TXT rows share generated series title covers by design and are not treated as a cover-extraction regression.

Representative hash evidence:

```text
epub-problem sample-1 <redacted-cover-file> <redacted-hash>
epub-problem sample-2 <redacted-cover-file> <redacted-hash>
epub-problem sample-3 <redacted-cover-file> <redacted-hash>
epub sample-a <redacted-cover-file> <redacted-hash>
epub sample-b <redacted-cover-file> <redacted-hash>
epub sample-c <redacted-cover-file> <redacted-hash>
zip sample-a <redacted-cover-file> <redacted-hash>
zip sample-b <redacted-cover-file> <redacted-hash>
cbz sample-a <redacted-cover-file> <redacted-hash>
cbz sample-b <redacted-cover-file> <redacted-hash>
```

The "2nd-and-later cover equals 1st cover" symptom is not present in the current test image after forced refresh.

## 2026-06-01 07:26 KST Expanded Fixture Validation

The final `9.0.6-1-test` image was rebuilt and redeployed:

- test image: `local/kavita-gds:9.0.6-1-test`
- Image ID: `<redacted-hash>`
- `kavita-test`: healthy

`LOCAL-FIXTURES` was expanded to `117` media files across `26` series:

```text
Archive: 65 files, 12 series, zero pages 0, missing covers 0
EPUB:    30 files, 7 series,  zero pages 0, missing covers 0
TXT:     22 files, 7 series,  zero pages 0, missing covers 0
```

Full reader/API validation over all `117` chapters was repeated 3 times:

```text
pass=1 total=117 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0
pass=2 total=117 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0
pass=3 total=117 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0
```

The validation checked `chapter-info`, next/previous chapter APIs, and first/middle/last page fetches for every item. No `[Error]`, exception, failed Hangfire job, or `DirectoryNotFound` log appeared during the final validation or the follow-up force scan.

Final force scan:

```text
Finished library scan of 117 files and 26 series in 11037 milliseconds for LOCAL-FIXTURES
DB summary: 117 chapters, 26 series, zero pages 0, missing chapter covers 0
```

## 2026-06-01 EPUB Page Count Follow-up

A separate reader issue was found while checking problem EPUBs in the Web UI:

- The files opened.
- The reader showed `1/1`.
- Choosing an item from the content table worked, but next/previous page navigation did not behave correctly.

## 2026-06-02 GDS scan word-count bottleneck

After `9.0.6-1` was applied to production, the large GDS libraries were forced-scanned again.

Completed on the low-memory stream-discovery image:

- `성인 만화`: `1200` series, `10190` files, finished `2026-06-02 09:29:16 KST`.
- `production-library-a`: `4394` series, `41276` files, finished `2026-06-02 12:34:55 KST`.
- `연재중`: `1930` series, `14231` files, finished `2026-06-02 13:25:30 KST`.

`production-library-c` was then started at `2026-06-02 13:31:10 KST`. File discovery completed at `2026-06-02 16:13 KST` with:

```text
Series to process: 13675
```

The scan then slowed down in the per-series post-processing phase because the GDS low-memory path was still forcing `WordCountAnalyzerService.ScanSeries(...)` after each cover update. Representative production logs showed:

```text
Updated metadata for 조선대혁명 in 130409 milliseconds
Updated metadata for 내 안에 천재 배우 in 22432 milliseconds
Updated metadata for 눈먼 짐승의 목줄을 쥐었다 in 24666 milliseconds
```

This is not required for the user's requested cover-focused forced scan. A new patch was prepared:

- source commit: `17253b3 fix: skip GDS word count during library scans`
- package patch: `patches/9.0.6-1/0002-fix-skip-GDS-word-count-during-library-scans.patch`

Behavior after the patch:

- GDS library scan still performs DB import/update and cover generation sequentially.
- GDS library scan no longer forces word-count analysis during the scan path.
- Word-count remains available through explicit analyze/word-count actions.
- Non-GDS scan behavior is unchanged.

The latest amd64 image with the GDS word-count split was pushed:

```text
ghcr.io/suikano1304/kavita-gds:9.0.6-1-amd64
digest sha256:<redacted>
```

Final arm64 and multiarch publication were completed after the production scan path was rebuilt and revalidated. Final public GHCR digest values are recorded in the `2026-06-02 Final Skip-Word Production Scan and GHCR Publish` section below.

## 2026-06-01 Production Cover Regeneration

After deploying final image `sha256:<redacted>`, production cover references for 2nd-and-later chapters/volumes were cleaned only where they were missing or had the same hash as the first item in the series.

Backups/manifests:

- DB backup: `/your/kavita/config/kavita.db.coverfix-20260601-093202.bak`
- cleanup manifest: `<local-cover-repair-output>`
- final cleanup summary: `<local-cover-repair-output>`

Final cleanup before regeneration:

```text
series_count 12695
chapter_rows_nulled 109841
volume_rows_nulled 104278
deleted_cover_files 0
```

Forced metadata refresh was queued for production libraries `1..9` with `force=true&forceColorscape=true`.

Status at `2026-06-01 10:50 KST`:

```text
production-library-a: 1300 / 4366 series complete (29.8%)
cover files: 36239
Chapter null: 100957
Volume null: 96034
production Web UI root: HTTP 200
```

The refresh is still running. The final duplicate/missing cover audit and forced production `scan-all` are intentionally deferred until metadata refresh completes.

Root cause:

- The GDS scan optimization returned `1` page for EPUB/PDF/TXT without reading the file.
- This was meant for the remote `/mnt/gds` mount but also affected local `/fixtures` files.

Fix prepared and validated:

- Keep the shortcut for `/mnt/gds`.
- For local fixture paths, run normal Kavita page-count logic.
- Recompute local fixture page count on force scan even if the previous DB value was already `1`.

Validation:

```text
problem-chapter-a Chapter.Pages=34 MangaFile.Pages=34
problem-chapter-b Chapter.Pages=49 MangaFile.Pages=49
problem-chapter-c Chapter.Pages=42 MangaFile.Pages=42
book-page middle/last pages HTTP 200
first EPUB font resources HTTP 200
```

## 2026-06-01 Production Apply

Production was updated after test validation and explicit user approval to interrupt the running production scan.

Backups:

- `<local-kavita-backup>`
- `<local-kavita-backup>`
- `<local-kavita-backup>`

Image:

- previous: `ghcr.io/suikano1304/kavita-gds:0.9.0.2-8`
- current: `local/kavita-gds:9.0.6-1`
- Image ID: `<redacted-hash>`

Verification:

- production `kavita`: healthy
- `/api/health`: `Ok`
- `/your/gds/mount -> /mnt/gds`: `RW=false`
- startup migration completed from `0.9.0.2` to `0.9.0.6`
- full force scan requested with `POST /api/library/scan-all?force=true`, HTTP `200`
- scan log shows `Starting Scan of All Libraries, Forced: true`
- rclone RC after deploy: `errors=0`, `deletes=0`, `renames=0`, `serverSideCopies=0`, `serverSideMoves=0`

Follow-up:

- The first force-scan run was followed by repeated SQLite `disk I/O error` 500s from production API requests.
- Storage and DB checks were clean: `zpool status -x` healthy, config write test OK, `PRAGMA integrity_check = ok`.
- Restarted production `kavita`; API recovered and `/api/library/libraries` returned `200`.
- Re-requested `POST /api/library/scan-all?force=true`; HTTP `200`.
- Retry scan started on `production-library-a`.
- No further SQLite `disk I/O` logs observed after restart; remaining errors were external publisher image fetch failures.

## 2026-06-01 08:31 KST Production EPUB Reader Repair

After production apply, Web UI checks found that test and production behaved differently for some EPUBs:

- `reported page-count EPUB sample` opened in test but production still showed `1/1`.
- `reported duplicate-manifest EPUB sample` produced `Incorrect EPUB manifest: item with href = "image-0001.jpg" is not unique.`

Root cause:

- The fixture library uses local `/fixtures` paths and was rescanned with real EPUB page counts.
- Production `/mnt/gds` paths still use the GDS scan shortcut, so scanner-side EPUB page counts remained `1`.
- Running real page counting during production scan was rejected because it blocked the Web UI against remote GDS reads.

Applied fix:

- Kept `/mnt/gds` scanner shortcut to avoid blocking scans.
- Added on-read EPUB page-count repair from `book-info`, updating `Chapter`, `MangaFile`, `Volume`, and `Series` page totals when an EPUB is opened and the real reading order is greater than the DB value.
- Applied duplicate manifest repair across `BookService` EPUB open paths so reader page rendering and TOC generation use the same temporary repaired EPUB copy.

Production image:

```text
local/kavita-gds:9.0.6-1
sha256:<redacted>
```

Production verification:

```text
kavita health: healthy
internal Web UI: HTTP 200
external kavita.suikano.net via NPM: HTTP 200

reported page-count EPUB sample 1권 chapter sample-chapter-a: pages 1 -> 15, book-page page=2 HTTP 200
reported page-count EPUB sample 2권 chapter sample-chapter-b: pages 1 -> 12
reported duplicate-manifest EPUB sample 1권 chapter sample-chapter-c: pages 1 -> 13, book-page page=2 HTTP 200, chapters HTTP 200
reported duplicate-manifest EPUB sample 2권 chapter sample-chapter-d: pages 1 -> 12, book-page page=2 HTTP 200, chapters HTTP 200
```

rclone safety check:

```text
errors=0
deletes=0
renames=0
serverSideCopies=0
serverSideMoves=0
```

Final forced production scan:

```text
2026-06-01 10:53 KST: not started yet.
```

The user requested GitHub push first while the production cover metadata refresh continues. The forced `scan-all?force=true` step remains a follow-up after cover regeneration completes.

Release package prepared:

```text
kavita-gds.tar.gz sha256=<redacted-hash>
docker-image/kavita-gds.docker.tar sha256=<redacted-hash>
```

GitHub release and GHCR publish:

```text
release=https://github.com/suikano1304/Kavita-GDS/releases/tag/v9.0.6-1
workflow_result=success
ghcr.io/suikano1304/kavita-gds:9.0.6-1 digest=sha256:<redacted>
ghcr.io/suikano1304/kavita-gds:latest digest=sha256:<redacted>
```

## 2026-06-01 09:12 KST Missing EPUB3 NAV follow-up

User Web UI testing found another EPUB reader failure:

```text
EPUB parsing error: NAV item not found in EPUB manifest.
```

Affected production item:

```text
chapter=131366
series=<redacted> reported cover-only EPUB sample
file=<redacted-media-path>
```

Implemented follow-up:

- temporary EPUB repair now also synthesizes a minimal EPUB3 nav document when OPF has no `properties="nav"` item;
- EPUB repair fallback catches `EpubReaderException`, not only `EpubPackageException`;
- applied to `BookService`, `BookController`, and `WordCountAnalyzerService`;
- copied the reported file into a redacted local test fixture path.

Verification:

```text
test image local/kavita-gds:9.0.6-1-test intermediate sha256:<redacted>
LOCAL-FIXTURES scan: 118 files, 27 series, zero pages 0, missing covers 0
test fixture chapter sample-chapter-cover-only: book-info 200, book-page?page=0 200, chapters 200
production image local/kavita-gds:9.0.6-1 final sha256:<redacted>
production chapter <redacted>: book-info 200, book-page?page=0 200, chapters 200
production Web UI internal/external: HTTP 200
rclone: errors=0 deletes=0 renames=0 serverSideCopies=0 serverSideMoves=0
```

Important limitation:

- The reported EPUB contains only `cover.jpg`, `cover.xhtml`, `toc.ncx`, and `content.opf`.
- OPF manifest has only one XHTML item, `cover.xhtml`; spine has only one `itemref`.
- The repair prevents reader failure, but the item remains a legitimate 1-page cover-only EPUB until the source file is replaced with a copy that contains the actual body chapters.

## 2026-06-02 05:01 KST Production Cover Remediation

After the production metadata refresh finished, a final cover remediation pass was run for 2nd-and-later items that either had no cover or still pointed to the same first-volume cover.

Pre-remediation backup:

```text
/kavita/config/kavita.db.coverfix-final-20260602-050124.bak
backup_size=243M
```

Pre-remediation findings:

```text
Chapter cover nulls after full metadata refresh: 253
Volume cover nulls after full metadata refresh: 258

Same-as-first hash audit:
production-library-a: 3213 chapters / 122 series
성인 만화: 378 chapters / 32 series
production-library-d: 1 chapter / 1 series
연재중: 3 chapters / 2 series
production-library-c: 11291 chapters / 1271 series, missing files 57
production-library-a: 11 chapters / 2 series
```

The remediation script nulled affected `Chapter.CoverImage` and `Volume.CoverImage` rows, removed unreferenced generated cover files, and queued forced metadata refresh for the affected series through `POST /api/series/refresh-metadata`.

Remediation summary:

```text
affected_series=1480
refresh_metadata_ok=1480
refresh_metadata_fail=0
deleted_cover_files=12791
cover_files_before=152904
cover_files_after=140113
affected_chapters=15256
affected_volumes=14799
```

Post-refresh result:

```text
remaining Chapter.CoverImage nulls=93
remaining Volume.CoverImage nulls=50
cover_files_after_regeneration=155302
```

Remaining same-hash findings were investigated with direct ZIP first-image hashes. Representative samples:

```text
보표:
1#34.zip 001.jpg fc301a66...
10#48.zip 001.jpg fc301a66...
11#46.zip 001.jpg fc301a66...

파천분뢰수:
01권#110.zip 001.jpg bb8e62a4...
02권#110.zip 001.jpg bb8e62a4...
```

These are not DB reference reuse after the fix. The source archives themselves share the same first image, and Kavita's archive cover rule uses the first image as the volume/chapter cover. Later pages differ, so changing this would require a separate cover-selection policy rather than another forced regeneration.

Residual missing-cover audit:

```text
production-library-d: chapter 43, volume 17
production-library-a: chapter 39, volume 39
production-library-a: chapter 2, volume 2
production-library-c: chapter 1, volume 1
production-library-b/성인 만화/연재중/텍스트 libraries: 0
```

Most residuals are TXT/PDF or source files with no usable image cover; several CBZ/ZIP samples have `Pages=0` or unusual source layout and need separate source-level review.

## 2026-06-02 06:12 KST Forced Scan-All OOM

The requested final `POST /api/library/scan-all?force=true` was attempted after cover regeneration. It consistently OOM-killed the production Kavita process while scanning `production-library-a`.

Observed attempts:

```text
16 GiB LXC memory: kavita killed, exitCode=137, RSS about 11.1 GiB
24 GiB LXC memory: kavita killed, exitCode=137, RSS about 19.5 GiB
32 GiB LXC memory: host global OOM killed kavita, RSS about 27.6 GiB
```

During the first global OOM event the host also killed VM201 (`vm1-ai`). The temporary LXC memory changes were reverted:

```text
pct set 101 -memory 16384 -swap 1024
verified lxc1 total memory: 16 GiB
```

Conclusion: full `scan-all?force=true` is unsafe against the current `production-library-a` library without scanner batching or a lower-memory scan strategy. The production follow-up switched to individual library scans instead.

## 2026-06-02 06:52 KST Individual Library Scan Follow-up

The full force scan was replaced with sequential per-library force scans.

Completed:

```text
LibraryId=5 production-library-b: LastScanned=2026-06-02 06:52:21.3806613
LibraryId=6 production-library-b: LastScanned=2026-06-02 06:55:46.6257085
LibraryId=7 production-library-e: LastScanned=2026-06-02 06:58:07.8451872
LibraryId=4 production-library-a: LastScanned=2026-06-02 07:03:46.5457951
LibraryId=9 production-library-d: LastScanned=2026-06-02 07:08:53.2575256
```

Interrupted:

```text
LibraryId=3 성인 만화: force scan started after production-library-d, but Kavita was OOM-killed at 2026-06-02 07:37:32 KST.
Kavita restart count after recovery: 2
Kavita health after recovery: healthy
External Web UI: HTTP 200, 30467 bytes
```

Cause of the interruption:

- `성인 만화` force scan and the ARM64 build ran concurrently.
- The kernel OOM log shows Kavita RSS about `9.0 GiB` when lxc1 exceeded its 16 GiB memory cgroup.
- The ARM build was allowed to finish, but no further production scans were started during that build.

Final scan state after interruption:

```text
<redacted> production-library-a     2026-06-02 00:00:26.4261487
2 연재중        2026-06-02 00:00:31.7440464
3 성인 만화     2026-06-02 00:00:34.8557038
<redacted> production-library-a        2026-06-02 07:03:46.5457951
<redacted> production-library-b          2026-06-02 06:52:21.3806613
<redacted> production-library-b 2026-06-02 06:55:46.6257085
<redacted> production-library-e   2026-06-02 06:58:07.8451872
<redacted> production-library-c  2026-06-02 00:42:14.7546525
<redacted> production-library-d        2026-06-02 07:08:53.2575256
```

rclone read-only verification:

```text
rclone-gds.service=active
RC deletes=0
RC renames=0
RC serverSideCopies=0
RC serverSideMoves=0
RC errors=7
lastError=Google Drive rateLimitExceeded / quota exceeded
recent log: to upload 0, uploading 0
```

The rclone errors were API quota/rate-limit errors, not write/delete/rename activity.

## 2026-06-02 08:45 KST GDS File Discovery Low-Memory Rebuild

The previous low-memory rebuild fixed the post-file-scan GDS processing path, but production `성인 만화` still OOMed before `Found N Series` appeared. This means the remaining failure was in file discovery/parser aggregation, not cover generation.

New source commit:

```text
e922205 fix: reduce GDS scan discovery memory
```

Changed file:

```text
Kavita.Services/Scanner/ParseScannedFiles.cs
```

Operational behavior changed for GDS libraries only:

- directory discovery now uses a bottom-up streaming traversal instead of materializing the full recursive directory list first.
- GDS file parsing no longer creates one task per file for large folders.
- scan-result parser tracking avoids a separate flattened `ParserInfo` list.
- changed scan results release their retained `Files` list after parse.

Validation before production:

```text
image=sha256:<redacted>
LOCAL-FIXTURES pass 1/2/3: total=118, info_fail=0, nav_fail=0, page_fail=0, zero_bytes=0, zero_pages=0, missing_covers=0
```

Production status at `2026-06-02 08:51 KST`:

```text
production image ID=sha256:<redacted>
kavita health=healthy
kavita restart count=0
LibraryId=3 성인 만화 forced scan started at 2026-06-02 08:45:08 KST
scan phase=file discovery before Found N Series
observed memory=below 1 GiB / 16 GiB
```

## 2026-06-02 07:46 KST ARM64 GHCR Publish

The existing GHCR tag was originally a single `linux/amd64` manifest:

```text
ghcr.io/suikano1304/kavita-gds:9.0.6-1 sha256:<redacted>
```

An ARM64 image was built from the same `port-0906-gds` source. The first Dockerfile path failed at `npm ci` because the package lock did not include arm64 optional dependency entries. The successful build used the already-generated production UI bundle in `UI/Web/dist/browser` and published only the `linux-arm64` runtime.

Build artifact:

```text
temporary Dockerfile: <temporary-dockerfile>
pushed tag: ghcr.io/suikano1304/kavita-gds:9.0.6-1-arm64
arm64 index digest: sha256:<redacted>
arm64 image manifest: sha256:<redacted>
```

The public version and latest tags were then rewritten as a multi-arch manifest:

```text
ghcr.io/suikano1304/kavita-gds:9.0.6-1
ghcr.io/suikano1304/kavita-gds:latest
multiarch digest=sha256:<redacted>

platform linux/amd64 -> sha256:<redacted>
platform linux/arm64 -> sha256:<redacted>
```

## 2026-06-02 Final Skip-Word Production Scan and GHCR Publish

The final skip-word scan image was deployed to production:

```text
production image ID=sha256:<redacted>
container status=running
health=healthy
restart count=0
OOMKilled=false
```

`production-library-c` final forced scan completed on the skip-word image:

```text
Found 13675 Series that need processing in 7519036 ms
Using low-memory sequential GDS scan path for production-library-c. Series to process: 13675
Finished library scan of 63039 files and 13675 series in 11536369 milliseconds for production-library-c
scan complete time=2026-06-02 19:46:55 KST
Finished series update count=13675
WordCountAnalyzerService logs in scan range=0
critical/fatal/OOM logs in scan range=0
```

Library `LastScanned` after the final scan:

```text
<redacted>|production-library-a|2026-06-02 12:34:55.5868943
2|연재중|2026-06-02 13:25:30.6796249
3|성인 만화|2026-06-02 09:29:16.4414823
<redacted>|production-library-a|2026-06-02 07:03:46.5457951
<redacted>|production-library-b|2026-06-02 06:52:21.3806613
<redacted>|production-library-b|2026-06-02 06:55:46.6257085
<redacted>|production-library-e|2026-06-02 06:58:07.8451872
<redacted>|production-library-c|2026-06-02 19:46:55.4197661
<redacted>|production-library-d|2026-06-02 07:08:53.2575256
```

rclone read-only verification:

```text
rclone-gds.service=active/running
service ExecStart includes --read-only
NRestarts=0
recent log: to upload 0, uploading 0
```

The final GHCR publication was rebuilt from the same source snapshot:

```text
ghcr.io/suikano1304/kavita-gds:9.0.6-1-amd64
linux/amd64=sha256:<redacted>

ghcr.io/suikano1304/kavita-gds:9.0.6-1-arm64
arm64 index digest=sha256:<redacted>
linux/arm64=sha256:<redacted>

ghcr.io/suikano1304/kavita-gds:9.0.6-1
ghcr.io/suikano1304/kavita-gds:latest
multiarch digest=sha256:<redacted>
platform linux/amd64 -> sha256:<redacted>
platform linux/arm64 -> sha256:<redacted>
```

Final release archive checksums:

```text
kavita-gds.tar.gz sha256=<redacted-hash>
docker-image/kavita-gds.docker.tar sha256=<redacted-hash>
```

## 2026-06-02 Final Fixture Expansion to 10 Series per Format

After the production scan and public image publication were complete, `LOCAL-FIXTURES` was expanded to meet the literal 10-series-per-format validation target. User-reported EPUB samples were also copied into fixtures with their source folder structure preserved.

Fixture filesystem summary:

```text
cbz series=<redacted>
zip series=<redacted>
epub series=<redacted>
txt series=<redacted>
epub-problem groups=2

cbz media files=44
zip media files=30
epub media files=54
txt media files=27
total validated media files=155
fixture root size=882M
```

Added source directories:

```text
CBZ <redacted-media-path>
CBZ <redacted-media-path>
CBZ <redacted-media-path>
CBZ <redacted-media-path>
ZIP <redacted-media-path>
ZIP <redacted-media-path>
ZIP <redacted-media-path>
ZIP <redacted-media-path>
EPUB <redacted-media-path>
EPUB <redacted-media-path>
EPUB <redacted-media-path>
EPUB <redacted-media-path>
TXT <redacted-media-path>
TXT <redacted-media-path>
TXT <redacted-media-path>
REPORTED <redacted-media-path>
REPORTED <redacted-media-path>
REPORTED <redacted-media-path>
REPORTED <redacted-media-path>
REPORTED <redacted-media-path>
```

Validation on final image `sha256:<redacted>`:

```text
Finished library scan of 155 files and 44 series in 11196 milliseconds for LOCAL-FIXTURES
Finished library scan of 155 files and 44 series in 12234 milliseconds for LOCAL-FIXTURES
Finished library scan of 155 files and 44 series in 11177 milliseconds for LOCAL-FIXTURES

pass=1 total=155 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=2 total=155 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0
pass=3 total=155 info_fail=0 nav_fail=0 page_fail=0 zero_bytes=0 zero_pages=0 missing_covers=0

DB summary: series=<redacted> media=155 zero_pages=0 missing_covers=0
DB format rows: Archive=74 EPUB=54 TXT=27
Reported fixture sample DB checks: reported cover-only EPUB sample, reported page-count EPUB sample, and reported duplicate-manifest EPUB sample all have non-zero pages and cover references.
```

rclone remained read-only during fixture expansion:

```text
rclone-gds.service=active/running
service ExecStart includes --read-only
NRestarts=0
recent log: to upload 0, uploading 0
```
