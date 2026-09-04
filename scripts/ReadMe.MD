
# Scripts

This directory contains useful scripts used for development or debugging user issues.

## GDS upstream porting helpers

The GDS porting helpers support the fork workflow documented in
`../docs/gds-upstream-porting.md`: start from an official Kavita upstream base,
then reapply the GDS patch stack as one source stack.

- `gds_patch_stack_summary.sh <base-ref>`: prints the commits and files changed
  on top of an upstream base, then runs the public-doc privacy scan.
- `gds_export_patch_stack.sh <base-ref> [output-dir]`: exports the current stack
  as numbered `git format-patch` files with `MANIFEST.txt` and `SHA256SUMS`.
- `gds_apply_patch_stack.sh <patch-dir> <target-worktree> [base-ref] [branch]`:
  creates a fresh worktree from the base and applies an exported stack.
- `gds_prepare_next_port.sh <current-base-ref> <target-base-ref> <target-worktree>
  [branch] [patch-dir]`: exports the current committed stack and applies it to
  a fresh worktree from the next upstream base.
- `gds_porting_selftest.sh [base-ref]`: runs syntax checks, the summary/privacy
  scan, export, apply, same-base tree equality, and temporary cleanup.

Run the self-test before relying on the helpers for a new upstream port:

```bash
scripts/gds_porting_selftest.sh upstream/develop
```
