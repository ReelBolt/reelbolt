#!/usr/bin/env bash
# Builds the `legal` named build context every published image copies into /licenses/:
#
#   infra/scripts/prepare-legal-context.sh <dir>
#   docker build --build-context legal=<dir> ...
#
# Holds LICENSE, THIRD_PARTY_NOTICES.md and GPL-SOURCE-OFFER.md. The public repository has LICENSE at
# its root; the private repository only has it in the export overlay (tools/open-source-export/overlay/),
# so that is the fallback. Fails if any of the three is missing or empty.
set -euo pipefail

[ $# -eq 1 ] || { echo "usage: prepare-legal-context.sh <dir>" >&2; exit 2; }
dest="$1"
root="${LEGAL_CONTEXT_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}"

mkdir -p "$dest"
for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do
  src="$root/$f"
  if [ ! -s "$src" ] && [ "$f" = LICENSE ]; then src="$root/tools/open-source-export/overlay/LICENSE"; fi
  [ -s "$src" ] || { echo "prepare-legal-context: missing or empty $f (looked in $root)" >&2; exit 1; }
  cp "$src" "$dest/$f"
done
echo "legal context ready: $dest"
