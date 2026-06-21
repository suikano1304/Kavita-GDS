# Kavita-GDS Upstream Porting Workflow

This document defines the preferred Kavita-GDS version-up model:
start from an official Kavita upstream base, then reapply the GDS patch set as
one source stack. Do not maintain separate source patches per CPU architecture.

## Release Model

- Base source comes from official Kavita, normally `upstream/develop` or a
  specific upstream release/nightly tag.
- GDS changes are carried as a fork-only patch stack on top of that base.
- Architecture differences are handled only at build/package time:
  - `linux/amd64` uses .NET RID `linux-x64`.
  - `linux/arm64` uses .NET RID `linux-arm64`.
  - `linux/arm/v7`, when included, uses .NET RID `linux-arm`.
- A platform is added to a published manifest only after its runtime smoke test
  reaches `/api/health`.

## Inputs To Record

Record these values in the local release audit before any publish or production
rollout:

- upstream base ref and commit
- upstream Kavita version
- source branch containing the port
- previous GDS release branch or tag used as the patch source
- expected GDS release tag
- local validation image tag
- target GHCR tag
- copied-fixture and production-clone validation status
- per-platform smoke status

## Port Procedure

1. Fetch current source refs.

   ```bash
   git fetch upstream develop --prune
   git fetch origin --prune
   ```

2. Create a new worktree from the upstream base.

   ```bash
   git worktree add -b codex/gds-<base-version> \
     /path/to/port-worktree upstream/develop
   ```

3. Confirm the upstream version and base commit.

   ```bash
   git rev-parse HEAD
   rg -n 'AssemblyVersion|VersionPrefix|0\.9\.' \
     Kavita.Common Kavita.Server Kavita.API
   ```

4. Reapply GDS commits from the previous maintained fork branch, oldest first.

   Prefer `git cherry-pick` for commits that still map cleanly. For conflicts,
   keep upstream behavior outside GDS-specific code paths and preserve GDS
   behavior only where the local regression matrix requires it.

5. If upstream already contains an equivalent fix, drop the redundant fork patch
   and record that decision in the local audit.

6. Summarize the resulting patch stack.

   ```bash
   scripts/gds_patch_stack_summary.sh upstream/develop
   ```

7. Run the helper self-test before relying on the porting helpers.

   ```bash
   scripts/gds_porting_selftest.sh upstream/develop
   ```

   The self-test checks helper syntax, public-doc privacy scanning, patch export,
   patch application into a temporary worktree, same-base tree equality, and
   temporary artifact cleanup.

8. Export the patch stack when you need a portable handoff or a rollback point.

   ```bash
   scripts/gds_export_patch_stack.sh upstream/develop /tmp/kavita-gds-patch-stack
   ```

   The export contains numbered `git format-patch` files, `MANIFEST.txt`, and
   `SHA256SUMS`. The manifest records the source base commit and source tree
   hash so a same-base apply smoke can prove the applied tree matches the
   original patch stack. Do not commit generated patch exports unless
   intentionally preparing release assets or an issue reproduction bundle.

9. Apply an exported stack to a fresh upstream worktree when preparing the next
   base port.

   ```bash
   scripts/gds_apply_patch_stack.sh \
     /tmp/kavita-gds-patch-stack \
     /tmp/kavita-gds-next-port \
     upstream/develop \
     codex/gds-next-port
   ```

   If `git am` stops on conflicts, resolve them in the target worktree and
   continue with `git am --continue`, or abort with `git am --abort` and record
   the conflict area in the local audit. When the export and target use the same
   base commit, the helper also checks final tree equality against the manifest.

10. Run source validation before Docker packaging.

   ```bash
   dotnet build Kavita.sln --no-restore -maxcpucount:1 /p:UseSharedCompilation=false
   dotnet test Kavita.Services.Tests/Kavita.Services.Tests.csproj \
     --no-build --filter 'FullyQualifiedName~GDS|FullyQualifiedName~Cover|FullyQualifiedName~Scanner'
   ```

11. Build packages once per RID from the same source tree.

   ```bash
   bash build.sh linux-x64
   bash build.sh linux-arm64
   bash build.sh linux-arm
   ```

12. Use one Dockerfile/build context that selects the correct RID output by
   `TARGETPLATFORM`. Do not fork the Dockerfile per architecture unless it is
   strictly a packaging fix and does not change application source behavior.

13. Validate in stages:

    - source build and focused service tests
    - amd64 fresh startup smoke
    - production DB clone reader/API smoke
    - copied original-layout fixture scan
    - ARM64 runtime smoke
    - ARMv7 runtime smoke, only if armv7 will be published
    - production targeted validation

14. Publish only after the local regression matrix has `FAIL=0` and every
    published platform has a successful runtime smoke.

## Conflict Policy

- Prefer upstream code when behavior is not GDS-specific.
- Preserve GDS `LibraryType` handling, GDS scanner grouping, copied-fixture
  path assumptions, reader/cache tolerance, and cover fallback behavior when
  they are covered by the regression matrix.
- Do not flatten copied fixtures. GDS behavior can depend on original relative
  folder layout and same-folder sidecars.
- If a conflict affects scan, reader, cache, cover, SQLite, startup, health, or
  operational stability, validate it in the current candidate. Do not defer a
  code-fixable regression to a later release.
- Keep exact sample titles, paths, raw IDs, and private operational details out
  of public docs. Store those only in local audit and regression matrix files.

## Validation Gate

The release gate is issue-class based, not sample-count based. Every issue class
in the local regression matrix must be classified for the candidate as one of:

- `PASS`
- `SOURCE/DB DEBT`
- `FUTURE POLICY`
- `NOT RETESTED WITH REASON`
- `FAIL`

`FAIL` blocks release and production rollout. `NOT RETESTED WITH REASON` must
include why it was skipped and the concrete condition for retest.

## ARM Publish Rule

Do not add an architecture to the version tag or `latest` manifest unless that
exact architecture image has reached `/api/health`.

If qemu on the media host is too slow or creates user-facing load, use a lower
load window, native ARM hardware, or an off-host Docker environment. A built
image alone is not enough evidence to publish that platform.

## Current 0.9.0.10 Notes

The `0.9.0.10` port was built by creating an upstream-based worktree and
reapplying the existing GDS patch stack. The amd64 source/build/runtime and
copied-fixture gates passed. ARM64 runtime smoke remains inconclusive until a
stable qemu or native ARM validation path reaches `/api/health`.

See `docs/gds-0.9.0.10-validation.md` for the public-safe validation summary.
