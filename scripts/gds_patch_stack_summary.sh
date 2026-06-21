#!/usr/bin/env bash
set -euo pipefail

base_ref="${1:-upstream/develop}"

git rev-parse --is-inside-work-tree >/dev/null

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

echo "== GDS-sensitive files touched =="
{
  git diff --name-only "${base_ref}..HEAD"
  git ls-files --others --exclude-standard
} | sort -u | rg \
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
if {
  git diff --name-only "${base_ref}..HEAD"
  git ls-files --others --exclude-standard
} | sort -u | rg '(^docs/|README|CHANGELOG|RELEASE|BUILD|USAGE)' >/tmp/gds-doc-files.$$; then
  if xargs -r rg -n "${private_pattern}" < /tmp/gds-doc-files.$$; then
    rm -f /tmp/gds-doc-files.$$
    echo "privacy_scan=FAIL"
    exit 1
  fi
fi
rm -f /tmp/gds-doc-files.$$
echo "privacy_scan=PASS"
