#!/usr/bin/env bash
# Brings up an isolated copy of the ReelBolt stack (billing on, signup on, Paddle mock) under its own
# compose project and host ports, then seeds it. Safe to re-run: secrets and garage.toml are reused,
# the seed is idempotent. See e2e/README.md.
#
#   E2E_PROJECT=reelbolt-myinit APP_PORT=8112 HTTPS_PORT=9112 PADDLE_MOCK_PORT=28112 e2e/scripts/up-isolated.sh
#   e2e/scripts/up-isolated.sh --provider stripe     # Stripe is the active provider, served by the Stripe fake
#
# Options:
#   --provider paddle|stripe  which billing provider takes new checkouts (default paddle; same as
#                             E2E_BILLING_PROVIDER, the option wins). `stripe` starts the `stripe-mock`
#                             service (compose profile `stripe`), points the Inference API at it with a
#                             generated test key and webhook secret, and seeds the plans' Stripe price ids
#                             and Stripe subscriptions. The Paddle mock keeps running either way.
#
# Inputs (environment):
#   E2E_PROJECT       compose project name (default reelbolt-e2e); never the default stack's name
#   APP_PORT          nginx HTTP port on 127.0.0.1 (default 8180)
#   HTTPS_PORT        nginx HTTPS port on 127.0.0.1 (default 8543)
#   PADDLE_MOCK_PORT  Paddle mock port on 127.0.0.1 (default APP_PORT + 20000)
#   STRIPE_MOCK_PORT  Stripe fake port on 127.0.0.1 with --provider stripe (default APP_PORT + 21000)
#   E2E_BILLING_PROVIDER  paddle (default) or stripe
#   E2E_PROFILES      extra profiles, comma-separated: e2e-site, e2e-engine (default none)
#   E2E_BUILD         1 = build images (default), 0 = reuse what exists
#   E2E_SEED          1 = run seed-billing.mjs after up (default), 0 = skip
#   E2E_AUTH_RATE     nginx rate for the public auth routes (default 100r/s; production is 5r/m)
#   E2E_BUILD_NETWORK build network for the images (default `default`; `host` when BuildKit cannot
#                     resolve DNS, e.g. a host whose resolver only lists an IPv6 upstream)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
E2E_PROJECT="${E2E_PROJECT-reelbolt-e2e}"
E2E_BILLING_PROVIDER="${E2E_BILLING_PROVIDER:-paddle}"

while [ $# -gt 0 ]; do
  case "$1" in
    --provider)
      [ $# -ge 2 ] || { echo "--provider needs paddle or stripe." >&2; exit 2; }
      E2E_BILLING_PROVIDER="$2"
      shift 2
      ;;
    --provider=*)
      E2E_BILLING_PROVIDER="${1#--provider=}"
      shift
      ;;
    -h | --help)
      sed -n '2,/^set -/{/^set -/!p}' "$0"
      exit 0
      ;;
    *)
      echo "Unknown argument '$1'." >&2
      exit 2
      ;;
  esac
done
E2E_BILLING_PROVIDER="$(printf '%s' "$E2E_BILLING_PROVIDER" | tr '[:upper:]' '[:lower:]')"
case "$E2E_BILLING_PROVIDER" in paddle | stripe) ;; *)
  echo "--provider / E2E_BILLING_PROVIDER must be paddle or stripe (got '$E2E_BILLING_PROVIDER')." >&2
  exit 2
  ;;
esac

STACK_DIR="$REPO_ROOT/e2e/.stack/$E2E_PROJECT"

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
  # Compose names the default project after the checkout directory; refuse both this checkout's and
  # the main checkout's, plus the names the developer's own stack has used.
  main_root="$(git -C "$REPO_ROOT" rev-parse --path-format=absolute --git-common-dir 2>/dev/null | sed 's#/\.git$##' || true)"
  for forbidden in reelforge reelbolt "$(basename "$REPO_ROOT")" "${main_root:+$(basename "$main_root")}"; do
    forbidden="$(printf '%s' "$forbidden" | tr '[:upper:]' '[:lower:]')"
    if [ -n "$forbidden" ] && [ "$lower" = "$forbidden" ]; then
      echo "E2E_PROJECT='$name' would address the developer's default stack; refusing. Use e.g. reelbolt-<initiative>." >&2
      exit 2
    fi
  done
}
refuse_project "$E2E_PROJECT"

