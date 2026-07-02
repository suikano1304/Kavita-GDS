# Kavita GDS

This release provides the Kavita official `0.9.0.12` nightly based GDS build as a GHCR multi-arch Docker image.

Version: `0.9.0.12-2`

## Included Platforms

- `linux/amd64`
- `linux/arm64` on GHCR
- `linux/arm/v7` on GHCR

## Asset

GHCR is the primary distribution channel for this release. Use the unified version tag; Docker will select the matching platform automatically.

## Verification

- **Korean search normalization improvement (0.9.0.12-2)**: Added Unicode NFC normalization to the search text normalizer so decomposed (NFD) Hangul input matches composed (NFC) database data. Added normalized-name fields for chapters and libraries so they get the same whitespace-insensitive search as series. Fixed three query mismatches where normalized fields were compared against non-normalized search terms or were missing entirely. A startup migration backfills normalized fields for existing records without a full library rescan.
- Published GHCR `0.9.0.12-2` and `latest` as one multi-arch manifest covering `linux/amd64`, `linux/arm64`, and `linux/arm/v7`.
- Manifest digest: `sha256:3ea108084fa6b71a7939d742aa55ff3c9a16cfe608261b90f75e520045b2c3d0`.
- Per-platform manifests:
  - `linux/amd64`: `sha256:e3ab2aaf34df671c7bcffe8c1d1002ce50b6812a3de99f73028d01854314dda6`
  - `linux/arm64`: `sha256:fef06bcae28254882c8007340c46be77b574f2d90825cb32f79415bbb0d83ffd`
  - `linux/arm/v7`: `sha256:c570a08f06df8fb653e1f99827a4cc8475e545795e8e575060b44d281f430dc6`
- Windows Docker Desktop buildx built and pushed all 3 platforms with `--no-cache`.
- Pushed GHCR `linux/amd64`, `linux/arm64`, and `linux/arm/v7` image startup all returned `/api/health` `Ok` (Docker health `healthy`) after an explicit `docker pull` of the pushed digest.
- Production rollout is on hold for this release pending user confirmation.

## Previous `0.9.0.12-1` Verification

- Built from the official Kavita `0.9.0.12` nightly source with the GDS patch set ported forward.
- Published GHCR `0.9.0.12-1` and `latest` as one multi-arch manifest covering `linux/amd64`, `linux/arm64`, and `linux/arm/v7`.
- Manifest digest: `sha256:8ed8199bf1c62b54e629f86e23482d81b43fb2880320b82c9275ea5ea0d66cd8`.
- Per-platform manifests:
  - `linux/amd64`: `sha256:202b4bf266fdd5b6b5176967a794343345ca9327f484f8b40176b1187a58fff8`
  - `linux/arm64`: `sha256:099c07ddcfa68973ef4fc039d8d277180a48f2c6d503bc328b7883dd436b1f8e`
  - `linux/arm/v7`: `sha256:86fdbf199cde125abb57ace0c64188c74925d0bc76119b0177ac4377d69fc8fe`
- **GDS mtime bypass (0.9.0.12-1)**: Fixed Library Scan missing new files on rclone FUSE mounts where `--dir-cache-time` froze directory mtimes. GDS libraries now always enumerate all registered series directories.
- OPDS: upstream #4759 resolved single-entry+merged-cbz handling; GDS patch stack inherits upstream OPDS behavior without custom patches.
- Windows Docker Desktop buildx built and pushed all 3 platforms.
- Pushed GHCR `linux/amd64` image startup returned `/api/health` `Ok`.
- Windows qemu smoke for `linux/arm64` image reached container startup.
- Production `kavita` was rolled to `0.9.0.12-1` and verified with `/api/health` `Ok`, Docker health `healthy`.

## Previous `0.9.0.12` Port

- Built from the official Kavita `0.9.0.12` nightly source with the GDS patch set (41 commits) ported forward.
- Resolved merge conflicts: `ImageService.cs` (upstream thumbnailHeight + GDS CreateTitleCover 6-arg signature), `GdsCoverServiceTests.cs` (mock 5-arg→6-arg).
- Published GHCR `0.9.0.12` and `latest` as one multi-arch manifest covering `linux/amd64`, `linux/arm64`, and `linux/arm/v7`.
- Manifest digest: `sha256:e6c12f19a77edb051eeb439c9655ae650133e08baf1c7df70ec4e682b298bd61`.
- Windows .NET 10 SDK 10.0.301 + node v23 + Docker Desktop buildx `gdswin`.
- Production `kavita` was rolled from `9.0.10-3` to `0.9.0.12` and verified healthy.

