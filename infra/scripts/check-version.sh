#!/usr/bin/env bash
# One version string, enforced. The repo-root VERSION file is the single source of truth;
# every other version-bearing surface carries that literal, and a surface that has drifted
# is a failure, not a warning. This exists so a release cannot ship the site at one version,
# the API at another and the docs at a third.
#
#   infra/scripts/check-version.sh                 check this repository
#   CHECK_VERSION_ROOT=/scratch check-version.sh   check another tree (its self-test)
#
# Exit 0 = every surface carries the VERSION literal; exit 1 = drift. On drift it prints one
# `drift: <path>` line per offending surface and `version ok: <version>` when clean.
set -uo pipefail

root="${CHECK_VERSION_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

version_file="$root/VERSION"
if [ ! -f "$version_file" ]; then
  echo "drift: VERSION (missing $version_file)"
  echo "check-version: no VERSION file to compare against" >&2
  exit 1
fi

# The file holds one non-empty line. Anything else is drift in the source of truth itself.
nonempty="$(grep -c '[^[:space:]]' "$version_file" 2>/dev/null || true)"
if [ "${nonempty:-0}" -ne 1 ]; then
  echo "drift: VERSION (expected exactly one non-empty line, found ${nonempty:-0})"
  exit 1
fi
version="$(sed -n '1p' "$version_file" | tr -d '[:space:]')"
if [ -z "$version" ]; then
  echo "drift: VERSION (empty)"
  exit 1
fi

# Every surface that must carry the literal. The four package.json files, the two frontend
# version modules, the Go default, the built image's ARG, the mail footer and the docs strings.
surfaces=(
  site/package.json
  web/package.json
  docs-site/package.json
  mcp/package.json
  site/lib/site-config.ts
  web/lib/version.ts
  api/config/config.go
  api/main.go
  api/Dockerfile
  api/services/mail_footer.go
  docs-site/i18n-strings.js
  mcp/src/bridge.ts
)

# A surface must carry the literal as a whole version token, not as a prefix of a longer one:
# `0.1.0-alpha-x` or `0.1.0-alpha.1` is drift, so the match is anchored on the character after
# it. Only `.` is regex-special in a semver string, so escaping it is enough.
escaped="${version//./\\.}"
pattern="${escaped}([^0-9A-Za-z.-]|$)"

drift=0
for path in "${surfaces[@]}"; do
  file="$root/$path"
  if [ ! -f "$file" ]; then
    echo "drift: $path (missing)"
    drift=1
  elif ! grep -qE -- "$pattern" "$file"; then
    echo "drift: $path"
    drift=1
  fi
done

if [ "$drift" -ne 0 ]; then
  echo "check-version: surfaces above do not carry $version (source: VERSION)" >&2
  exit 1
fi

echo "version ok: $version"