APP_PORT="${APP_PORT:-8180}"
HTTPS_PORT="${HTTPS_PORT:-8543}"
PADDLE_MOCK_PORT="${PADDLE_MOCK_PORT:-$((APP_PORT + 20000))}"
STRIPE_MOCK_PORT="${STRIPE_MOCK_PORT:-$((APP_PORT + 21000))}"
E2E_PROFILES="${E2E_PROFILES:-}"
E2E_BUILD="${E2E_BUILD:-1}"
E2E_SEED="${E2E_SEED:-1}"
E2E_BUILD_NETWORK="${E2E_BUILD_NETWORK:-default}"
case "$E2E_BUILD_NETWORK" in default | host | none) ;; *)
  echo "E2E_BUILD_NETWORK must be default, host or none." >&2
  exit 2
  ;;
esac

PORTS_TO_CHECK=("$APP_PORT" "$HTTPS_PORT" "$PADDLE_MOCK_PORT")
if [ "$E2E_BILLING_PROVIDER" = "stripe" ]; then PORTS_TO_CHECK+=("$STRIPE_MOCK_PORT"); fi
for p in "${PORTS_TO_CHECK[@]}"; do
  if ! [[ "$p" =~ ^[0-9]+$ ]] || [ "$p" -lt 1024 ] || [ "$p" -gt 65535 ]; then
    echo "Port '$p' is not a number between 1024 and 65535." >&2
    exit 2
  fi
done

PROFILE_ARGS=()
# The Stripe fake is not a user-selectable profile (E2E_PROFILES below); it follows --provider.
if [ "$E2E_BILLING_PROVIDER" = "stripe" ]; then PROFILE_ARGS+=(--profile stripe); fi
IFS=',' read -r -a _profiles <<<"$E2E_PROFILES"
for prof in "${_profiles[@]}"; do
  prof="$(echo "$prof" | tr -d '[:space:]')"
  [ -z "$prof" ] && continue
  case "$prof" in
    e2e-site | e2e-engine) PROFILE_ARGS+=(--profile "$prof") ;;
    *)
      echo "Profile '$prof' is not allowed here; only e2e-site and e2e-engine (sandbox-executor and autoheal must never run in an e2e stack)." >&2
      exit 2
      ;;
  esac
done

mkdir -p "$STACK_DIR"
chmod 700 "$STACK_DIR"

rand_hex() { openssl rand -hex "$1"; }

# ------------------------------------------------------------------ secrets (generated once, then reused)

SECRETS_FILE="$STACK_DIR/secrets.env"
if [ ! -f "$SECRETS_FILE" ]; then
  umask 077
  cat >"$SECRETS_FILE" <<EOF
JWT_SIGNING_KEY=$(rand_hex 32)
SANDBOX_API_TOKEN=$(rand_hex 32)
MINIO_SECRET_KEY=$(rand_hex 20)
PADDLE_WEBHOOK_SECRET=pdl_ntfset_e2e_$(rand_hex 16)
POSTGRES_PASSWORD=$(rand_hex 16)
RABBITMQ_PASSWORD=$(rand_hex 16)
ADMIN_PASSWORD=E2e-admin-$(rand_hex 8)
IMPORT_WORKER_TOKEN=$(rand_hex 32)
EOF
fi
# A secrets file written before the import-worker existed lacks its token; add it once.
if ! grep -q '^IMPORT_WORKER_TOKEN=' "$SECRETS_FILE"; then
  echo "IMPORT_WORKER_TOKEN=$(rand_hex 32)" >>"$SECRETS_FILE"
fi
# The Stripe fake's test key and webhook secret are generated once, and only for a stripe stack, so a
# Paddle stack's secrets file stays as it was. The key carries the sk_test_ prefix the provider's
# options validator insists on; only this stack's fake accepts it.
if [ "$E2E_BILLING_PROVIDER" = "stripe" ]; then
  if ! grep -q '^STRIPE_API_KEY=' "$SECRETS_FILE"; then
    echo "STRIPE_API_KEY=sk_test_e2e_mock_$(rand_hex 16)" >>"$SECRETS_FILE"
  fi
  if ! grep -q '^STRIPE_WEBHOOK_SECRET=' "$SECRETS_FILE"; then
    echo "STRIPE_WEBHOOK_SECRET=whsec_e2e_$(rand_hex 16)" >>"$SECRETS_FILE"
  fi
fi
# shellcheck disable=SC1090
. "$SECRETS_FILE"

GARAGE_TOML="$STACK_DIR/garage.toml"
if [ ! -f "$GARAGE_TOML" ]; then
  umask 077
  sed -e "s#^rpc_secret = .*#rpc_secret = \"$(rand_hex 32)\"#" \
    -e "s#^admin_token = .*#admin_token = \"$(openssl rand -base64 32)\"#" \
    -e "s#^metrics_token = .*#metrics_token = \"$(openssl rand -base64 32)\"#" \
    "$REPO_ROOT/garage/garage.toml.example" >"$GARAGE_TOML"
  # Garage runs as root in its image, but the file is bind-mounted read-only: keep it readable.
  chmod 644 "$GARAGE_TOML"