## Previous `9.0.10-3` Verification

- Built from the official Kavita `0.9.0.7` nightly source with the GDS patch set ported forward.
- Rebuilt the production WebUI bundle for the metadata filter default hotfix.
- Added a WebUI hotfix so saving metadata filter/sort settings with a blank smart-filter name updates the route-local default filter instead of requiring a named smart filter.
- Kept the metadata-filter save button enabled for blank-name default-filter saves while preserving named smart-filter saves.
- OPDS compatibility experiment patches were reverted before the final `9.0.7-6` publish. Existing OPDS functionality remains, but the experimental OPDS acquisition/progress changes are not part of this release.
- Added a reader/cache hotfix that prefers a readable non-empty book file when a chapter has both broken/empty and valid EPUB rows.
- Kept the GDS targeted scan follow-up hotfix that skips word-count analysis and global metadata/cache cleanup after GDS series scans.
- Built RID-specific backend packages for `linux-x64`, `linux-arm64`, and `linux-arm`.
- GHCR `9.0.7-6` and `latest` are published as one multi-arch manifest covering `linux/amd64`, `linux/arm64`, and `linux/arm/v7`.
- Manifest digest: `sha256:bbdfcff8d1e6b070af1cad78a82c5515ed0292e8e04cb057f839d70cde73206c`.
- Per-platform manifests:
  - `linux/amd64`: `sha256:e1e2ebb9059257bc24d8756e629d768f722646e253b0dca6805071f173b41e0b`
  - `linux/arm64`: `sha256:57109c8ed67bab282d071d7d498fea3b56516d59b15fdb5fb3f3237ab24f98dd`
  - `linux/arm/v7`: `sha256:254c022caed57acb6bfb59788f2f8d9c5ae07060c0454c4d9027a9fbe91f1f4e`
- Duplicate broken/valid EPUB row baseline was preserved; `CacheServiceTests` rerun returned `DOTNET_EXIT:0`.
- Local `linux/amd64` release image startup returned `/api/health` 200.
- Local `linux/arm64` release image startup under qemu returned `/api/health` 200.
- Local `linux/arm/v7` release image startup under qemu returned `/api/health` 200.
- Pushed GHCR `linux/amd64`, `linux/arm64`, and `linux/arm/v7` image startup returned `/api/health` 200; Docker health reached `healthy` on all three smoke containers.
- Production `kavita` was rolled to `9.0.7-6` and verified with `/api/health` 200, Docker health `healthy`, and restart count `0`.
- GDS library scans now use a low-memory sequential processing path for DB updates and cover generation to reduce OOM risk on large rclone-backed libraries.
- GDS file discovery now avoids the highest-memory scanner paths by streaming directory traversal, parsing large GDS folders sequentially, and releasing retained file lists after parse.
- GDS library scans skip forced word-count analysis during the scan path; word-count can still be run separately through analyze actions. This keeps cover-focused forced scans from re-reading large remote EPUBs for minutes per series.
- Fixture reader validation passed 3 full passes across the expanded LOCAL-FIXTURES corpus: CBZ `10` series, ZIP `10` series, EPUB `10` series, TXT `10` series, plus retained redacted problem EPUB samples.
- Production Web UI, NPM proxy access, EPUB reader page rendering, table-of-contents, and duplicate manifest repair were verified.
- rclone RC remained read-only: `deletes=0`, `renames=0`, server-side copy/move counters `0`. Later production scan attempts accumulated Google Drive rate-limit errors, not write/delete activity.
- The package does not include intermediate test images.

## Pre-Release Regression Checklist

Before publishing the next release, run the repeatable GDS regression checklist:

- [docs/GDS_REGRESSION_CHECKLIST_KO.md](docs/GDS_REGRESSION_CHECKLIST_KO.md)

The local-only matrix with actual sample titles, raw ids, and media paths is kept outside this public repository.

## Changes Since `9.0.7-6`

