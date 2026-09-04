#!/usr/bin/env bash
set -euo pipefail

patch_dir="${1:-}"
target_worktree="${2:-}"
base_ref="${3:-upstream/develop}"
branch_name="${4:-}"

if [[ -z "${patch_dir}" || -z "${target_worktree}" ]]; then
  echo "Usage: $0 <patch-dir> <target-worktree> [base-ref] [branch-name]" >&2
  exit 2
fi

if [[ ! -d "${patch_dir}" ]]; then
  echo "Patch directory does not exist: ${patch_dir}" >&2
  exit 1
fi

mapfile -t patches < <(find "${patch_dir}" -maxdepth 1 -type f -name '*.patch' | sort)
if [[ "${#patches[@]}" -eq 0 ]]; then
  echo "No patch files found in ${patch_dir}" >&2
  exit 1
fi

git rev-parse --is-inside-work-tree >/dev/null
base_commit="$(git rev-parse "${base_ref}")"

manifest="${patch_dir}/MANIFEST.txt"
manifest_base_commit=""
manifest_head_tree=""
if [[ -f "${manifest}" ]]; then
  manifest_base_commit="$(awk -F= '$1 == "base_commit" {print $2; exit}' "${manifest}")"
  manifest_head_tree="$(awk -F= '$1 == "head_tree" {print $2; exit}' "${manifest}")"
fi

if [[ -e "${target_worktree}" ]]; then
  echo "Target worktree path already exists: ${target_worktree}" >&2
  exit 1
fi

if [[ -z "${branch_name}" ]]; then
  branch_name="gds-port-$(date -u +%Y%m%dT%H%M%SZ)"
fi

git worktree add -b "${branch_name}" "${target_worktree}" "${base_ref}"

(
  cd "${target_worktree}"
  echo "target_worktree=${target_worktree}"
  echo "base_ref=${base_ref}"
  echo "base_commit=${base_commit}"
  echo "branch=${branch_name}"
  echo "patch_count=${#patches[@]}"
  if ! git am "${patches[@]}"; then
    echo "Patch application failed. Resolve conflicts in ${target_worktree} or run: git -C ${target_worktree} am --abort" >&2
    exit 1
  fi
  echo "head_commit=$(git rev-parse HEAD)"
  echo "applied_patch_count=$(git rev-list --count "${base_ref}..HEAD")"
  if [[ -n "${manifest_head_tree}" && "${manifest_base_commit}" == "${base_commit}" ]]; then
    applied_tree="$(git rev-parse HEAD^{tree})"
    echo "manifest_head_tree=${manifest_head_tree}"
    echo "applied_head_tree=${applied_tree}"
    if [[ "${applied_tree}" != "${manifest_head_tree}" ]]; then
      echo "tree_match=FAIL" >&2
      exit 1
    fi
    echo "tree_match=PASS"
  elif [[ -n "${manifest_head_tree}" ]]; then
    echo "tree_match=SKIPPED different_base"
  fi
)