fi

# nginx.conf with the public-auth rate limit raised: production allows 5 logins a minute per IP, and
# every login of the seed and of the specs comes from 127.0.0.1. Regenerated each run from the
# checkout's own file, so nothing else drifts; the stack therefore does not exercise that limit.
# The `burst=5 nodelay` of each auth location lives in nginx/locations.conf, which the base compose
# file bind-mounts unchanged; nginx.conf cannot override it. What this file controls is how fast that
# burst refills: at 100r/s a slot is back after 10 ms, so a full parallel Playwright run (one sign-in
# per test, plus the throwaway users' four) only meets a 429 when more than six sign-ins land within
# a few milliseconds — and fixtures/stack.ts `postToken` retries those with backoff.
# The marketing form endpoints' zone is raised for the same reason: site_forms (nginx.conf, used by
# the three /api/{contact,subscribe,promotions} locations) allows five requests per address and then
# one a minute, the site specs submit the footer newsletter from 127.0.0.1, and nothing there retries
# a 429. site_promotions is left as it ships (100r/m, burst 100): the suite's few dozen reads of
# /api/promotions per run sit well inside it.
E2E_AUTH_RATE="${E2E_AUTH_RATE:-100r/s}"
E2E_SITE_FORM_RATE="${E2E_SITE_FORM_RATE:-100r/s}"
NGINX_CONF="$STACK_DIR/nginx.conf"
if ! grep -q 'zone=auth_public:10m rate=5r/m;' "$REPO_ROOT/nginx/nginx.conf" \
   || ! grep -q 'zone=site_forms:10m rate=1r/m;' "$REPO_ROOT/nginx/nginx.conf"; then
  echo "nginx/nginx.conf no longer has the auth_public/site_forms zone lines this script rewrites; update up-isolated.sh." >&2
  exit 1
fi
sed -e "s#zone=auth_public:10m rate=5r/m;#zone=auth_public:10m rate=$E2E_AUTH_RATE;#" \
    -e "s#zone=site_forms:10m rate=1r/m;#zone=site_forms:10m rate=$E2E_SITE_FORM_RATE;#" \
    "$REPO_ROOT/nginx/nginx.conf" >"$NGINX_CONF"
chmod 644 "$NGINX_CONF"

# ------------------------------------------------------------------ the compose env file (rewritten every run)

umask 077
cat >"$STACK_DIR/.env" <<EOF
# Generated by e2e/scripts/up-isolated.sh for compose project $E2E_PROJECT. Rewritten on every run;
# secrets live in secrets.env next to it.
COMPOSE_PROJECT_NAME=$E2E_PROJECT
APP_PORT=$APP_PORT
HTTPS_PORT=$HTTPS_PORT
PADDLE_MOCK_PORT=$PADDLE_MOCK_PORT
E2E_GARAGE_TOML=$GARAGE_TOML
E2E_NGINX_CONF=$NGINX_CONF
E2E_BUILD_NETWORK=$E2E_BUILD_NETWORK

POSTGRES_USER=postgres
POSTGRES_PASSWORD=$POSTGRES_PASSWORD
POSTGRES_DB=reelforge
MINIO_ACCESS_KEY=minioadmin
MINIO_SECRET_KEY=$MINIO_SECRET_KEY
MINIO_BUCKET=reelforge
RABBITMQ_USER=reelbolt
RABBITMQ_PASSWORD=$RABBITMQ_PASSWORD

JWT_SIGNING_KEY=$JWT_SIGNING_KEY
SANDBOX_API_TOKEN=$SANDBOX_API_TOKEN
IMPORT_WORKER_TOKEN=$IMPORT_WORKER_TOKEN

ADMIN_EMAIL=admin@e2e.test
ADMIN_PASSWORD=$ADMIN_PASSWORD

BILLING_ENABLED=true
SIGNUP_ENABLED=true
NEXT_PUBLIC_SIGNUP_ENABLED=true
PADDLE_API_KEY=e2e-mock-key
PADDLE_CLIENT_TOKEN=
PADDLE_ENVIRONMENT=sandbox
PADDLE_WEBHOOK_SECRET=$PADDLE_WEBHOOK_SECRET
PADDLE_TOPUP_PRICE_500=pri_e2e_topup_500
PADDLE_TOPUP_PRICE_1000=pri_e2e_topup_1000
PADDLE_TOPUP_PRICE_3000=pri_e2e_topup_3000
EOF