### 2026-06-23 `9.0.10-3` sort-aware JumpBar density hotfix

- Improved the right-side JumpBar for large non-title sorted series lists, especially recent-modified views with many items sharing the same date.
- Exposed series `LastModified` and `ReleaseYear` to the WebUI so JumpBar labels can follow the active sort mode.
- Added sampled direct-index JumpBar keys and compact repeated-label markers so repeated date blocks still provide multiple usable jump targets.
- Cleared stale runtime WebUI files during Docker image assembly before copying the current production bundle.
- Published `9.0.10-3` and `latest` as the same GHCR multi-arch manifest for `linux/amd64`, `linux/arm64`, and `linux/arm/v7`.
- Rolled production `kavita` to the GHCR `9.0.10-3` amd64 image and verified health.

### 2026-06-23 `9.0.10-2` web-novel completion and JumpBar hotfix

- Improved scanner parsing for web-novel completion markers in filenames, including bracketed Korean/Japanese markers and end-marker ranges.
- Preserved title-internal false-positive avoidance for names that merely contain completion-related words.
- Fixed the WebUI right-side JumpBar/list click behavior so current title/modified-date sorted lists scroll to the selected item.
- Corrected the Korean `Ended` publication-status label.
- Published `9.0.10-2` and `latest` as the same GHCR multi-arch manifest for `linux/amd64`, `linux/arm64`, and `linux/arm/v7`.
- Rolled production `kavita` to the GHCR `9.0.10-2` amd64 image and verified health.

### 2026-06-22 `9.0.10-1` official `0.9.0.10` port

- Ported the maintained GDS patch set onto official Kavita `0.9.0.10`.
- Preserved the upstream scanner performance improvement for scanning large numbers of files in one directory.
- Published `9.0.10-1` and `latest` as the same GHCR multi-arch manifest for `linux/amd64`, `linux/arm64`, and `linux/arm/v7`.
- Added `linux/arm/v7` after rebuilding the armv7 image and passing pushed-GHCR qemu runtime smoke.
- Passed source build, focused GDS regression tests, UI production build, local amd64 Docker startup smoke, production-targeted reader/API validation, Windows amd64 runtime smoke, Windows ARM64 qemu smoke, and Windows copied original-layout fixture scan.
- Left production `kavita` on its existing image; production rollout is a separate operation.

## Historical Changes Since `9.0.7-1`

### 2026-06-11 `9.0.7-6` metadata-filter hotfix

- Fixed unnamed metadata filter saves in WebUI so blank-name save updates the current route default filter and keeps named smart-filter saves unchanged.
- Fixed the WebUI save-button state so blank-name default-filter saves remain clickable.
- Reverted the OPDS compatibility experiment patches before final publish; no new OPDS behavior is claimed in this release.

### 2026-06-10 `9.0.7-5` readable book-file selection hotfix

- Fixed reader/cache selection when a chapter has both broken/empty and valid EPUB rows.
- Added `ChapterFileSelector.GetBestReadingFile()` and applied it to cache copy, cached file lookup, book-info, book-page, EPUB resource, and TOC generation paths.
- Preserved legacy first-file behavior when no file analysis exists for any attached row.
- Added regression tests for readable EPUB preference and cache copy behavior.
- Revalidated the affected production regression sample in `kavita-test` with a production DB clone and read-only GDS mount, then repeated cold-cache reader API validation after production rollout.
- Updated the release gate rule: newly discovered code-fixable failures found during release validation must be recorded, patched into the current candidate, and retested before rollout.

### 2026-06-10 `9.0.7-4` GDS targeted scan follow-up hotfix

- Skipped word-count analysis after GDS targeted series scans so cover or metadata refreshes do not re-read large remote EPUB sets through the word-count path.
- Skipped global metadata cleanup and whole-cache cleanup after GDS targeted series scans; series-local chapter cache cleanup remains in place.
- Added regression tests for the GDS targeted scan follow-up gates.
- Revalidated a production-clone test container with the original GDS directory layout mounted read-only.
- Revalidated production targeted GDS scans and batched representative-cover normalization without post-scan CPU spin.

### 2026-06-10 `9.0.7-3` GDS cover scan hardening and WebUI cover cache fix

