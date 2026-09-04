# Kavita-GDS 0.9.0.10 Validation

Validation date: 2026-06-22

This records the public-safe validation state for the Kavita-GDS port based on
official Kavita `0.9.0.10`. The candidate was built from an official upstream
base with the GDS patch stack reapplied on top. Production was not upgraded or
restarted during this validation.

## Scope

- Upstream nightly version: `0.9.0.10`.
- GDS patch stack model: official upstream base plus one fork patch stack.
- Scanner, reader, cache, cover, metadata-filter, and GDS library behavior after
  the upstream rebase.
- amd64 source build, test, package, Docker image startup, production-clone
  reader/API smoke, copied original-layout fixture scan, and ARM64 runtime
  smoke.
- Patch-stack helper workflow for future upstream ports.

## Result

The amd64 and ARM64 candidate images are viable for continued validation, but
this is not a production rollout or publish candidate yet.

Passed:

- Source build completed.
- Focused GDS service tests passed.
- UI production bundle and RID package outputs were built.
- amd64 Docker image startup reached `/api/health`.
- Production-clone reader/API smoke passed for representative GDS formats.
- Windows/off-host amd64 fresh-container smoke passed.
- Windows/off-host copied original-layout fixture scan passed.
- Windows/off-host ARM64 qemu fresh-container smoke passed.
- Patch-stack export/apply self-test passed with same-base tree equality.

Not yet passed:

- GHCR manifest publish/inspection.
- Production targeted validation.
- Final release cleanup.

Platform scope:

- Initial publish scope is amd64 and ARM64.
- ARMv7 is excluded from the initial `0.9.0.10` manifest unless it is built and
  runtime-smoked separately.

## Reader And Scan Coverage

The validation exercised representative GDS and copied fixture samples across:

- EPUB book reader, including page-count and tolerant parsing paths.
- TXT book reader and pagination paths.
- Archive/image reader paths, including page image and dimension endpoints.
- PDF reader paths.
- Cover fallback and generated-cover paths.
- GDS original-layout scan behavior with same-folder sidecars preserved.
- SQLite integrity and log checks before and after targeted validation.

## Patch Stack Workflow

The porting workflow now includes helpers for future upstream rebases:

- `scripts/gds_patch_stack_summary.sh`
- `scripts/gds_export_patch_stack.sh`
- `scripts/gds_apply_patch_stack.sh`
- `scripts/gds_porting_selftest.sh`

The self-test exports the current GDS patch stack, applies it to a temporary
upstream worktree, checks final tree equality when the base commit matches, runs
public-doc privacy checks, and removes temporary artifacts.

## Operational Notes

- Production remained on the previous published image.
- The disposable test container was stopped after validation.
- A production-clone SQLite I/O incident was treated as a test-clone operational
  incident. The affected clone was discarded, and follow-up validation used safer
  off-host or copied-fixture paths.
- Broad real-media scans were deferred because media-host I/O pressure remained
  high.
- ARMv7 must not be added to a published manifest unless its own runtime smoke
  reaches `/api/health`. ARM64 has passed off-host qemu `/api/health` smoke.

## Release Decision

Do not publish or roll this candidate into production until every release blocker
is resolved. In particular, each architecture included in the release manifest
must pass runtime health smoke, and the local regression matrix must have no
`FAIL` entries.

## 2026-06-23 `9.0.10-2` RC addendum

This RC keeps the official Kavita `0.9.0.10` base and adds a small GDS patch
set for production-reported web-novel and WebUI behavior:

- Improved Korean/Japanese completion marker parsing for web-novel filenames,
  including bracketed markers and end-marker ranges while avoiding title-internal
  false positives.
- Fixed the WebUI right-side JumpBar/list click behavior so it scrolls by the
  visible item key even when the current list is sorted by fields such as title
  or last modified.
- Corrected the Korean label for `PublicationStatus.Ended` from a missing-data
  wording to an ended/closed wording.

Validation completed before production promotion:

- Focused parser unit tests passed for bracketed markers, separated markers,
  range markers, and title-only false-positive cases.
- The amd64 package and local Docker image started successfully and reached
  `/api/health`.
- A production DB metadata backfill was applied only after SQLite online backup,
  stopped-copy backup, WAL checkpoint, and `quick_check`.
- Production health remained `Ok` after promotion and after the backfill.

The release manifest must still include only platforms that have passed pushed
image startup smoke for this RC. ARM64 and ARMv7 remain release gates, not source
differences.

## 2026-06-23 `9.0.10-3` JumpBar addendum

This patch keeps the official Kavita `0.9.0.10` base and the `9.0.10-2` GDS
patch stack, then adds WebUI/runtime fixes for the right-side card-list JumpBar.

Changes:

- Series card-list DTOs expose `LastModified` and metadata `ReleaseYear` so the
  WebUI can build sort-aware JumpBar targets from the same fields users are
  sorting by.
- Series list views now request sort-aware JumpBar keys. Title sorting keeps
  alphabetic initials; date and numeric sorts use sampled positions from the
  actual sorted list so large same-value buckets can still be navigated.
- The virtualized card layout scrolls directly to a JumpBar key's sampled item
  index when present.
- Runtime Docker images clear old `wwwroot` files before copying the production
  UI bundle, preventing stale lazy chunks from surviving between releases.
- Repeated long labels, such as many entries from the same last-modified date,
  render the first full label and compact markers for following jump points while
  keeping the original label as the tooltip.

Validation before promotion:

- `git diff --check` passed.
- Angular `npm run build` and `npm run prod` passed with only existing Sass,
  style-budget, and CommonJS warnings.
- `dotnet build Kavita.Models/Kavita.Models.csproj --no-restore` passed for the
  DTO/profile changes.
- `kavita-test` ran the candidate image at `https://tkavita.suikano.net` and
  reached local/routed `/api/health` `Ok` with Docker health `healthy`.

The release manifest must include `linux/amd64`, `linux/arm64`, and
`linux/arm/v7` only after pushed-image startup smoke reaches `/api/health`.

Release result:

- GHCR `ghcr.io/suikano1304/kavita-gds:9.0.10-3` and `latest` point to the
  same multi-arch digest:
  `sha256:b3f9ed89796cbdfb24e0dee44bbf7a8d5a4bd72cdd3333f333110cc5836da8dd`.
- Platform manifests:
  - `linux/amd64`:
    `sha256:a450319e5aa1c2d8e2cd1ae1a0b67db18f471b5b8a278e096344d1119b194c4d`
  - `linux/arm64`:
    `sha256:20736a7f473005c37b365a5570cbf47034a46bec11514f48514cc03617a78419`
  - `linux/arm/v7`:
    `sha256:b49075079765946a3ba672ab21124e009275f961a87a1dd54383b1d44ad6b1d9`
- Pushed-image smoke reached `/api/health` `Ok` on all three platforms.
- Production was promoted to
  `ghcr.io/suikano1304/kavita-gds:9.0.10-3` after a SQLite online backup and
  `quick_check`; local and routed production health returned `Ok`.
