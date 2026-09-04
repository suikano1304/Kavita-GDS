#!/usr/bin/env bash
set -euo pipefail

current_base_ref="${1:-}"
target_base_ref="${2:-}"
target_worktree="${3:-}"
branch_name="${4:-}"
patch_dir="${5:-}"

if [[ -z "${current_base_ref}" || -z "${target_base_ref}" || -z "${target_worktree}" ]]; then
  echo "Usage: $0 <current-base-ref> <target-base-ref> <target-worktree> [branch-name] [patch-dir]" >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"

cd "${repo_root}"
git rev-parse --is-inside-work-tree >/dev/null

if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "Current worktree has uncommitted changes; commit or stash before exporting the patch stack." >&2
  exit 1
fi

if [[ -e "${target_worktree}" ]]; then
  echo "Target worktree path already exists: ${target_worktree}" >&2
  exit 1
fi

current_base_commit="$(git rev-parse "${current_base_ref}")"
target_base_commit="$(git rev-parse "${target_base_ref}")"
head_commit="$(git rev-parse HEAD)"

if [[ -z "${branch_name}" ]]; then
  safe_target="${target_base_ref//\//-}"
  branch_name="codex/gds-port-${safe_target}-$(date -u +%Y%m%dT%H%M%SZ)"
fi

if [[ -z "${patch_dir}" ]]; then
  safe_current="${current_base_ref//\//-}"
  safe_target="${target_base_ref//\//-}"
  patch_dir="${TMPDIR:-/tmp}/kavita-gds-patch-stack-${safe_current}-to-${safe_target}-$(date -u +%Y%m%dT%H%M%SZ)"
fi

echo "repo_root=${repo_root}"
echo "current_base_ref=${current_base_ref}"
echo "current_base_commit=${current_base_commit}"
echo "source_head_commit=${head_commit}"
echo "target_base_ref=${target_base_ref}"
echo "target_base_commit=${target_base_commit}"
echo "target_worktree=${target_worktree}"
echo "target_branch=${branch_name}"
echo "patch_dir=${patch_dir}"

echo "== summary =="
"${script_dir}/gds_patch_stack_summary.sh" "${current_base_ref}" | tail -18

echo "== export =="
"${script_dir}/gds_export_patch_stack.sh" "${current_base_ref}" "${patch_dir}"

echo "== apply =="
"${script_dir}/gds_apply_patch_stack.sh" "${patch_dir}" "${target_worktree}" "${target_base_ref}" "${branch_name}"

echo "== next =="
echo "Inspect ${target_worktree}, resolve any follow-up conflicts or version-specific fixes, then run:"
echo "  cd ${target_worktree}"
echo "  scripts/gds_porting_selftest.sh ${target_base_ref}"
