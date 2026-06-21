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
  reader/API smoke, and copied original-layout fixture scan.
- Patch-stack helper workflow for future upstream ports.

## Result

The amd64 candidate is viable for continued validation, but it is not a
production rollout or publish candidate yet.

Passed:

- Source build completed.
- Focused GDS service tests passed.
- UI production bundle and RID package outputs were built.
- amd64 Docker image startup reached `/api/health`.
- Production-clone reader/API smoke passed for representative GDS formats.
- Windows/off-host amd64 fresh-container smoke passed.
- Windows/off-host copied original-layout fixture scan passed.
- Patch-stack export/apply self-test passed with same-base tree equality.

Not yet passed:

- ARM64 runtime smoke.
- ARMv7 runtime smoke, if armv7 remains part of the release manifest.
- GHCR manifest publish/inspection.
- Production targeted validation.
- Final release cleanup.

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
- ARM images must not be added to a published manifest until runtime smoke
  reaches `/api/health`.

## Release Decision

Do not publish or roll this candidate into production until every release blocker
is resolved. In particular, each architecture included in the release manifest
must pass runtime health smoke, and the local regression matrix must have no
`FAIL` entries.
