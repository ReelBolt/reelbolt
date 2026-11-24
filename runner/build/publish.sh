#!/bin/sh
# Assemble, sign, verify and publish one runner release. The half of the release
# workflow that holds secrets, as a script, so the same code runs in CI and in a
# rehearsal against a local S3 (see README.md, "Rehearsing the publish").
#
#   publish.sh            everything comes from the environment; no arguments
#
# Two modes
#   publish (default)  assemble, sign and upload the artifacts in DIST
#   PROMOTE=true       NO build, NO overwrite: download runner/<VERSION>/* from the
#                      bucket over S3, check those bytes against the signed
#                      PROMOTE_FROM (default beta) manifest, then sign a manifest for
#                      CHANNEL over exactly those bytes. DIST/SOURCES_DIR are unused.
#
# Released bytes are immutable: a publish refuses to replace an existing
# runner/<VERSION>/ object unless the bytes are identical.
#
# Required environment
#   DIST (publish only)                        directory holding the built artifacts (flat)
#   VERSION, CHANNEL            1.4.2 / beta|stable
#   ROLLOUT                     0-100
#   DOWNLOAD_BASE               public root, e.g. https://downloads.reelbolt.ai/runner
#   RUNNER_RELEASE_PUBLIC_KEY_CURRENT   <id>:<hex>
#   RUNNER_MANIFEST_SIGNING_KEY         the Ed25519 private key (read by the manifest
#                                       tool from the environment, never from argv)
#   R2_BUCKET, AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY
#   R2_ENDPOINT or R2_ACCOUNT_ID        endpoint defaults to
#                                       https://$R2_ACCOUNT_ID.eu.r2.cloudflarestorage.com
# Optional
#   MIN_SUPPORTED, NOTES        manifest fields
#   SOURCES_DIR                 verified ffmpeg source tarballs (the licence offer);
#                               publishing without them is refused
#   WORK                        scratch directory (default: a fresh mktemp -d)
#   MANIFEST_BIN                a built reelbolt-manifest; default: built from runner/
#   COSIGN_REQUIRED=true        fail the publish if keyless signing cannot run
#   COSIGN_DISABLE=true         skip keyless signing
#
# Keyless cosign signing runs only when the job has an OIDC token
# (ACTIONS_ID_TOKEN_REQUEST_URL); otherwise it is skipped with a notice.
#
# Every invariant of the old inline job is kept: digests are computed here from
# the bytes that are uploaded, the signature is checked against the expected
# public key before anything leaves, the signing key travels by environment, and
# the last check downloads from the bucket's public URL.
set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
repo=$(CDPATH='' cd -- "$here/../.." && pwd)
# shellcheck source=ci/pins.env
. "$here/ci/pins.env"

need() {
	eval "v=\${$1:-}"
	[ -n "$v" ] || { echo "::error::publish.sh: $1 is not set"; missing=1; }
}
missing=0
promote=${PROMOTE:-false}
[ "$promote" = true ] || need DIST
for n in VERSION CHANNEL ROLLOUT DOWNLOAD_BASE RUNNER_RELEASE_PUBLIC_KEY_CURRENT RUNNER_MANIFEST_SIGNING_KEY R2_BUCKET AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY; do need "$n"; done
[ -n "${R2_ENDPOINT:-}" ] || [ -n "${R2_ACCOUNT_ID:-}" ] || { echo "::error::publish.sh: set R2_ENDPOINT or R2_ACCOUNT_ID"; missing=1; }
[ "$missing" = 0 ] || { echo "::error::Nothing was uploaded and no manifest was signed."; exit 1; }

