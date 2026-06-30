#!/usr/bin/env bash
set -euo pipefail

REPO_NAME="${REPO_NAME:-Kavita-GDS}"
VISIBILITY="${VISIBILITY:-public}"
TAG="${TAG:-v9.0.7-1}"
TITLE="${TITLE:-Kavita GDS ${TAG}}"
ASSET="${ASSET:-}"
NOTES_FILE="${NOTES_FILE:-RELEASE_NOTES.md}"

if [[ -z "${GITHUB_TOKEN:-}" ]]; then
  echo "GITHUB_TOKEN is required." >&2
  exit 1
fi

if [[ -n "$ASSET" && ! -f "$ASSET" ]]; then
  echo "Asset not found: $ASSET" >&2
  exit 1
fi

api() {
  curl -fsS \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -H "Accept: application/vnd.github+json" \
    -H "X-GitHub-Api-Version: 2022-11-28" \
    "$@"
}

OWNER="$(api https://api.github.com/user | sed -n 's/.*"login"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)"
if [[ -z "$OWNER" ]]; then
  echo "Could not resolve GitHub user from token." >&2
  exit 1
fi

FULL_NAME="$OWNER/$REPO_NAME"

if ! api "https://api.github.com/repos/$FULL_NAME" >/dev/null 2>&1; then
  private=false
  if [[ "$VISIBILITY" == "private" ]]; then
    private=true
  fi

  api -X POST https://api.github.com/user/repos \
    -d "{\"name\":\"$REPO_NAME\",\"private\":$private,\"description\":\"Kavita GDS multi-arch image\"}" \
    >/dev/null
fi

if [[ ! -d .git ]]; then
  git init
  git branch -M main
fi

git config user.name "${GIT_AUTHOR_NAME:-$OWNER}"
git config user.email "${GIT_AUTHOR_EMAIL:-$OWNER@users.noreply.github.com}"

if ! git remote get-url origin >/dev/null 2>&1; then
  git remote add origin "https://github.com/$FULL_NAME.git"
fi

git add README.md RELEASE_NOTES.md .gitignore scripts/create_github_release.sh
if ! git diff --cached --quiet; then
  git commit -m "Add release documentation"
fi

git push "https://x-access-token:${GITHUB_TOKEN}@github.com/$FULL_NAME.git" main

notes_json="$(sed 's/\\/\\\\/g; s/"/\\"/g; s/$/\\n/' "$NOTES_FILE" | tr -d '\n')"
release_json="$(api -X POST "https://api.github.com/repos/$FULL_NAME/releases" \
  -d "{\"tag_name\":\"$TAG\",\"target_commitish\":\"main\",\"name\":\"$TITLE\",\"body\":\"$notes_json\",\"draft\":false,\"prerelease\":false}")"

if [[ -n "$ASSET" ]]; then
  upload_url="$(printf '%s' "$release_json" | sed -n 's/.*"upload_url"[[:space:]]*:[[:space:]]*"\([^"]*\){?name,label}".*/\1/p')"
  if [[ -z "$upload_url" ]]; then
    echo "Could not resolve release upload URL. Release may already exist." >&2
    exit 1
  fi

  asset_name="$(basename "$ASSET")"
  curl -fsS \
    -X POST \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -H "Accept: application/vnd.github+json" \
    -H "X-GitHub-Api-Version: 2022-11-28" \
    -H "Content-Type: application/gzip" \
    --data-binary @"$ASSET" \
    "$upload_url?name=$asset_name" \
    >/dev/null
fi

echo "Created release:"
echo "https://github.com/$FULL_NAME/releases/tag/$TAG"
