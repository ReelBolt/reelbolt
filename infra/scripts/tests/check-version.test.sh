#!/usr/bin/env bash
# Tests for infra/scripts/check-version.sh. The point of the check is that it *fails* on
# drift, so this asserts both directions: a consistent tree exits 0 and prints the version,
# and a tree where exactly one surface has been moved off the literal exits 1 naming that
# file (Review Focus 3 — a check that has only ever been seen to pass is not a check).
#
# It runs against a scratch copy of the real version-bearing surfaces, addressed through
# CHECK_VERSION_ROOT, so the assertions never mutate the working tree and still track the
# real files (if a real surface loses the literal, the "clean" case here fails too).
#
#   infra/scripts/tests/check-version.test.sh
set -euo pipefail

tests_dir="$(cd "$(dirname "$0")" && pwd)"
scripts_dir="$(cd "$tests_dir/.." && pwd)"
repo_root="$(cd "$scripts_dir/../.." && pwd)"
script="$scripts_dir/check-version.sh"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

passed=0
failed=0
ok() { printf '  ok   %s\n' "$1"; passed=$((passed + 1)); }
ko() {
  printf '  FAIL %s\n' "$1"
  if [ $# -gt 1 ]; then printf '%s\n' "$2" | sed 's/^/       /'; fi
  failed=$((failed + 1))
}

# The same list check-version.sh asserts. Kept here so the scratch tree is a faithful copy.
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

scratch="$work/tree"
mkdir -p "$scratch"
cp "$repo_root/VERSION" "$scratch/VERSION"
for path in "${surfaces[@]}"; do
  mkdir -p "$scratch/$(dirname "$path")"
  cp "$repo_root/$path" "$scratch/$path"
done

out=""
status=0
run_check() { # ROOT
  set +e
  out="$(CHECK_VERSION_ROOT="$1" bash "$script" 2>&1)"
  status=$?
  set -e
}

# ── 1. a consistent tree ─────────────────────────────────────────────────
echo "1. a consistent tree"
run_check "$scratch"
if [ "$status" -eq 0 ]; then ok "clean tree exits 0"; else ko "clean tree exits 0 (got $status)" "$out"; fi
if printf '%s' "$out" | grep -qF "version ok: $(cat "$scratch/VERSION")"; then
  ok "clean tree prints version ok"
else
  ko "clean tree prints version ok" "$out"
fi

# ── 2. exactly one surface drifts ────────────────────────────────────────
echo "2. one surface edited off the literal (Review Focus 3)"
drifted="api/config/config.go"
ver_re="$(sed -n 1p "$scratch/VERSION" | tr -d '[:space:]' | sed 's/\./\\./g')"
sed -i "s/${ver_re}/&-x/" "$scratch/$drifted"
run_check "$scratch"
if [ "$status" -eq 1 ]; then ok "drifted tree exits 1"; else ko "drifted tree exits 1 (got $status)" "$out"; fi
if printf '%s' "$out" | grep -qF "drift: $drifted"; then
  ok "drifted tree names the file"
else
  ko "drifted tree names the file" "$out"
fi
if [ "$(printf '%s\n' "$out" | grep -c '^drift: ')" -eq 1 ]; then
  ok "exactly one drift line"
else
  ko "exactly one drift line" "$out"
fi
cp "$repo_root/$drifted" "$scratch/$drifted"
run_check "$scratch"
if [ "$status" -eq 0 ]; then ok "restored tree exits 0"; else ko "restored tree exits 0 (got $status)" "$out"; fi

# ── 3. a missing surface ─────────────────────────────────────────────────
echo "3. a missing surface"
rm "$scratch/web/lib/version.ts"
run_check "$scratch"
if [ "$status" -eq 1 ]; then ok "missing surface exits 1"; else ko "missing surface exits 1 (got $status)" "$out"; fi
if printf '%s' "$out" | grep -qF "drift: web/lib/version.ts"; then
  ok "missing surface names the file"
else
  ko "missing surface names the file" "$out"
fi
cp "$repo_root/web/lib/version.ts" "$scratch/web/lib/version.ts"

# ── 4. a missing VERSION ─────────────────────────────────────────────────
echo "4. a missing VERSION"
rm "$scratch/VERSION"
run_check "$scratch"
if [ "$status" -eq 1 ]; then ok "missing VERSION exits 1"; else ko "missing VERSION exits 1 (got $status)" "$out"; fi

printf '\n%d passed, %d failed\n' "$passed" "$failed"
[ "$failed" -eq 0 ] || exit 1
