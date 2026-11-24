#!/usr/bin/env bash
# Tears down an isolated e2e stack started by up-isolated.sh: its containers, its network and its
# volumes (down -v), nothing else. Refuses the developer's default project names.
#
#   E2E_PROJECT=reelbolt-myinit e2e/scripts/down-isolated.sh [--keep-env]
#
# --keep-env  keep e2e/.stack/<project>/ (secrets, garage.toml, users.json); by default it is removed.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
E2E_PROJECT="${E2E_PROJECT-reelbolt-e2e}"
STACK_DIR="$REPO_ROOT/e2e/.stack/$E2E_PROJECT"

KEEP_ENV=0
for arg in "$@"; do
  case "$arg" in
    --keep-env) KEEP_ENV=1 ;;
    -h | --help)
      sed -n '2,8p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown argument '$arg'." >&2
      exit 2
      ;;
  esac
done

refuse_project() {
  local name="$1" lower main_root forbidden
  lower="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"
  if [ -z "$lower" ]; then
    echo "E2E_PROJECT is empty; refusing (an empty project name means the default stack)." >&2
    exit 2
  fi
  if ! printf '%s' "$lower" | grep -Eq '^[a-z0-9][a-z0-9_-]*$'; then
    echo "E2E_PROJECT='$name' is not a valid compose project name ([a-z0-9][a-z0-9_-]*)." >&2
    exit 2
  fi
  main_root="$(git -C "$REPO_ROOT" rev-parse --path-format=absolute --git-common-dir 2>/dev/null | sed 's#/\.git$##' || true)"
  for forbidden in reelforge reelbolt "$(basename "$REPO_ROOT")" "${main_root:+$(basename "$main_root")}"; do
    forbidden="$(printf '%s' "$forbidden" | tr '[:upper:]' '[:lower:]')"
    if [ -n "$forbidden" ] && [ "$lower" = "$forbidden" ]; then
      echo "E2E_PROJECT='$name' would address the developer's default stack; refusing." >&2
      exit 2
    fi
  done
}
refuse_project "$E2E_PROJECT"

if [ ! -f "$STACK_DIR/.env" ]; then
  echo "No $STACK_DIR/.env: $E2E_PROJECT was not started by up-isolated.sh from this checkout; nothing to do." >&2
  exit 1
fi

# The opt-in profiles are named so their containers are included (`stripe` is the Stripe fake of
# `up-isolated.sh --provider stripe`); e2e-forbidden never is (nothing in it is ever started).
docker compose -p "$E2E_PROJECT" \
  --project-directory "$REPO_ROOT" \
  --env-file "$STACK_DIR/.env" \
  -f "$REPO_ROOT/docker-compose.yml" \
  -f "$REPO_ROOT/docker-compose.e2e.yml" \
  --profile e2e-site --profile e2e-engine --profile stripe \
  down -v --remove-orphans

if [ "$KEEP_ENV" = "0" ]; then
  rm -rf "$STACK_DIR"
  echo "Removed $STACK_DIR."
fi
