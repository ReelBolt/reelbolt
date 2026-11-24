#!/usr/bin/env bash
# gitleaks trust-boundary helper, used by .github/workflows/secret-scan.yml and by
# secret-scan-trust-test.sh.
#
#   secret-scan.sh prepare   make the checkout's silencing files trustworthy
#   secret-scan.sh scan      run the history scan from a neutral directory
#
# Env: GITLEAKS_BIN, SOURCE (checkout, default .), CFG_DIR (scratch dir),
#      EVENT_NAME, BASE_SHA (pull_request only).
#
# On a pull_request the tree is the contributor's. gitleaks auto-loads ./.gitleaksignore (and
# discovers .gitleaks.toml) from the scanned source, and --gitleaks-ignore-path does not stop
# that, so `prepare` overwrites both tree files with the base commit's copy, or deletes them
# when the base has none. Config comes only from the base ref; default rules if it has none.
set -euo pipefail

SOURCE="$(cd "${SOURCE:-.}" && pwd)"
: "${GITLEAKS_BIN:?}" "${CFG_DIR:?}"
EVENT_NAME="${EVENT_NAME:-push}"
mkdir -p "$CFG_DIR"

prepare() {
  if [ "$EVENT_NAME" = "pull_request" ]; then
    [ -n "${BASE_SHA:-}" ] || { echo "no base sha on a pull_request event" >&2; exit 1; }
    git -C "$SOURCE" cat-file -e "${BASE_SHA}^{commit}"
    src() { git -C "$SOURCE" show "${BASE_SHA}:$1" 2>/dev/null; }
  else
    src() { cat "$SOURCE/$1" 2>/dev/null; }
  fi
  if ! src .gitleaks.toml > "$CFG_DIR/gitleaks.toml"; then
    printf '[extend]\nuseDefault = true\n' > "$CFG_DIR/gitleaks.toml"
  fi
  src .gitleaksignore > "$CFG_DIR/.gitleaksignore" || : > "$CFG_DIR/.gitleaksignore"
  if [ "$EVENT_NAME" = "pull_request" ]; then
    # Neutralise the tree: base copy over the PR copy, or delete.
    for f in .gitleaksignore .gitleaks.toml; do
      if src "$f" > "$CFG_DIR/.base-$f"; then
        cp "$CFG_DIR/.base-$f" "$SOURCE/$f"
      else
        rm -f "$SOURCE/$f"
      fi
    done
  fi
}

scan() {
  # Neutral working directory: nothing of the PR is in cwd either.
  cd "$CFG_DIR"
  # A PR-controlled .gitattributes (for example "* -diff") makes "git log -p" show a leak as
  # binary, so gitleaks sees nothing. Read attributes from git's empty tree instead (git >= 2.40);
  # on an older git, refuse to scan rather than scan blind.
  # Fail closed: an unparseable "git --version", or any non-integer part, is a refusal, not a pass.
  local gv maj min
  gv="$(git --version 2>/dev/null || true)"
  if [[ "$gv" =~ ^git\ version\ ([0-9]+)\.([0-9]+) ]]; then
    maj="${BASH_REMATCH[1]}"; min="${BASH_REMATCH[2]}"
  else
    echo "cannot parse the git version (got: ${gv:-nothing}); git >= 2.40 is required for GIT_ATTR_SOURCE" >&2; exit 2
  fi
  if [ "$maj" -lt 2 ] || { [ "$maj" -eq 2 ] && [ "$min" -lt 40 ]; }; then
    echo "git >= 2.40 is required for GIT_ATTR_SOURCE; found $gv" >&2; exit 2
  fi
  export GIT_ATTR_SOURCE="$(git hash-object -t tree /dev/null)"
  "$GITLEAKS_BIN" git --redact --no-banner --exit-code 1 --log-level warn \
    --ignore-gitleaks-allow --gitleaks-ignore-path "$CFG_DIR" \
    --config "$CFG_DIR/gitleaks.toml" "$SOURCE"
}

case "${1:-}" in
  prepare) prepare ;;
  scan) scan ;;
  *) echo "usage: $0 prepare|scan" >&2; exit 2 ;;
esac
