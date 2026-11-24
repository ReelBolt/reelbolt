#!/usr/bin/env bash
# Runs the Postgres-gated tests (Trait Category=Postgres) against a throwaway postgres:16-alpine
# with max_connections=200 on a free loopback port, then stops the container whatever happens.
#
#   inference/tests/run-postgres-tests.sh [extra dotnet test args...]
#
# Exits with dotnet test's exit code. Optional overrides (for proof runs and parallel worktrees):
#   REELBOLT_PGTEST_NAME             container name        (default reelbolt-pgtest-<pid>)
#   REELBOLT_PGTEST_MAX_CONNECTIONS  server max_connections (default 200)
#   REELBOLT_PGTEST_FILTER           extra test filter, AND-ed with the category: the run uses
#                                    --filter "Category=Postgres&(<value>)", e.g.
#                                    REELBOLT_PGTEST_FILTER='FullyQualifiedName~ExecutionResumeRoute'.
#                                    Unset or empty: --filter "Category=Postgres", exactly as before.
#                                    (A --filter among the extra args would replace the category filter,
#                                    so it is not the way to narrow a run.)
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$script_dir/ReelBolt.WorkflowEngine.Tests/ReelBolt.WorkflowEngine.Tests.csproj"
name="${REELBOLT_PGTEST_NAME:-reelbolt-pgtest-$$}"
max_connections="${REELBOLT_PGTEST_MAX_CONNECTIONS:-200}"
password="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
started=0

cleanup() {
  if [[ "$started" == 1 ]]; then
    local rejected
    rejected="$(docker logs "$name" 2>&1 | grep -c 'too many clients' || true)"
    echo "run-postgres-tests: server logged ${rejected} 'too many clients' rejection(s); stopping ${name}" >&2
    docker stop "$name" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

# Docker binds a free ephemeral loopback port (127.0.0.1::5432) and reports which one it took.
docker run -d --rm --name "$name" \
  -e POSTGRES_PASSWORD="$password" \
  --tmpfs /var/lib/postgresql/data \
  -p 127.0.0.1::5432 \
  postgres:16-alpine -c max_connections="$max_connections" >/dev/null
started=1

port="$(docker port "$name" 5432/tcp | head -n1 | sed 's/.*://')"
if [[ -z "$port" ]]; then
  echo "run-postgres-tests: could not read the published port of ${name}" >&2
  exit 1
fi

# TCP readiness: the image's init server listens on the socket only, so this waits for the real one.
for _ in $(seq 1 60); do
  if docker exec "$name" pg_isready -q -h 127.0.0.1 -U postgres; then
    break
  fi
  sleep 1
done
docker exec "$name" pg_isready -q -h 127.0.0.1 -U postgres

connection="Host=127.0.0.1;Port=${port};Database=postgres;Username=postgres;Password=${password}"
export REELBOLT_TEST_PG="$connection"
export REELBOLT_TEST_DATABASE_URL="$connection"
echo "run-postgres-tests: ${name} on 127.0.0.1:${port}, max_connections=${max_connections}" >&2

status=0
filter="Category=Postgres"
if [[ -n "${REELBOLT_PGTEST_FILTER:-}" ]]; then
  filter="${filter}&(${REELBOLT_PGTEST_FILTER})"
fi
echo "run-postgres-tests: --filter ${filter}" >&2
dotnet test "$project" --filter "$filter" "$@" || status=$?
exit "$status"