- Fixed targeted GDS series scans so mixed-root series scan only the actual attached file directories instead of a broad category or library root.
- Fixed mixed-format GDS scan grouping so TXT/EPUB/PDF/archive files attached to the same normalized series are not dropped because their formats differ.
- Preserved concrete GDS `LowestFolderPath` when a series spans multiple real file directories.
- Added explicit post-scan completion logging after cleanup enqueue so targeted scan jobs have an observable finish point.
- Fixed stale cover display after scan/refresh by adding cover image URL cache-busting on cover update events and no-cache headers on cover endpoints.
- Added regression coverage for mixed TXT and EPUB volumes so TXT title fallback does not displace EPUB media cover as representative series cover.
- Documented that bulk representative-cover normalization must be latency-gated and batched to avoid WebUI slowdown during production cleanup.

### 2026-06-09 `9.0.7-2` GDS cover refactor and TXT YAML cover precedence fix

- Moved GDS-specific cover generation out of `MetadataService` into a dedicated GDS cover service.
- Reduced the upstream rebase conflict surface to dependency injection plus a small `LibraryType.GDS` hook in `MetadataService`.
- Preserved the GDS cover priority in one service: folder cover for series, file-level YAML base64 for exact chapters, media internal cover fallback, then TXT title fallback only when no image cover exists.
- Fixed TXT-only GDS import/refresh so file-level YAML base64 cover wins over generated TXT title cover.
- Treated `cover: TEXT`, external URLs, invalid base64, empty YAML, and NUL-filled YAML as non-image hints without blocking media import.
- Kept known unrecoverable source-cover samples classified as source-data issues rather than code failures.

## Historical Changes Since `9.0.7`

### 2026-06-09 `9.0.7-1` GDS cover and SQLite hotfix

- Hardened GDS cover extraction when sidecar cover metadata is empty, malformed, or unusable.
- Ensured generated GDS chapter covers are persisted through volume and series cover references.
- Reverted an upstream SQLite startup/write-path regression that could surface as transient WebUI disk I/O errors in this environment.

## Historical Changes Since `9.0.6-2`

### 2026-06-06 `9.0.7` official `0.9.0.7` nightly port

- Merged official Kavita `0.9.0.7` nightly changes into the GDS port.
- Kept the GDS reader metadata refresh stabilization patch.
- Confirmed the upstream book access guard and GDS no-store cache policy remain compatible.
- Confirmed non-Kavita+ operation remains viable for ordinary Book libraries; only GDS is excluded from Kavita+ metadata handling.
- Revalidated GDS reader/API behavior across TXT, ZIP/CBZ archive, EPUB, PDF, cover, repeated cache, DB integrity, and post-test logs.
- Verified the synthetic single-spine EPUB fixture only as a TOC page mapping regression target; it intentionally has no cover.
- Built and pushed `linux/amd64`, `linux/arm64`, and `linux/arm/v7` from the same source patch set using the official RID/platform mapping.

## Historical Changes Since `0.9.0.2-8`

### 2026-06-02 `9.0.6-2` scan/page-count stabilization

- Removed the GDS EPUB/PDF/TXT scanner shortcut that could persist new or rebuilt remote book files as `Pages = 1`.
- Added malformed `kavita.yaml` fallback handling so bad sidecar YAML no longer drops the entire media file from the scan.
- Added an explicit final scan-job completion log after post-scan cleanup and abandoned metadata cleanup.
- Preserved folder-level GDS series covers when later volume/chapter cover generation runs.
- Added backend virtual pages for EPUBs that have one XHTML spine item but multiple internal TOC anchors.
- Verified `kavita-test` on `linux/amd64` with 3 LOCAL-FIXTURES passes: `155` media items, all API/page/nav/cover checks passed.
- Verified a synthetic single-spine EPUB regression fixture: DB pages `3/3`, `book-info` pages `3`, TOC `3`, `book-page` 0/1/2 all returned distinct content.
- Verified production `local/kavita-gds:9.0.6-2` health and corrected a redacted duplicate-manifest EPUB sample chapter range from `1/1` to `12/12`, `12/12`, `12/12`, and `13/13`.
- Confirmed the `reported cover-only EPUB sample` fixture EPUB contains only `cover.xhtml`, `cover.jpg`, `toc.ncx`, and no body content XHTML, so its `1/1` state is source-file corruption rather than a recoverable Kavita page-count issue.
- Built and pushed GHCR `linux/amd64` and `linux/arm64` images. The arm64 image was started under qemu and returned `/api/health` 200.
- Added `linux/arm/v7` after qemu validation with CoreCLR write-xor-execute disabled and a longer healthcheck start period for qemu ARM32 startup.