# Only a stripe stack gets the provider switch, the fake's port and its credentials; a Paddle stack's
# env file keeps its original content (the compose file defaults every one of these).
if [ "$E2E_BILLING_PROVIDER" = "stripe" ]; then
  cat >>"$STACK_DIR/.env" <<EOF

E2E_BILLING_PROVIDER=stripe
STRIPE_MOCK_PORT=$STRIPE_MOCK_PORT
STRIPE_API_KEY=$STRIPE_API_KEY
STRIPE_WEBHOOK_SECRET=$STRIPE_WEBHOOK_SECRET
E2E_STRIPE_BASE_URL=http://stripe-mock:8080
E2E_STRIPE_PORTAL_CONFIGURATION_ID=bpc_e2e_portal
EOF
fi

cat >>"$STACK_DIR/.env" <<EOF

ASPNETCORE_ENVIRONMENT=Development
WEB_BUILD_TARGET=production
WEB_NODE_ENV=production
WEB_NODE_OPTIONS=
SITE_BUILD_TARGET=production
SITE_NODE_ENV=production
NEXT_PUBLIC_SITE_URL=http://localhost:$APP_PORT
PUBLIC_BASE_URL=http://localhost:$APP_PORT
COOKIE_SECURE=false
FORCE_HTTPS=false
PLATFORM_DOCS_ENABLED=false
STARTUP_CLEANUP_PURGE_QUEUES=true
EOF

cat >"$STACK_DIR/env.sh" <<EOF
# Source this to point specs and tools at the $E2E_PROJECT stack.
export E2E_PROJECT=$E2E_PROJECT
export E2E_STACK_DIR=$STACK_DIR
export BASE_URL=http://localhost:$APP_PORT
export PADDLE_MOCK_URL=http://localhost:$PADDLE_MOCK_PORT
EOF
if [ "$E2E_BILLING_PROVIDER" = "stripe" ]; then
  cat >>"$STACK_DIR/env.sh" <<EOF
export E2E_BILLING_PROVIDER=stripe
export STRIPE_MOCK_URL=http://localhost:$STRIPE_MOCK_PORT
EOF
fi

compose() {
  docker compose -p "$E2E_PROJECT" \
    --project-directory "$REPO_ROOT" \
    --env-file "$STACK_DIR/.env" \
    -f "$REPO_ROOT/docker-compose.yml" \
    -f "$REPO_ROOT/docker-compose.e2e.yml" \
    "${PROFILE_ARGS[@]}" "$@"
}

UP_ARGS=(up -d --wait --wait-timeout 900 --remove-orphans)
if [ "$E2E_BUILD" = "1" ]; then UP_ARGS+=(--build); fi

echo "==> docker compose -p $E2E_PROJECT ${UP_ARGS[*]} (profiles: ${E2E_PROFILES:-none})"
compose "${UP_ARGS[@]}"

# ------------------------------------------------------------------ seed

if [ "$E2E_SEED" = "1" ]; then
  echo "==> seeding billing fixtures"
  BASE_URL="http://localhost:$APP_PORT" \
    PADDLE_MOCK_URL="http://localhost:$PADDLE_MOCK_PORT" \
    STRIPE_MOCK_URL="http://localhost:$STRIPE_MOCK_PORT" \
    E2E_PROJECT="$E2E_PROJECT" \
    E2E_STACK_DIR="$STACK_DIR" \
    E2E_REPO_ROOT="$REPO_ROOT" \
    node "$REPO_ROOT/e2e/scripts/seed-billing.mjs" --provider "$E2E_BILLING_PROVIDER"
fi

cat <<EOF

Stack $E2E_PROJECT is up.
  BASE_URL         http://localhost:$APP_PORT
  PADDLE_MOCK_URL  http://localhost:$PADDLE_MOCK_PORT   (control API: /__mock/state, /__mock/calls, ...)
  users            $STACK_DIR/users.json
  env for specs    source $STACK_DIR/env.sh
  tear down        E2E_PROJECT=$E2E_PROJECT $REPO_ROOT/e2e/scripts/down-isolated.sh
EOF
if [ "$E2E_BILLING_PROVIDER" = "stripe" ]; then
  cat <<EOF
  STRIPE_MOCK_URL  http://localhost:$STRIPE_MOCK_PORT   (Stripe is the active provider; control API: /__mock/state, /__mock/calls, ...)
  run the specs    source $STACK_DIR/env.sh && cd e2e && npx playwright test --project=billing-stripe
EOF
fi