endpoint=${R2_ENDPOINT:-https://$R2_ACCOUNT_ID.eu.r2.cloudflarestorage.com}
current_key=$RUNNER_RELEASE_PUBLIC_KEY_CURRENT
work=${WORK:-$(mktemp -d)}
mkdir -p "$work"
work=$(CDPATH='' cd -- "$work" && pwd)
dist=""
dist_mount=""
if [ "$promote" != true ]; then
	dist=$(CDPATH='' cd -- "$DIST" && pwd)
	dist_mount=$dist
fi
sources=""
if [ -n "${SOURCES_DIR:-}" ] && [ -d "$SOURCES_DIR" ]; then
	sources=$(CDPATH='' cd -- "$SOURCES_DIR" && pwd)
fi
notices="$here/ffmpeg/THIRD_PARTY_NOTICES.md"

# ---- tools -------------------------------------------------------------------
if [ -n "${MANIFEST_BIN:-}" ]; then
	manifest=$MANIFEST_BIN
else
	(cd "$repo/runner" && go build -o "$work/reelbolt-manifest" ./cmd/reelbolt-manifest)
	manifest="$work/reelbolt-manifest"
fi

# aws from PATH if there is one, else the pinned image. Everything passed to it
# is an absolute path under $dist, $work, $sources or the notices directory, and
# those are mounted at the same paths, so the two modes take identical arguments.
s3() {
	if command -v aws >/dev/null 2>&1; then
		AWS_DEFAULT_REGION=${AWS_DEFAULT_REGION:-auto} aws "$@"
	else
		docker run --rm --network host \
			-e AWS_ACCESS_KEY_ID -e AWS_SECRET_ACCESS_KEY \
			-e AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-auto}" \
			-v "$work:$work" ${dist_mount:+-v "$dist_mount:$dist_mount:ro"} -v "$here/ffmpeg:$here/ffmpeg:ro" \
			${sources:+-v "$sources:$sources:ro"} \
			--entrypoint aws "$AWSCLI_IMAGE" "$@"
	fi
}

# ---- bucket helpers ------------------------------------------------------------
# exists KEY: is there an object with exactly this key? A listing error aborts the
# script (never "probably absent"), so an outage cannot turn into an overwrite.
exists() {
	r=$(s3 s3api list-objects-v2 --endpoint-url "$endpoint" --bucket "$R2_BUCKET" \
		--prefix "$1" --query "Contents[?Key=='$1'].Key" --output text) || {
		echo "::error::publish.sh: could not list $1 in the bucket"
		exit 1
	}
	[ -n "$r" ] && [ "$r" != None ]
}
fetch() { # key dest
	s3 s3 cp --no-progress --endpoint-url "$endpoint" "s3://$R2_BUCKET/$1" "$2" >/dev/null
}

# ---- promote: take the released bytes from the bucket ----------------------------
# Promotion never rebuilds and never writes under runner/<VERSION>/. The bytes are
# downloaded over S3, proven to be the ones the signed source manifest describes
# (signature, version, then digests from the files themselves and from the public
# URLs), and only then is a new manifest signed over them.
if [ "$promote" = true ]; then
	from=${PROMOTE_FROM:-beta}
	if [ "$from" = "$CHANNEL" ]; then
		echo "::error::cannot promote $from to itself"
		exit 1
	fi
	mkdir -p "$work/prev" "$work/promoted"
	exists "runner/$from/manifest.json" || {
		echo "::error::there is no $from manifest in the bucket; nothing to promote"
		exit 1
	}
	fetch "runner/$from/manifest.json" "$work/prev/manifest.json"
	fetch "runner/$from/manifest.json.sig" "$work/prev/manifest.json.sig"
	# Signature, expected key, and: it must be THIS version.
	"$manifest" verify -manifest "$work/prev/manifest.json" -public-key "$current_key" \
		-expect-version "$VERSION" -expect-channel "$from" -json >"$work/prev/verify.json" || {
		echo "::error::the $from manifest is not a valid signed manifest for version $VERSION; refusing to promote"
		exit 1
	}
	# Only what the VERIFIED manifest lists is fetched, nothing else: the bytes of a
	# stable release are exactly the beta manifest's artifacts.
	awk '/"sha256"/ {gsub(/[",]/,"",$2); sha=$2} /"url"/ {gsub(/[",]/,"",$2); n=split($2,p,"/"); print sha "  " p[n]}' \
		"$work/prev/verify.json" >"$work/prev/expected.sums"
	[ -s "$work/prev/expected.sums" ] || { echo "::error::the $from manifest lists no artifacts"; exit 1; }
	dist="$work/promoted"
	while read -r _ name; do
		fetch "runner/$VERSION/$name" "$dist/$name"
	done <"$work/prev/expected.sums"
	# An object with a recognised artifact name that the manifest does not list is
	# something that was put there after the beta (or never was part of it). The
	# stable manifest is assembled only from the verified set, but refuse loudly.
	keys=$(s3 s3api list-objects-v2 --endpoint-url "$endpoint" --bucket "$R2_BUCKET" \
		--prefix "runner/$VERSION/" --query 'Contents[].Key' --output text) || {
		echo "::error::could not list runner/$VERSION/"
		exit 1
	}
	debver=$VERSION
	for key in $keys; do
		name=${key##*/}
		case "$name" in
		"reelbolt-runner-$VERSION-windows-amd64.exe" | "reelbolt-runner-$VERSION-windows-amd64.msi" | \
			"reelbolt-runner-$VERSION-linux-amd64" | "reelbolt-runner-$VERSION-linux-amd64.AppImage" | \
			"reelbolt-runner_${debver}_amd64.deb" | "reelbolt-runner-$VERSION-linux-arm64" | \
			"reelbolt-runner-$VERSION-linux-arm64.AppImage" | "reelbolt-runner_${debver}_arm64.deb")
			if ! grep -q "  $name\$" "$work/prev/expected.sums"; then
				echo "::error::runner/$VERSION/$name exists but is not in the signed $from manifest; refusing to promote (was it planted?)"
				exit 1
			fi
			;;
		esac
	done
	# The bytes just downloaded over S3 must be the ones the manifest signed.
	(cd "$dist" && sha256sum -c "$work/prev/expected.sums") || {
		echo "::error::the objects under runner/$VERSION/ do not match the signed $from manifest; refusing to promote"
		exit 1
	}
	# And the public URLs serve the same bytes (the -download check).
	"$manifest" verify -manifest "$work/prev/manifest.json" -public-key "$current_key" \
		-expect-version "$VERSION" -expect-channel "$from" -download >/dev/null || {
		echo "::error::the public download of the $from release does not verify; refusing to promote"
		exit 1
	}
	echo "publish.sh: promoting $VERSION from $from to $CHANNEL over the existing bytes"