### 2026-06-01 `9.0.6-1` official `0.9.0.6` port

- Ported the maintained GDS/rclone patch set from `0.9.0.2-8` onto official Kavita `0.9.0.6`.
- Added on-read EPUB page-count repair so GDS EPUBs that scanned as `1/1` update their chapter/file/volume/series page totals when opened.
- Expanded malformed EPUB manifest repair to duplicate exact items, duplicate ids, and duplicate `href + media-type` entries.
- Applied the EPUB repair path across `book-info`, `book-page`, table-of-contents, resource, metadata, and word-count paths.
- Normalized EPUB resource keys so relative `../Images/...` links can resolve correctly.
- Kept GDS media mounts read-only and avoided scanner-side full EPUB reads against `/mnt/gds`.
- Fixed GDS archive cover regeneration so later volumes/chapters do not reuse the first volume cover.
- Fixed Korean TXT title-cover rendering by bundling Nanum Gothic Regular/Bold/ExtraBold in the runtime image.
- Added a low-memory GDS scan path that keeps per-series DB update, cover generation, and word-count work sequential instead of running all post-scan work in parallel.
- Added a GDS-only low-memory file discovery path that streams bottom-up directory traversal and avoids one parse task per file in large folders.
- Split GDS word-count analysis out of the library scan path after a large production GDS library showed 13,675 series and per-series EPUB word-count work taking up to 130 seconds. Cover generation remains part of the forced scan path.
- Included `sqlite3` in the runtime image for operational DB/API verification inside the container.
- Made cache cleanup tolerate concurrent directory deletion.
- Validated redacted production EPUB regression samples after deployment.
- Built the public release package as a `linux/amd64` Docker archive and published GHCR tags as `linux/amd64` + `linux/arm64` multi-arch manifests.

## Historical Changes Since `kavita-gds-0.9.0.2-scan-20260528`

### 2026-05-31 `0.9.0.2-8` default series sort hotfix

- Fixed the default series sort so empty/new filter state uses last modified descending instead of sort-name ascending.
- Fixed the Web UI filter state handling that converted an explicit descending value (`false`) back to ascending.
- Updated backend null-sort fallbacks for series and bookmarks to last modified descending.
- Verified the production API returns the same first page as `Series.LastModified desc` when no sort option is supplied.
- Rebuilt `linux/amd64` and `linux/arm64` self-contained runtime packages.
- Built a new multi-platform OCI archive with both `linux/amd64` and `linux/arm64`.
- Passed a local `linux/amd64` startup smoke test for `0.9.0.2-8`.

### 2026-05-31 `0.9.0.2-7` GDS archive cover fallback hotfix

- Fixed GDS cover generation so archive-based series without a YAML/base64 cover or TXT title cover fall back to the normal ZIP/CBZ first-page cover extraction path.
- This fixes newly discovered GDS archive series being registered with valid files and page counts but no `Series`, `Volume`, or `Chapter` cover reference.
- Existing GDS TXT title-cover behavior is preserved.
- Verified the focused word-count regression test still passes.
- Rebuilt `linux/amd64` and `linux/arm64` self-contained runtime packages.
- Built a new multi-platform OCI archive with both `linux/amd64` and `linux/arm64`.
- Passed a local `linux/amd64` startup smoke test for `0.9.0.2-7`.

### 2026-05-31 `0.9.0.2-6` mixed-format word-count hotfix

- Fixed the word-count analyzer so an EPUB-format series skips non-EPUB files instead of opening PDF/TXT files with the EPUB reader.
- This prevents misleading "There was an issue counting words on an epub" errors when a GDS mixed-format series contains non-EPUB chapters.
- Added a regression test for non-EPUB files inside an EPUB-format series.
- Verified `linux/amd64` and `linux/arm64` self-contained publish.
- Built a new multi-platform OCI archive with both `linux/amd64` and `linux/arm64`.
- Passed a local `linux/amd64` startup smoke test and confirmed the Web UI bundle does not contain `localhost:5000`, `:5000/api`, or Angular development-mode strings.

