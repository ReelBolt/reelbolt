#!/usr/bin/env bash
# Tests for infra/scripts/check-image-publish-gate.sh against a fake docker. The gate is only useful
# if it FAILS, so every check is shown to fail on its own and a clean image to pass.
#
#   infra/scripts/tests/check-image-publish-gate.test.sh
set -euo pipefail
tests_dir="$(cd "$(dirname "$0")" && pwd)"
script="$tests_dir/../check-image-publish-gate.sh"
pins="$tests_dir/../../image-pins.env"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/bundles"; cp "$tests_dir/fake-docker-image.sh" "$work/bin/docker"; chmod +x "$work/bin/docker"
export PATH="$work/bin:$PATH"
# ok/ko also run inside ( ... ) subshells, so the tally lives in a file, not in variables.
mkdir -p "$work"; : > "$work/results"
ok() { printf '  ok   %s\n' "$1"; echo ok >> "$work/results"; }
ko() { printf '  FAIL %s\n' "$1"; echo ko >> "$work/results"; }
# shellcheck disable=SC1090
. "$pins"

# a good bundle for image id abcdef012345
b="$work/bundles"; mkdir -p "$work/mk/reelbolt-source-x-abcdef012345"; echo m > "$work/mk/reelbolt-source-x-abcdef012345/MANIFEST.txt"
# MANIFEST.txt first, then thousands of entries: the listing must be read to the end, because a grep -q that
# exits early SIGPIPEs tar and fails the pipeline under pipefail (seen on a real 400 MB bundle).
mkdir -p "$work/mk/reelbolt-source-x-abcdef012345/sources"
for i in $(seq 1 3000); do : > "$work/mk/reelbolt-source-x-abcdef012345/sources/f$i"; done
tar -C "$work/mk" -czf "$b/reelbolt-source-x-abcdef012345.tar.gz" reelbolt-source-x-abcdef012345/MANIFEST.txt reelbolt-source-x-abcdef012345/sources
( cd "$b" && sha256sum reelbolt-source-x-abcdef012345.tar.gz > reelbolt-source-x-abcdef012345.tar.gz.sha256 )

export FAKE_LABEL_source=https://github.com/o/r FAKE_LABEL_licenses=AGPL-3.0-only FAKE_LABEL_revision=abc FAKE_LABEL_version=0.4.0 FAKE_LABEL_title=t FAKE_LABEL_description=d
export FAKE_HAS_DPKG=1
cdir=/opt/x/node_modules/@remotion/compositor-linux-x64-gnu
hash_lines=""; for e in $COMPOSITOR_SHA256_LINUX_X64_GNU; do hash_lines="$hash_lines\nHASH $cdir/${e%%=*} ${e#*=}"; done
# the Remotion compositor ffmpeg reports libfdk (named exception); distro ffmpeg and ffprobe are free
good_probe="FREE /usr/bin/ffmpeg\nFREE /usr/bin/ffprobe\nPKG ffmpeg $FFMPEG_DEBIAN_VERSION\nNONFREE $cdir/ffmpeg\nNONFREE $cdir/libavcodec.so\nCOMPOSITOR $cdir/package.json $REMOTION_VERSION$hash_lines"
export FAKE_PROBE="$good_probe"

run() { "$script" --image img --bundle-dir "$b" --pins "$pins" 2>&1; }
expect_fail() { # <name> <expected text>; the rest of the environment is set by the caller
  local o rc=0; o="$(run)" || rc=$?
  if [ "$rc" -eq 1 ] && grep -q -- "$2" <<<"$o"; then ok "$1"; else ko "$1 (rc=$rc): $o"; fi
}

o="$(run)" && ok "clean image passes" || ko "clean image failed: $o"
grep -q 'gate: passed' <<<"$o" || ko "no 'gate: passed' line"

