#!/usr/bin/env bash
set -euo pipefail

base_ref="${1:-upstream/develop}"

git rev-parse --is-inside-work-tree >/dev/null

touched_files() {
  {
    git diff --name-only "${base_ref}..HEAD"
    git diff --name-only
    git diff --cached --name-only
    git ls-files --others --exclude-standard
  } | sort -u
}

echo "base_ref=${base_ref}"
echo "base_commit=$(git rev-parse "${base_ref}")"
echo "head_commit=$(git rev-parse HEAD)"
echo "branch=$(git branch --show-current)"
echo

echo "== Commits on top of base =="
git log --reverse --oneline "${base_ref}..HEAD"
echo

echo "== Changed file summary =="
git diff --stat "${base_ref}..HEAD"
echo

echo "== Untracked files =="
git ls-files --others --exclude-standard
echo

echo "== Worktree changes =="
git status --short
echo

echo "== GDS-sensitive files touched =="
touched_files | rg \
  '(^Kavita\.(API|Server|Services|Models|Database)|^UI/|^Dockerfile$|^build\.sh$|^copy_runtime\.sh$|^docs/|^scripts/)' \
  || true
echo

echo "== Public-doc privacy scan =="
private_patterns=(
  "$(printf '%s' '/mnt/')gds"
  "$(printf '%s' '/mnt/')gds2"
  "GDRIVE$(printf '%s' '/')READING"
  "library$(printf '%s' 'Id=')"
  "chapter$(printf '%s' 'Id=')"
  "series$(printf '%s' 'Id=')"
)
private_pattern="$(IFS='|'; echo "${private_patterns[*]}")"
doc_files="$(mktemp /tmp/gds-doc-files.XXXXXX)"
trap 'rm -f "${doc_files}"' EXIT
if touched_files | rg -i '(^docs/|README|CHANGELOG|RELEASE|BUILD|USAGE)' >"${doc_files}"; then
  if xargs -r rg -n "${private_pattern}" < "${doc_files}"; then
    echo "privacy_scan=FAIL"
    exit 1
  fi
fi
echo "privacy_scan=PASS"
