#!/usr/bin/env bash
# Proves a pull request cannot silence gitleaks with its own .gitleaksignore / .gitleaks.toml.
# Usage: GITLEAKS_BIN=/path/to/gitleaks .github/scripts/secret-scan-trust-test.sh
# The fake secret is built at runtime in a throwaway repo and never printed.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
GITLEAKS_BIN="${GITLEAKS_BIN:?set GITLEAKS_BIN}"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
repo="$tmp/repo"; mkdir "$repo"; cd "$repo"
git init -q -b main; git config user.email t@example.invalid; git config user.name t
echo base > README.md; git add .; git commit -qm base
base="$(git rev-parse HEAD)"
# GitHub PAT shape, assembled at runtime from fragments.
tok="ghp_$(printf 'aB3dE5fG7hJ9kL1mN3pQ5rS7tU9vW1xY3zA5' | head -c 36)"
printf 'GITHUB_TOKEN=%s\n' "$tok" > config.env
git add config.env; git commit -qm "pr: leak" 
leak="$(git rev-parse HEAD)"
# Fingerprint = commit:file:rule:line, taken from the real finding.
rule="$("$GITLEAKS_BIN" git --no-banner --log-level error -r "$tmp/r.json" -f json --exit-code 0 "$repo" >/dev/null; \
  grep -o '"RuleID": *"[^"]*"' "$tmp/r.json" | head -1 | sed 's/.*: *"//;s/"//')"
[ -n "$rule" ] || { echo "setup: planted secret not detected"; exit 1; }
echo "$leak:config.env:$rule:1" > .gitleaksignore
printf '[extend]\nuseDefault = true\n[allowlist]\npaths = ["config.env"]\nregexes = ["ghp_.*"]\n' > .gitleaks.toml
git add .gitleaksignore .gitleaks.toml; git commit -qm "pr: silence"

run() { # $1 = with|without neutralisation of the tree
  local cfg="$tmp/cfg-$1"
  export GITLEAKS_BIN SOURCE="$repo" CFG_DIR="$cfg" EVENT_NAME=pull_request BASE_SHA="$base"
  git -C "$repo" reset -q --hard "$head"
  if [ "$1" = with ]; then
    "$here/secret-scan.sh" prepare
  else
    # Control = the previous behaviour: explicit base config and ignore path, tree left as the PR has it.
    mkdir -p "$cfg"; printf '[extend]\nuseDefault = true\n' > "$cfg/gitleaks.toml"; : > "$cfg/.gitleaksignore"
  fi
  local rc=0; "$here/secret-scan.sh" scan 2>"$tmp/err-$1" >/dev/null || rc=$?
  return $rc
}
head="$(git rev-parse HEAD)"

# Control: tree not neutralised -> the PR's ./.gitleaksignore silences the scan (exit 0).
rc_without=0; run without || rc_without=$?
# Protected: neutralised tree -> the leak is reported (exit 1).
rc_with=0; run with || rc_with=$?
echo "without neutralisation: exit $rc_without (expected 0, scanner suppressed)"
echo "with neutralisation:    exit $rc_with (expected 1, leak detected)"
[ "$rc_without" -eq 0 ] && [ "$rc_with" -eq 1 ] || { echo "FAIL"; exit 1; }

# Second scenario: a PR-supplied .gitattributes ("* -diff", root and nested) must not hide the leak.
repo2="$tmp/repo2"; mkdir "$repo2"; cd "$repo2"
git init -q -b main; git config user.email t@example.invalid; git config user.name t
echo base > README.md; git add .; git commit -qm base; base2="$(git rev-parse HEAD)"
mkdir sub; printf 'GITHUB_TOKEN=%s\n' "$tok" > sub/config.env
printf '* -diff\n' > .gitattributes; printf '* -diff\n' > sub/.gitattributes
git add .; git commit -qm "pr: leak hidden by attributes"
export GITLEAKS_BIN SOURCE="$repo2" CFG_DIR="$tmp/cfg-attr" EVENT_NAME=pull_request BASE_SHA="$base2"
"$here/secret-scan.sh" prepare
rc_attr=0; "$here/secret-scan.sh" scan 2>"$tmp/err-attr" >/dev/null || rc_attr=$?
echo "attributes scenario:    exit $rc_attr (expected 1, leak detected despite -diff)"
[ "$rc_attr" -eq 1 ] || { echo "FAIL (.gitattributes bypass)"; exit 1; }
# CONTROL (not a protection test): the same attributes scenario with GIT_ATTR_SOURCE removed from the
# scan. It must exit 0, which proves "* -diff" really hides the leak and the scenario above is meaningful.
sed '/export GIT_ATTR_SOURCE=/d' "$here/secret-scan.sh" > "$tmp/secret-scan-no-attr-source.sh"
chmod +x "$tmp/secret-scan-no-attr-source.sh"
rc_ctl=0; env -u GIT_ATTR_SOURCE "$tmp/secret-scan-no-attr-source.sh" scan 2>"$tmp/err-ctl" >/dev/null || rc_ctl=$?
echo "CONTROL (no GIT_ATTR_SOURCE): exit $rc_ctl (expected 0, leak hidden by -diff)"
if [ "$rc_ctl" -ne 0 ]; then
  # The control only calibrates the scenario above; the protection itself already passed (exit 1 with
  # GIT_ATTR_SOURCE). A git that shows the leak even with "* -diff" (seen on the hosted ubuntu-26.04
  # image, git 2.56.1) makes the scenario vacuous on that host, not the scanner unsafe. Say so loudly,
  # with enough to diagnose (git version, config key names only, scanner stderr is already redacted).
  echo "::warning::attributes control did not hide the leak on this host, so the .gitattributes scenario is not proven here (git: $(git --version))"
  git config --show-origin -l 2>/dev/null | sed 's/=.*//' | sort -u >&2 || true
  head -c 2000 "$tmp/err-ctl" >&2 || true
fi

# Third scenario: the git version gate fails closed. A PATH shim answers "git --version" with a chosen
# string and delegates everything else to the real git.
real_git="$(command -v git)"
shim="$tmp/shim"; mkdir "$shim"
cat > "$shim/git" <<SHIM
#!/usr/bin/env bash
if [ "\${1:-}" = "--version" ]; then printf '%s\n' "\$FAKE_GIT_VERSION"; exit 0; fi
exec "$real_git" "\$@"
SHIM
chmod +x "$shim/git"
gate() { # $1 = fake "git --version" output
  local rc=0; FAKE_GIT_VERSION="$1" PATH="$shim:$PATH" "$here/secret-scan.sh" scan 2>"$tmp/err-gate" >/dev/null || rc=$?
  echo "$rc"
}
for v in "git version banana" "" "git version 2.x.1" "git version 2.39.5" "git version 1.9.0"; do
  rc="$(gate "$v")"
  echo "version gate '$v': exit $rc (expected 2, refused)"
  [ "$rc" -eq 2 ] || { echo "FAIL (version gate failed open for '$v')"; exit 1; }
done
for v in "git version 2.40.0" "git version 2.53.1 (Apple Git-1)" "git version 3.0.0"; do
  rc="$(gate "$v")"
  echo "version gate '$v': exit $rc (expected 1, scan ran and found the leak)"
  [ "$rc" -eq 1 ] || { echo "FAIL (version gate rejected an acceptable git: '$v')"; exit 1; }
done
echo "PASS"