### 2026-05-31 `0.9.0.2-5` Web UI production hotfix

- Fixed the public Docker image Web UI bundle so it uses the production environment.
- The broken `0.9.0.2-4` image could call `localhost:5000/api` from the browser when accessed through a host port such as `5657:5000`.
- Rebuilt the Angular UI after deleting stale `dist` output, then rebuilt the image after deleting the old `/kavita/wwwroot` contents.
- Verified the container no longer contains `localhost:5000`, `:5000/api`, or Angular development-mode strings in `/kavita/wwwroot`.
- Verified the production environment chunk uses the document base URL and resolves API calls as same-origin `/api/`.
- Built a new `linux/amd64` and `linux/arm64` OCI archive for `0.9.0.2-5`.

### 2026-05-31 `0.9.0.2-4` source/release alignment

- Added GDS handling to chapter-title routing in continue-reading and volume-detail views.
- Added GDS handling to the old file-type migration so legacy databases include Archive, EPub, Pdf, Images, and Text groups for GDS libraries.
- Built `linux/amd64` and `linux/arm64` runtime tarballs from the same source snapshot.
- Built a multi-platform OCI archive containing both `linux/amd64` and `linux/arm64`.
- Passed a local `linux/amd64` startup smoke test.
- Separated the Oracle A1 startup FK report from generic x86/NAS behavior. If the same database or image starts on x86/NAS but fails on Oracle A1, compare the existing DB migration state, volume mapping, previous shutdown state, and foreign-key diagnostics first.

### 2026-05-31 startup FK diagnostics and duplicate cleanup

- Separated startup migration execution from later BaseUrl setting persistence so a failed migration is not hidden behind a later settings save.
- Startup migration exceptions are now logged and rethrown instead of being swallowed and surfacing as a misleading later `SaveChanges` failure.
- If BaseUrl persistence fails with a database update exception, Kavita logs `PRAGMA foreign_key_check` output to make existing DB damage or migration-state issues visible.
- Same-volume duplicate file path cleanup now preserves the chapter selected by the current scan instead of retaining an arbitrary earlier duplicate.
- The read-only diagnostic script now prints SQLite foreign-key violations and classifies duplicate file path groups into cleanup-safe and cross-series cases.
- The read-only diagnostic script now includes EF migration history, manual migration history, selected server settings, and core table row counts to compare x86/NAS and Oracle A1 startup reports.
- The read-only diagnostic script now classifies MediaError rows into EPUB structure, PDF metadata/encryption, archive support, and scanner-unrecognized buckets.
- The preflight collector records host architecture and Docker engine details, which helps separate Oracle A1-only startup reports from generic x86 or image architecture issues.
- Postflight comparison can now print explicit `PASS`, `WARN`, and `FAIL` gates for integrity, foreign keys, `Pages=0`, duplicate file paths, media errors, GDS config cover references, and TXT missing-cover debt.
- Archive validation can be written into the JSON baseline, so postflight gates can separate recoverable direct-image `Pages=0` archives from nested archive structures.
- Added a read-only scan-log timing summarizer that reports library scan, file discovery, series update timings, and slow reader HTTP requests without exposing library or series names by default.
- Added a read-only reader latency correlator that maps slow reader requests to DB file size, format, page count, and cache-folder state without exposing titles or paths by default.
- Built and packaged as `0.9.0.2-4` for `linux/amd64` and `linux/arm64`.

### 2026-05-31 GDS TXT fallback cover and scan debt recovery

- Added a local title-cover generator for GDS TXT series that have no folder cover and no YAML base64 cover.
- Treated `cover: TEXT` and external cover URLs as non-image hints, so the scanner does not mistake them for embedded cover data.
- Generated fallback covers are written only to Kavita config cover storage, never to the GDS/rclone media mount.
- Bundled Nanum Gothic in the Docker image so generated Korean title covers render correctly.
- Preserved existing config cover cache when the GDS source folder has no `cover.*` file.
- Forced a real rescan for GDS series that still have `Pages=0` files, allowing stale scan debt to recover on the next library scan.
- Verified backend build, UI production build, generated-cover smoke test, `linux/amd64` container startup, and multi-arch OCI manifest contents.

