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
