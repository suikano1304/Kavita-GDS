#!/usr/bin/env bash
set -euo pipefail

base_ref="${1:-upstream/develop}"
output_dir="${2:-}"

if [[ -z "${output_dir}" ]]; then
  safe_base="${base_ref//\//-}"
  output_dir="/tmp/kavita-gds-patch-stack-${safe_base}-$(date -u +%Y%m%dT%H%M%SZ)"
fi

git rev-parse --is-inside-work-tree >/dev/null

base_commit="$(git rev-parse "${base_ref}")"
head_commit="$(git rev-parse HEAD)"
head_tree="$(git rev-parse HEAD^{tree})"
branch_name="$(git branch --show-current)"
commit_count="$(git rev-list --count "${base_ref}..HEAD")"

if [[ "${commit_count}" == "0" ]]; then
  echo "No commits on top of ${base_ref}; nothing to export." >&2
  exit 1
fi

mkdir -p "${output_dir}"

manifest="${output_dir}/MANIFEST.txt"
checksums="${output_dir}/SHA256SUMS"

{
  echo "Kavita-GDS patch stack export"
  echo "created_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "base_ref=${base_ref}"
  echo "base_commit=${base_commit}"
  echo "head_commit=${head_commit}"
  echo "head_tree=${head_tree}"
  echo "branch=${branch_name}"
  echo "commit_count=${commit_count}"
  echo
  echo "commits_oldest_first:"
  git log --reverse --format='- %H %s' "${base_ref}..HEAD"
  echo
  echo "changed_files:"
  git diff --name-only "${base_ref}..HEAD" | sed 's/^/- /'
} > "${manifest}"

git format-patch --output-directory "${output_dir}" "${base_ref}..HEAD" >/dev/null

(
  cd "${output_dir}"
  sha256sum ./*.patch MANIFEST.txt > "${checksums}"
)

echo "output_dir=${output_dir}"
echo "manifest=${manifest}"
echo "checksums=${checksums}"
echo "patch_count=$(find "${output_dir}" -maxdepth 1 -name '*.patch' | wc -l)"
