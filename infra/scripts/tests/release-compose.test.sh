#!/usr/bin/env bash
# infra-lint: docker-compose.release.yml resolves every built-here service to a published image, no build left.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
bash infra/scripts/release-compose-smoke.sh config
# A missing tag must fail loudly, not fall back to a build or a latest tag.
if env -u REELBOLT_IMAGE_TAG RABBITMQ_PASSWORD=x SANDBOX_API_TOKEN=x IMPORT_WORKER_TOKEN=x \
  docker compose -f docker-compose.yml -f docker-compose.release.yml config -q 2>/dev/null; then
  echo "FAIL: config succeeded without REELBOLT_IMAGE_TAG" >&2; exit 1
fi
echo "release-compose.test.sh: ok"
