#!/usr/bin/env bash
# Lets a BROWSER reach the isolated editor-v2 stack's object storage, which the picture editor needs: "Save to project" and the
# erase/fill mask upload PUT to a presigned URL straight from the page (docs/direct-upload.md). The stack script leaves the
# public storage endpoint empty (URLs name the compose-internal host garage:3900) and Garage has no CORS rule, so without this
# those uploads fail with "The upload could not reach storage".
#
#   e2e/scripts/editor-v2-picture-storage.sh apply [--from <worktree>]   point presigned URLs at localhost + add the CORS rule
#   e2e/scripts/editor-v2-picture-storage.sh show  [--from <worktree>]   print the bucket's CORS rule
#
# Run it AFTER `editor-v2-stack.sh up` (a later `up` recreates `inference` and drops the endpoint again; just re-run this).
# Honours the same EDITOR_STACK_STATE_DIR as the stack script, and EDITOR_STACK_PROJECT (compose project, default
# reelbolt-editor-remediation). Does not edit editor-v2-stack.sh; touches nothing outside that compose project.
set -euo pipefail
cmd=${1:-apply}; shift || true
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
FROM=$(cd "$HERE/../.." && pwd)
while [ $# -gt 0 ]; do case "$1" in --from) FROM=$(cd "$2" && pwd); shift 2 ;; *) echo "unknown argument: $1" >&2; exit 2 ;; esac; done
PROJECT=${EDITOR_STACK_PROJECT:-reelbolt-editor-remediation}
STATE_DIR=${EDITOR_STACK_STATE_DIR:-/tmp/editor-remediation}
ENV_FILE=$STATE_DIR/stack.env
[ -f "$ENV_FILE" ] || { echo "no $ENV_FILE: run editor-v2-stack.sh up first" >&2; exit 1; }
# shellcheck disable=SC1090
. "$ENV_FILE"
export S3_ENDPOINT=http://localhost:${MINIO_API_PORT} S3_ACCESS_KEY=$MINIO_ACCESS_KEY S3_SECRET_KEY=$MINIO_SECRET_KEY S3_BUCKET=${MINIO_BUCKET:-reelforge}
export ALLOWED_ORIGIN=http://localhost:${APP_PORT}

case "$cmd" in
  apply)
    docker compose -p "$PROJECT" --project-directory "$FROM" --env-file "$ENV_FILE" \
      -f "$FROM/docker-compose.yml" -f "$STATE_DIR/compose.editor-v2.yml" -f "$HERE/editor-v2-picture-storage.compose.yml" \
      up -d --no-deps --wait --wait-timeout 300 inference
    node "$HERE/editor-v2-picture-storage.mjs" apply
    ;;
  show) node "$HERE/editor-v2-picture-storage.mjs" show ;;
  *) sed -n 2,12p "$0" >&2; exit 2 ;;
esac
