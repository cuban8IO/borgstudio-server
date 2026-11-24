#!/usr/bin/env bash
# Adds a release to CHANGELOG.md, right above the newest one.
#
# Usage: update-changelog.sh <version> <notes-file> [changelog-file]
#   The notes file is what next-release.sh writes ("### New features" / "### Fixes" with "- Title (#12)").
#   The date is today's (UTC).
set -euo pipefail

version=${1:?version missing}
notes_file=${2:?notes file missing}
changelog=${3:-CHANGELOG.md}

if grep -q "^## \[$version\]" "$changelog"; then
  echo "$changelog already lists $version." >&2
  exit 0
fi

entry=$(mktemp)
trap 'rm -f "$entry"' EXIT
{
  printf '## [%s] - %s\n\n' "$version" "$(date -u +%Y-%m-%d)"
  # Notes without trailing blank lines; the blank line before the next release follows below.
  sed -e :a -e '/^\n*$/{$d;N;ba' -e '}' "$notes_file"
} > "$entry"

# Insert before the first release heading, or append when there is none yet.
if grep -q '^## \[' "$changelog"; then
  awk -v entry="$entry" '
    !inserted && /^## \[/ { while ((getline line < entry) > 0) print line; print ""; inserted = 1 }
    { print }
  ' "$changelog" > "$changelog.new"
else
  { cat "$changelog"; printf '\n'; cat "$entry"; } > "$changelog.new"
fi
mv "$changelog.new" "$changelog"
echo "Added $version to $changelog."
