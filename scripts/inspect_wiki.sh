#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   export GH_PAT="ghp_..."      # safer: export locally, don't paste into chat
#   ./scripts/inspect_wiki.sh [owner/repo]
#
# What it does:
# - Clones the repository wiki using GH_PAT (must be in GH_PAT env var)
# - Lists markdown files in the cloned wiki and in the repo's docs/
# - Shows files present in docs/ but missing in the wiki (possible causes of 404s)
# - Optionally prints a short summary of link-like targets found in docs/ (links to /docs/ or blob URLs)

REPO_ARG=${1-}
WORKDIR=$(pwd)

# GH_PAT is optional. If set we will use it for an authenticated HTTPS clone.
# Otherwise we will try SSH, then unauthenticated HTTPS.


# Determine repo (owner/repo) if not given
if [ -z "$REPO_ARG" ]; then
  # try to read from git remote
  REMOTE_URL=$(git config --get remote.origin.url || true)
  if [ -z "$REMOTE_URL" ]; then
    echo "Could not detect remote origin URL. Please pass owner/repo as argument." >&2
    echo "Usage: $0 owner/repo" >&2
    exit 2
  fi
  # Normalize: supports git@github.com:owner/repo.git and https://github.com/owner/repo.git
  if [[ "$REMOTE_URL" =~ ^git@github.com:(.+)/(.+)(\.git)?$ ]]; then
    OWNER=${BASH_REMATCH[1]}
    NAME=${BASH_REMATCH[2]}
    REPO="$OWNER/$NAME"
  elif [[ "$REMOTE_URL" =~ ^https://github.com/(.+)/(.+)(\.git)?$ ]]; then
    OWNER=${BASH_REMATCH[1]}
    NAME=${BASH_REMATCH[2]}
    REPO="$OWNER/$NAME"
  else
    echo "Unrecognized remote URL: $REMOTE_URL" >&2
    echo "Please pass owner/repo as argument." >&2
    exit 2
  fi
else
  REPO="$REPO_ARG"
fi

# Normalize repo string: remove any trailing .git so we get owner/repo
REPO="${REPO%.git}"

TMPDIR=$(mktemp -d)

echo "Repo: $REPO"
echo "Cloning wiki into $TMPDIR"

# Build candidate clone URLs. Prefer GH_PAT if provided.
declare -a CANDIDATES
if [ -n "${GH_PAT-}" ]; then
  CANDIDATES+=("https://x-access-token:${GH_PAT}@github.com/${REPO}.wiki.git")
else
  # Try SSH first (requires user's SSH keys), then unauthenticated HTTPS
  CANDIDATES+=("git@github.com:${REPO}.wiki.git" "https://github.com/${REPO}.wiki.git")
fi

CLONED=0
for url in "${CANDIDATES[@]}"; do
  echo "Trying: $url"
  if git clone --depth 1 "$url" "$TMPDIR" >/dev/null 2>&1; then
    echo "Cloned wiki from $url"
    CLONED=1
    break
  else
    echo "Failed to clone from $url"
  fi
done

if [ "$CLONED" -ne 1 ]; then
  echo "Warning: could not clone wiki (wiki may not be enabled or authentication failed)." >&2
  echo "Proceeding with empty wiki clone at $TMPDIR so we can still compare file lists." >&2
  mkdir -p "$TMPDIR"
fi

# Gather lists
pushd "$WORKDIR" >/dev/null
if [ -d "docs" ]; then
  echo "Listing docs/ markdown files..."
  find docs -type f -name '*.md' | sed 's|^docs/||' | sort > /tmp/docs_files.txt
else
  echo "No docs/ directory found in repo root." >&2
  rm -rf "$TMPDIR"
  popd >/dev/null
  exit 2
fi
popd >/dev/null

pushd "$TMPDIR" >/dev/null
echo "Listing wiki markdown files..."
find . -type f -name '*.md' | sed 's|^./||' | sort > /tmp/wiki_files.txt || true
popd >/dev/null

echo
echo "Files present in docs/ but NOT in wiki (relative paths):"
comm -23 /tmp/docs_files.txt /tmp/wiki_files.txt || true

echo
echo "Files present in wiki but not in docs/ (relative paths):"
comm -13 /tmp/docs_files.txt /tmp/wiki_files.txt || true

# Quick scan: find links in docs that look like they target docs/ or blob URLs
echo
echo "Scanning docs/ for links referencing docs/ or GitHub blob URLs (possible problematic links):"
grep -RIn --line-number --only-matching -E "\((/docs/[^)]+|docs/[^)]+|https?://[^)]+/blob/[^)]+)\)" docs || true

# Cleanup note
echo
echo "Done. Wiki clone is at: $TMPDIR (not removed so you can inspect it)."
echo "If you want the script to remove it automatically, rerun with CLEAN=1 environment var."
if [ "${CLEAN-0}" = "1" ]; then
  rm -rf "$TMPDIR"
  echo "Removed $TMPDIR"
fi

# Exit with success
exit 0