### 2026-05-31 GDS incremental scan stabilization

- Matched GDS format subfolders back to their parent series folder when checking no-change scan state.
- Replaced direct unchanged-folder path lookups with a safe fallback lookup so missing alias paths do not trigger repeated scan retries.
- Matched split sibling folders by normalized directory name for GDS libraries, reducing repeated processing when physical folder names differ slightly.
- Test verification: repeated normal scan for the affected mixed-format library dropped from `5 Series / 108 files / about 7-10 seconds` to `0 Series / 0 files / about 0.8 seconds`.

### 2026-05-31 GDS light-novel mixed folder/readability patch

- Fixed GDS `chapter-info` handling for `LibraryType.GDS` so book/PDF reader routing does not hit an unsupported library type path.
- Preserved existing GDS volumes when their files still exist on disk, even if one side of a split folder layout is not present in an incremental scan result.
- Added a fast minimum page count for unchanged GDS EPUB/PDF/TXT files so they do not remain at `Pages=0` and appear unreadable.
- Allowed `force=true` GDS scans to perform a real filesystem rescan, which is slower but can recover files missed by an earlier incremental scan.
- Operational verification: a split-folder light-novel series now keeps 8 files total: 3 ZIP files and 5 EPUB files. The EPUB continue-point returns `pages=1` and the EPUB page endpoint returns content.

### 2026-05-31 GDS rescan speed patch

- Avoided recalculating page count and KOReader hash for unchanged GDS files during forced scans.
- Kept normal GDS/rclone rescans fast by avoiding unnecessary file stat/hash work on unchanged files.
- Excluded bracketed cover files such as `[Cover].jpg` from GDS media parsing.
- Skipped repeated folder-cover copy/color analysis when the local cover and colors are already present.
- Operational verification: two representative production GDS libraries completed forced scans of `11 files / 187 series` in about 2.8 seconds and `2 files / 2061 series` in about 4.5 seconds.

### 2026-05-31 operational verification

- Documented the production cover recovery and YAML metadata validation process.
- Added the GDS YAML metadata fix to the operational notes.
- GDS sidecar YAML now supplies safe metadata fields such as summary, tags, people, publisher, release date, and age rating.
- YAML `meta.Name` is no longer used to overwrite chapter titles.
- GDS chapter titles are derived from file names with common distribution/quality suffixes removed.
- Verified that publisher/category-prefixed folders did not create duplicate series during validation.

### 2026-05-29 fixes

- Added EPUB manifest duplicate ID recovery for malformed/generated EPUB files.
- Added PDF XRef recursion depth guard to prevent hangs on damaged PDFs.
- Reworked large rclone/FUSE directory enumeration to avoid recursive scan hangs.

### 2026-05-30 GDS scanfix

- Fixed GDS reader/runtime issues.
- Reduced series splitting when multiple formats exist under the same work folder.
- Excluded `kavita.yaml`, `kavita.yml`, `cover.*`, and similar metadata files from media scans.
- Prevented loose `.jpg` files under web-novel style paths from becoming false series/volumes.
- Added guards so GDS scans preserve DB rows and do not attempt source cleanup.
- Kept GDS cover handling inside Kavita config cover storage rather than writing to media paths.
- Improved GDS `FolderPath` and change detection by considering actual DB file parent directories.
- Reduced repeated scan churn and stabilized no-change rescans.
- Rebuilt UI delivery to avoid stale Angular chunks and set static cache policy to no-cache/no-store.
- Restored default series ordering by last modified descending.

### Multi-arch packaging

- Packaged the scanfix build as one OCI archive containing `linux/amd64` and `linux/arm64`.
- Excluded intermediate test images and the later webtoon patch tree from this public package.
- Added a GHCR publishing workflow so users can deploy with `docker pull ghcr.io/suikano1304/kavita-gds:0.9.0.2-5`.

## Caveat

The `linux/arm64` and `linux/arm/v7` images were built from the same source and passed qemu startup smoke tests. `linux/arm/v7` additionally reached Docker health `healthy` with the CoreCLR write-xor-execute workaround enabled. Native ARM long-running production deployment was not validated in this release record.