( export FAKE_LABEL_revision=""; expect_fail "missing revision label" "label org.opencontainers.image.revision is missing" )
( export FAKE_LABEL_licenses=MIT; expect_fail "licenses label without AGPL-3.0-only" "must name AGPL-3.0-only" )
( export FAKE_LICENSE_HEAD=""; expect_fail "missing /licenses files" "/licenses/LICENSE is missing or empty" )
( export FAKE_LICENSE_HEAD="PLACEHOLDER: build with"; expect_fail "placeholder /licenses files" "is the placeholder" )
( export FAKE_PROBE="${good_probe/FREE \/usr\/bin\/ffprobe/NONFREE \/usr\/bin\/ffprobe}"; expect_fail "libfdk/nonfree in distro ffprobe" "/usr/bin/ffprobe reports libfdk-aac" )
( export FAKE_PROBE="${good_probe/HASH $cdir\/ffmpeg /HASH $cdir/ffmpeg 00}"; expect_fail "compositor with a different file hash" "differs from the pin" )
( export FAKE_PROBE="${good_probe/\\nHASH $cdir\/remotion */}"; expect_fail "compositor with a pinned file missing" "missing" )
( export FAKE_PROBE="${good_probe//compositor-linux-x64-gnu/compositor-linux-armv7-gnu}"; expect_fail "fdk in an unlisted compositor package" "not the named Remotion exception" )
( export FAKE_PROBE="FREE /usr/bin/ffmpeg\nPKG ffmpeg $FFMPEG_DEBIAN_VERSION\nNONFREE /usr/lib/x86_64-linux-gnu/libavcodec.so.59"; expect_fail "distro libavcodec with fdk" "libavcodec.so.59 reports libfdk-aac" )
( export FAKE_PROBE="FREE /usr/bin/ffmpeg\nPKG ffmpeg $FFMPEG_DEBIAN_VERSION\nNONFREE /opt/other/ffmpeg"; expect_fail "fdk ffmpeg outside the compositor package" "not the named Remotion exception" )
( export FAKE_PROBE="${good_probe/$FFMPEG_DEBIAN_VERSION/7:5.1.1-1}"; expect_fail "ffmpeg version differs from pin" "ffmpeg is 7:5.1.1-1, pin FFMPEG_DEBIAN_VERSION" )
( export FAKE_PROBE="${good_probe/COMPOSITOR $cdir\/package.json $REMOTION_VERSION/COMPOSITOR $cdir/package.json 4.0.1}"; expect_fail "compositor version differs from pin" "the exception covers only" )
# the exception covers only the pinned files: an unlisted versioned lib next to them fails, flagged or not
( export FAKE_PROBE="$good_probe\nNONFREE $cdir/libavcodec.so.60"; expect_fail "versioned libavcodec.so.N flagged next to pinned files" "not one of the pinned files" )
( export FAKE_PROBE="$good_probe\nCFILE $cdir/libavcodec.so.60"; expect_fail "unlisted libav file in the compositor dir (not flagged)" "not in the pin list" )
( export FAKE_PROBE="$good_probe\nCFILE $cdir/ffmpeg-extra"; expect_fail "unlisted ffmpeg-like file in the compositor dir" "not in the pin list" )
( cfiles=""; for e in $COMPOSITOR_SHA256_LINUX_X64_GNU; do cfiles="$cfiles\nCFILE $cdir/${e%%=*}"; done; export FAKE_PROBE="$good_probe$cfiles"; o="$(run)" && ok "all compositor files listed in the pin pass" || ko "listed compositor files failed: $o" )
( export FAKE_HAS_DPKG=0 FAKE_PROBE="FREE /usr/bin/ffmpeg\nPKG ffmpeg 8.0.1-r0"; expect_fail "alpine ffmpeg differs from pin" "pin FFMPEG_ALPINE_VERSION" )
( export FAKE_HAS_DPKG=0 FAKE_PROBE="FREE /usr/bin/ffmpeg\nPKG ffmpeg $FFMPEG_ALPINE_VERSION\nPKG poppler-utils 1.0-r0"; expect_fail "poppler differs from pin" "pin POPPLER_ALPINE_VERSION" )
( export FAKE_PROBE="FREE /usr/local/bin/ffmpeg"; expect_fail "ffmpeg binary no package pins" "no package pins it" )
( export FAKE_ID=sha256:999999999999aaaa; expect_fail "no source bundle for this image id" "no source bundle for image 999999999999" )
( rm -f "$b"/*.sha256; expect_fail "bundle without SHA-256 file" "no .sha256 file" )
( cd "$b" && sha256sum reelbolt-source-x-abcdef012345.tar.gz > reelbolt-source-x-abcdef012345.tar.gz.sha256 && echo tamper >> reelbolt-source-x-abcdef012345.tar.gz; expect_fail "tampered bundle" "does not verify" )

# an image with no ffmpeg at all (nginx, web) and no pinned package passes the ffmpeg checks
# MANIFEST.txt first, then thousands of entries: the listing must be read to the end, because a grep -q that
# exits early SIGPIPEs tar and fails the pipeline under pipefail (seen on a real 400 MB bundle).
mkdir -p "$work/mk/reelbolt-source-x-abcdef012345/sources"
for i in $(seq 1 3000); do : > "$work/mk/reelbolt-source-x-abcdef012345/sources/f$i"; done
tar -C "$work/mk" -czf "$b/reelbolt-source-x-abcdef012345.tar.gz" reelbolt-source-x-abcdef012345/MANIFEST.txt reelbolt-source-x-abcdef012345/sources
( cd "$b" && sha256sum reelbolt-source-x-abcdef012345.tar.gz > reelbolt-source-x-abcdef012345.tar.gz.sha256 )
# A hostile directory name from the image under test must fail the gate and must not execute anything or reach a variable lookup.
canary="$work/canary-executed"; rm -f "$canary"
for hostile in "compositor-linux-x64-gnu}\$(touch\${IFS}$canary)\${y" "compositor-linux-x64-gnu\$(TOUCH\${IFS}$canary)" "compositor-linux-x64-gnu;touch\${IFS}$canary" "../../compositor-linux-x64-gnu-evil"; do
  hdir="/opt/x/node_modules/@remotion/$hostile"
  ( export FAKE_PROBE="FREE /usr/bin/ffmpeg\nCFILE $hdir/libavcodec.so\nNONFREE $hdir/ffmpeg"; expect_fail "hostile directory name fails the gate: ${hostile:0:40}" "not a known Remotion compositor package\|not under @remotion\|not a known" )
done
[ ! -e "$canary" ] && ok "hostile directory names executed nothing" || ko "a hostile directory name executed a command"
( export FAKE_PROBE=""; o="$(run)" && echo "$o" | grep -q "gate: passed" ) && ok "image with no ffmpeg passes" || ko "image with no ffmpeg failed"
( export FAKE_NO_IMAGE=1; "$script" --image img --bundle-dir "$b" >/dev/null 2>&1 && exit 1 || exit 0 ) && ok "unknown image: refused" || ko "unknown image accepted"

passed="$(grep -c '^ok$' "$work/results" || true)"; failed="$(grep -c '^ko$' "$work/results" || true)"
# ---- post-push mode: one platform of a pushed index, read without running it ----
pp="$work/pp"; mkdir -p "$pp/fs/licenses" "$pp/fs/app/node_modules/@remotion/compositor-linux-arm64-gnu" "$pp/fs/var/lib/dpkg"
for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do echo "GNU AFFERO GENERAL PUBLIC LICENSE" > "$pp/fs/licenses/$f"; done
cd_="$pp/fs/app/node_modules/@remotion/compositor-linux-arm64-gnu"
names="ffmpeg ffprobe remotion libavcodec.so libavdevice.so libavfilter.so libavformat.so libavutil.so libswresample.so libswscale.so"
pinned=""; for n in $names; do echo "bytes of $n" > "$cd_/$n"; pinned="$pinned $n=$(sha256sum "$cd_/$n" | cut -d' ' -f1)"; done
printf '{"name":"@remotion/compositor-linux-arm64-gnu","version":"%s"}\n' "$REMOTION_VERSION" > "$cd_/package.json"
printf 'Package: ffmpeg\nVersion: %s\n\nPackage: other\nVersion: 1\n' "$FFMPEG_DEBIAN_VERSION" > "$pp/fs/var/lib/dpkg/status"
cp "$pins" "$pp/pins.env"; printf 'COMPOSITOR_SHA256_LINUX_ARM64_GNU="%s"\n' "${pinned# }" >> "$pp/pins.env"
tar -C "$pp/fs" -cf "$pp/fs.tar" .
cfg_ok='{"config":{"Labels":{"org.opencontainers.image.source":"s","org.opencontainers.image.licenses":"AGPL-3.0-only","org.opencontainers.image.revision":"r","org.opencontainers.image.version":"1","org.opencontainers.image.title":"t","org.opencontainers.image.description":"d"}}}'
idx_ok='{"manifests":[{"platform":{"os":"linux","architecture":"amd64"},"digest":"sha256:aaa"},{"platform":{"os":"linux","architecture":"arm64"},"digest":"sha256:bbb"},{"platform":{"os":"unknown","architecture":"unknown"},"digest":"sha256:ccc"}]}'
export FAKE_INDEX_JSON="$idx_ok" FAKE_IMAGE_CONFIG="$cfg_ok" FAKE_EXPORT_TAR="$pp/fs.tar"
pprun() { "$script" --post-push "ghcr.io/o/img@sha256:abc" --platform linux/arm64 --pins "$pp/pins.env" 2>&1; }
ppfail() { local o rc=0; o="$(pprun)" || rc=$?; if [ "$rc" -eq 1 ] && grep -q -- "$2" <<<"$o"; then ok "post-push: $1"; else ko "post-push: $1 (rc=$rc): $o"; fi; }
o="$(pprun)" && ok "post-push: clean arm64 image passes" || ko "post-push clean failed: $o"
grep -q 'WITHOUT running' <<<"$o" && grep -q 'NOT verified here' <<<"$o" && ok "post-push: states what is and is not verified" || ko "post-push: no verified/not-verified statement"
( export FAKE_IMAGE_CONFIG='{"config":{"Labels":{}}}'; ppfail "missing labels" "label org.opencontainers.image.revision is missing" )
( export FAKE_INDEX_JSON='{"manifests":[{"platform":{"os":"linux","architecture":"amd64"},"digest":"sha256:aaa"}]}'; ppfail "index without an arm64 manifest" "no linux/arm64 manifest" )
( echo "tampered" > "$cd_/libavcodec.so"; tar -C "$pp/fs" -cf "$pp/fs-bad.tar" .; echo "bytes of libavcodec.so" > "$cd_/libavcodec.so"; export FAKE_EXPORT_TAR="$pp/fs-bad.tar"; ppfail "tampered compositor file" "libavcodec.so hash" )
( echo x > "$cd_/libavcodec.so.60"; tar -C "$pp/fs" -cf "$pp/fs-extra.tar" .; rm -f "$cd_/libavcodec.so.60"; export FAKE_EXPORT_TAR="$pp/fs-extra.tar"; ppfail "versioned libavcodec.so.N next to pinned files" "not in the pin list" )
( echo "PLACEHOLDER: build" > "$pp/fs/licenses/LICENSE"; tar -C "$pp/fs" -cf "$pp/fs-ph.tar" .; echo "GNU AFFERO GENERAL PUBLIC LICENSE" > "$pp/fs/licenses/LICENSE"; export FAKE_EXPORT_TAR="$pp/fs-ph.tar"; ppfail "placeholder licenses" "is the placeholder" )
( printf 'Package: ffmpeg\nVersion: 9:9\n' > "$pp/fs/var/lib/dpkg/status"; tar -C "$pp/fs" -cf "$pp/fs-v.tar" .; export FAKE_EXPORT_TAR="$pp/fs-v.tar"; ppfail "ffmpeg version differs from pin" "ffmpeg is 9:9" )
rc=0; "$script" --post-push ghcr.io/o/img:latest --pins "$pp/pins.env" >/dev/null 2>&1 || rc=$?; [ "$rc" -eq 2 ] && ok "post-push: a tag instead of a digest is refused" || ko "post-push: tag accepted"

printf '\n%d passed, %d failed\n' "$passed" "$failed"
[ "$failed" -eq 0 ]