fi

# ---- the manifest ------------------------------------------------------------
args=""
for spec in \
	"windows/amd64/exe:$dist/reelbolt-runner-$VERSION-windows-amd64.exe" \
	"windows/amd64/msi:$dist/reelbolt-runner-$VERSION-windows-amd64.msi" \
	"linux/amd64/tarball:$dist/reelbolt-runner-$VERSION-linux-amd64" \
	"linux/amd64/appimage:$dist/reelbolt-runner-$VERSION-linux-amd64.AppImage" \
	"linux/amd64/deb:$dist/reelbolt-runner_${VERSION}_amd64.deb" \
	"linux/arm64/tarball:$dist/reelbolt-runner-$VERSION-linux-arm64" \
	"linux/arm64/appimage:$dist/reelbolt-runner-$VERSION-linux-arm64.AppImage" \
	"linux/arm64/deb:$dist/reelbolt-runner_${VERSION}_arm64.deb"; do
	key=${spec%%:*}
	path=${spec#*:}
	# A format this release did not produce is skipped loudly rather than
	# silently: a missing leg must not look like a complete release.
	if [ ! -f "$path" ]; then
		echo "::warning::$path was not built; the manifest will not offer $key"
		continue
	fi
	args="$args -artifact $key=$path"
done
if [ -z "$args" ]; then
	echo "::error::no artifacts were found in $dist; the build legs produced nothing to publish"
	exit 1
fi
# shellcheck disable=SC2086
"$manifest" assemble \
	-version "$VERSION" -channel "$CHANNEL" -rollout-percent "$ROLLOUT" \
	${MIN_SUPPORTED:+-min-supported "$MIN_SUPPORTED"} \
	-notes "${NOTES:-}" \
	-url-base "$DOWNLOAD_BASE/$VERSION" \
	-out "$work/manifest.json" \
	$args

# The key goes through the environment and never through argv: a process's
# arguments are visible to every other process on the machine.
"$manifest" sign -manifest "$work/manifest.json" -key-env RUNNER_MANIFEST_SIGNING_KEY -expect-public "$current_key"

"$manifest" verify -manifest "$work/manifest.json" -public-key "$current_key" \
	-expect-version "$VERSION" -expect-channel "$CHANNEL"

# ---- SHA256SUMS --------------------------------------------------------------
# Computed here from the files that are about to be uploaded; `sha256sum -c`
# format, sorted. The versioned file covers everything in the release directory.
(cd "$dist" && find . -maxdepth 1 -type f ! -name SHA256SUMS ! -name '*.sigstore.json' -printf '%f\n' | LC_ALL=C sort | xargs sha256sum) >"$work/SHA256SUMS"
echo "publish.sh: SHA256SUMS"
cat "$work/SHA256SUMS"

# ---- optional keyless cosign (not on a promotion: those bytes are already signed) --
# A convenience for people who want a transparency-logged signature on the
# installers. The update path never reads it, so a failure here fails the publish
# only under COSIGN_REQUIRED=true.
cosign_ok=no
if [ "$promote" = true ]; then
	echo "::notice::promotion: no new cosign signing"
elif [ "${COSIGN_DISABLE:-}" = true ]; then
	echo "::notice::cosign signing disabled (COSIGN_DISABLE=true)"
elif [ -z "${ACTIONS_ID_TOKEN_REQUEST_URL:-}" ]; then
	echo "::notice::no OIDC token in this job (needs id-token: write), so the installers are not cosign-signed"
else
	cosign_bin="$work/cosign"
	if curl -fsSL --retry 3 -o "$cosign_bin" "https://github.com/sigstore/cosign/releases/download/$COSIGN_VERSION/cosign-linux-amd64" &&
		echo "$COSIGN_SHA256_AMD64  $cosign_bin" | sha256sum -c - >/dev/null &&
		chmod 0755 "$cosign_bin"; then
		cosign_ok=yes
		for f in "$dist"/* "$work/SHA256SUMS"; do
			[ -f "$f" ] || continue
			if ! "$cosign_bin" sign-blob --yes --bundle "$work/$(basename "$f").sigstore.json" "$f" >/dev/null; then
				cosign_ok=no
				break
			fi
		done
	fi
	if [ "$cosign_ok" != yes ]; then
		if [ "${COSIGN_REQUIRED:-}" = true ]; then
			echo "::error::cosign signing failed and COSIGN_REQUIRED=true"
			exit 1
		fi
		echo "::warning::cosign signing did not complete; publishing without cosign bundles"
		find "${work:?}" -maxdepth 1 -name '*.sigstore.json' -delete
	fi
fi

# ---- latest.json -------------------------------------------------------------
"$manifest" latest -manifest "$work/manifest.json" -public-key "$current_key" \
	-url-base "$DOWNLOAD_BASE/$CHANNEL/latest" \
	-manifest-url "$DOWNLOAD_BASE/$CHANNEL/manifest.json" \
	-checksums-url "$DOWNLOAD_BASE/$CHANNEL/latest/SHA256SUMS" \
	-dist "$dist" -out "$work/latest.json" -uploads-out "$work/uploads.txt"

# latest/ holds only the manifest's artifacts, so its SHA256SUMS lists exactly
# those names and `sha256sum -c` works on a download of that directory.
tab=$(printf '\t')
: >"$work/SHA256SUMS.latest"
while IFS="$tab" read -r path key; do
	[ -n "$path" ] || continue
	printf '%s  %s\n' "$(sha256sum "$path" | cut -d' ' -f1)" "$key" >>"$work/SHA256SUMS.latest"
done <"$work/uploads.txt"

# ---- upload ------------------------------------------------------------------
nocache="--cache-control no-cache,max-age=0,must-revalidate"
put() { # file key [extra aws args]
	f=$1 k=$2
	shift 2
	s3 s3 cp --no-progress --endpoint-url "$endpoint" "$f" "s3://$R2_BUCKET/$k" "$@"
}

if [ "$promote" != true ]; then
	# The ffmpeg source offer in THIRD_PARTY_NOTICES.md is a licence obligation, not
	# documentation: the exact sources this build used must be reachable at a stable
	# URL for as long as the binaries are distributed. It goes up BEFORE anything
	# that lets a runner or a person find the release (manifest, latest.json).
	if [ -z "$sources" ]; then
		echo "::error::SOURCES_DIR is missing: THIRD_PARTY_NOTICES.md promises the exact ffmpeg sources, and publishing without them is a licence violation rather than a documentation gap."
		exit 1
	fi

	# Released bytes are immutable. Check EVERY target first; refuse before the
	# first byte is written if any existing object differs.
	: >"$work/identical.list"
	check_immutable() { # file key
		if exists "$2"; then
			fetch "$2" "$work/cmp.tmp"
			if cmp -s "$1" "$work/cmp.tmp"; then
				echo "$2" >>"$work/identical.list"
			else
				echo "::error::$2 already exists in the bucket with different bytes. Released artifacts are immutable and are never overwritten; use a new version (or promote the existing one)."
				exit 1
			fi
			rm -f "$work/cmp.tmp"
		fi
	}
	for f in "$sources"/*; do [ -f "$f" ] && check_immutable "$f" "runner/source/$VERSION/$(basename "$f")"; done
	check_immutable "$notices" "runner/source/$VERSION/THIRD_PARTY_NOTICES.md"
	for f in "$dist"/*; do [ -f "$f" ] && check_immutable "$f" "runner/$VERSION/$(basename "$f")"; done
	check_immutable "$work/SHA256SUMS" "runner/$VERSION/SHA256SUMS"

	put_immutable() { # file key
		if grep -qxF "$2" "$work/identical.list"; then
			echo "publish.sh: $2 already published with identical bytes; skipped"
		else
			put "$1" "$2"
		fi
	}
	for f in "$sources"/*; do [ -f "$f" ] && put_immutable "$f" "runner/source/$VERSION/$(basename "$f")"; done
	put_immutable "$notices" "runner/source/$VERSION/THIRD_PARTY_NOTICES.md"
	for f in "$dist"/*; do [ -f "$f" ] && put_immutable "$f" "runner/$VERSION/$(basename "$f")"; done
	put_immutable "$work/SHA256SUMS" "runner/$VERSION/SHA256SUMS"
	for f in "$work"/*.sigstore.json; do
		[ -f "$f" ] || continue
		# A cosign bundle differs on every signing; the first one published stays.
		if exists "runner/$VERSION/$(basename "$f")"; then continue; fi
		put "$f" "runner/$VERSION/$(basename "$f")"
	done
fi

# The signed manifest and its signature: the exact bytes that were signed.
for f in manifest.json manifest.json.sig; do
	# shellcheck disable=SC2086
	put "$work/$f" "runner/$CHANNEL/$f" $nocache
done
# Stable download-page URLs: copies for a human, never an input to an update.
while IFS="$tab" read -r path key; do
	[ -n "$path" ] || continue
	put "$path" "runner/$CHANNEL/latest/$key"
done <"$work/uploads.txt"
put "$work/SHA256SUMS.latest" "runner/$CHANNEL/latest/SHA256SUMS"
# shellcheck disable=SC2086
put "$work/latest.json" "runner/$CHANNEL/latest.json" $nocache

# ---- the last check is against the bucket ------------------------------------
"$manifest" verify -manifest "$work/manifest.json" -public-key "$current_key" \
	-expect-version "$VERSION" -expect-channel "$CHANNEL" -download -json | tee "$work/published.json"

{
	echo "## reelbolt-runner $VERSION -> $CHANNEL"
	echo
	echo "Rollout: $ROLLOUT% of the fleet (bucket = sha256('reelbolt-runner-rollout' || 0x00 || deviceId), first 8 bytes big-endian, mod 100)."
	echo "cosign bundles: $cosign_ok"
	echo
	echo "| artifact | size | sha256 |"
	echo "| --- | --- | --- |"
	if command -v jq >/dev/null 2>&1; then
		jq -r '.artifacts[] | "| \(.key) | \(.size) | \(.sha256) |"' "$work/published.json"
	else
		echo "| (jq not installed) | | see published.json |"
	fi
	echo
	if [ "$promote" = true ]; then
		echo "Promoted from $from over the existing bytes (no rebuild)."
	else
		echo "Promote to stable (no rebuild): dispatch this workflow with version \`$VERSION\`, channel \`stable\`, publish=true and promote=true."
	fi
} >"$work/summary.md"
echo "publish.sh: done; summary in $work/summary.md"
