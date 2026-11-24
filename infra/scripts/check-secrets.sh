#!/usr/bin/env bash
# Fails when infra/, the compose files or the workflows contain what looks like a literal
# secret (WP F8: "no secret lives in a manifest"). It is a tripwire,
# not a scanner: it catches the mistakes people actually make (a pasted key, a password in
# a connection string, a private key block) and is deliberately noisy-by-allowlist rather
# than quiet-by-omission.
#
#   infra/scripts/check-secrets.sh            scan the default paths
#   infra/scripts/check-secrets.sh path ...   scan given files or directories
#
# A line that is a reviewed false positive carries the marker `secret-scan: allow`.
# Placeholders are fine: ${VAR}, <angle>, var.x, CHANGE_ME, changeme, example, your-...
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

if [ "$#" -gt 0 ]; then
  paths=("$@")
else
  paths=(infra .github/workflows docker-compose.yml)
fi

# Files that legitimately contain long opaque strings.
exclude=(--exclude='check-secrets.sh')

# Well-known token shapes. Each is specific enough to have no sensible false positive.
shapes='AKIA[0-9A-Z]{16}'
shapes+='|ASIA[0-9A-Z]{16}'
shapes+='|-----BEGIN [A-Z ]*PRIVATE KEY-----'
shapes+='|gh[pousr]_[A-Za-z0-9]{36,}'
shapes+='|github_pat_[A-Za-z0-9_]{50,}'
shapes+='|sk-ant-[A-Za-z0-9_-]{20,}'
shapes+='|sk-[A-Za-z0-9]{32,}'
shapes+='|AIza[0-9A-Za-z_-]{35}'
shapes+='|xox[baprs]-[A-Za-z0-9-]{10,}'
shapes+='|glc_[A-Za-z0-9+/=]{30,}'

# scheme://user:password@host with a literal password.
urlpw='[a-z][a-z0-9+.-]*://[^/\s:@$<{]+:[^/\s@$<{}*]{4,}@'

# name = "literal" for names that hold secrets. The value must be 16+ opaque characters.
named='(password|passwd|secret|token|api[_-]?key|access[_-]?key|signing[_-]?key|private[_-]?key)[A-Za-z0-9_]*["'"'"']?[[:space:]]*[:=][[:space:]]*["'"'"']?[A-Za-z0-9+/_.=-]{16,}'

placeholder='\$\{|\$\(|<[a-z_ -]+>|var\.|local\.|module\.|secrets\.|vars\.|env\.|CHANGE_?ME|changeme|example|your-|xxxx|\*\*\*\*|REDACTED|placeholder|ci-schema-check'

found=0
scan() {
  local label=$1 pattern=$2 hits
  hits=$(grep -rInEi "${exclude[@]}" -e "$pattern" "${paths[@]}" 2>/dev/null \
    | grep -v 'secret-scan: allow' \
    | grep -ivE "$placeholder" || true)
  if [ -n "$hits" ]; then
    echo "::error::possible literal secret ($label):"
    printf '%s\n' "$hits" | sed 's/^/  /'
    found=1
  fi
}

scan "known token shape" "$shapes"
scan "credentials in a URL" "$urlpw"
scan "secret-named assignment" "$named"

if [ "$found" -ne 0 ]; then
  echo
  echo "Move the value to the secret store (docs/secrets.md) and reference it by name." >&2
  echo "If a hit is a reviewed false positive, append '# secret-scan: allow' to that line." >&2
  exit 1
fi
echo "check-secrets: no literal secrets found in ${paths[*]}"
