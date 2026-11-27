#!/usr/bin/env bash
# Tests for infra/scripts/gpl-source-bundle.sh against a fake docker (fake-docker-image.sh): the
# bundle's contents, its SHA-256, and that an incomplete source set or an unknown distribution fails.
#
#   infra/scripts/tests/gpl-source-bundle.test.sh
set -euo pipefail
tests_dir="$(cd "$(dirname "$0")" && pwd)"
script="$tests_dir/../gpl-source-bundle.sh"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin"; cp "$tests_dir/fake-docker-image.sh" "$work/bin/docker"; chmod +x "$work/bin/docker"
export PATH="$work/bin:$PATH"
passed=0; failed=0
ok() { printf '  ok   %s\n' "$1"; passed=$((passed + 1)); }
ko() { printf '  FAIL %s\n' "$1"; failed=$((failed + 1)); }

export FAKE_OS_RELEASE=$'ID=debian\nVERSION_ID="12"\nVERSION_CODENAME=bookworm'
export FAKE_DPKG='ffmpeg\t7:5.1.9-0+deb12u1\tffmpeg\t7:5.1.9-0+deb12u1\nglibc\t2.36-9\tlibc6\t2.36-9'
export FAKE_COMPOSITOR=$'@remotion/compositor-linux-x64-gnu\t4.0.545\t/opt/x/node_modules/@remotion/compositor-linux-x64-gnu'
export FAKE_REPO_DIGEST="ghcr.io/o/reelbolt-sandbox-runtime@sha256:1111 "

out="$work/out"
if "$script" --out "$out" ghcr.io/o/reelbolt-sandbox-runtime:1.0 >/dev/null 2>"$work/err"; then ok "debian image: exit 0"; else ko "debian image failed: $(cat "$work/err")"; fi
tb="$out/reelbolt-source-reelbolt-sandbox-runtime-abcdef012345.tar.gz"
[ -s "$tb" ] && ok "tarball named by image name and id12" || ko "tarball missing: $(ls "$out" 2>&1)"
( cd "$out" && sha256sum -c "$(basename "$tb").sha256" >/dev/null 2>&1 ) && ok "SHA-256 file verifies" || ko "SHA-256 file does not verify"
list="$(tar -tzf "$tb")"
for f in MANIFEST.txt packages.tsv source-packages.tsv COMPOSITOR-FFMPEG.md os-release sources/ffmpeg_5.1.9.orig.tar.xz; do
  grep -q "/$f\$" <<<"$list" && ok "bundle contains $f" || ko "bundle lacks $f"
done
x="$work/x"; mkdir "$x"; tar -xzf "$tb" -C "$x"; d="$x/reelbolt-source-reelbolt-sandbox-runtime-abcdef012345"
grep -q 'registry:     ghcr.io/o/reelbolt-sandbox-runtime@sha256:1111' "$d/MANIFEST.txt" && ok "manifest records the registry digest" || ko "manifest lacks the registry digest"
grep -q 'ffmpeg	7:5.1.9-0+deb12u1' "$d/source-packages.tsv" && ok "source list has the installed ffmpeg version" || ko "source list wrong: $(cat "$d/source-packages.tsv")"
grep -q 'remotion-dev/remotion/tree/v4.0.545/packages/compositor' "$d/COMPOSITOR-FFMPEG.md" && ok "compositor reference names the Remotion tag" || ko "compositor reference missing"

# Alpine: origin and version come from `apk list -I`
export FAKE_OS_RELEASE=$'ID=alpine\nVERSION_ID=3.24.3'
export FAKE_APK_LIST=$'ffmpeg-libs-8.1.3-r0 x86_64 {ffmpeg} (GPL-2.0-or-later) [installed]\nmusl-1.2.5-r10 x86_64 {musl} (MIT) [installed]'
rm -rf "$out"
if "$script" --out "$out" alpineimg >/dev/null 2>"$work/err"; then ok "alpine image: exit 0"; else ko "alpine image failed: $(cat "$work/err")"; fi
t2="$(ls "$out"/*.tar.gz 2>/dev/null | head -1)"
[ -n "$t2" ] && tar -xzOf "$t2" --wildcards '*/source-packages.tsv' | grep -q 'ffmpeg	8.1.3-r0' && ok "alpine: origin package and version parsed" || ko "alpine: source list wrong"

# a source package that cannot be found makes the bundle fail (an incomplete offer is worse than none)
rm -rf "$out"
if FAKE_FETCH_MISSING="ffmpeg 8.1.3-r0" "$script" --out "$out" alpineimg >/dev/null 2>"$work/err"; then ko "missing source: expected failure"; else
  grep -q "incomplete" "$work/err" && ok "missing source: fails and says the bundle would be incomplete" || ko "missing source: wrong message: $(cat "$work/err")"; fi
[ -z "$(ls "$out"/*.tar.gz 2>/dev/null)" ] && ok "missing source: no tarball left behind" || ko "missing source: a tarball was written"

# a permissive package from outside the distribution (infra/gpl-source-exempt.txt) needs no source; a copyleft claim in its copyright file fails
export FAKE_OS_RELEASE=$'ID=debian\nVERSION_ID="12"\nVERSION_CODENAME=bookworm'
export FAKE_DPKG='ffmpeg\t7:5.1.9-0+deb12u1\tffmpeg\t7:5.1.9-0+deb12u1\ndocker-ce\t5:29.10.1-1~debian.12~bookworm\tdocker-ce-cli\t5:29.10.1-1~debian.12~bookworm'
rm -rf "$out"
if FAKE_COPYRIGHT="Licensed under the Apache License, Version 2.0" "$script" --out "$out" debimg >/dev/null 2>"$work/err"; then ok "exempt package: bundle succeeds"; else ko "exempt package: failed: $(cat "$work/err")"; fi
t3="$(ls "$out"/*.tar.gz 2>/dev/null | head -1)"
[ -n "$t3" ] && ! tar -xzOf "$t3" --wildcards '*/source-packages.tsv' | grep -q 'docker-ce' && ok "exempt package: no source is looked up for it" || ko "exempt package: still in the source list"
[ -n "$t3" ] && tar -xzOf "$t3" --wildcards '*/exempt.tsv' | grep -q 'docker-ce-cli.*Apache-2.0' && ok "exempt package: recorded in exempt.tsv" || ko "exempt package: not recorded"
rm -rf "$out"
if FAKE_COPYRIGHT="This program is free software under the GNU General Public License" "$script" --out "$out" debimg >/dev/null 2>"$work/err"; then ko "exempt package with a copyleft copyright file: expected failure"; else
  grep -q "copyleft" "$work/err" && ok "exempt package with a copyleft copyright file: fails" || ko "wrong message: $(cat "$work/err")"; fi
rm -rf "$out"
printf 'docker-ce-cli\tApache-2.0\tdocker-ce-cli exempt without a no-copyright-file note\n' > "$work/exempt-bad.txt"
if GPL_BUNDLE_EXEMPT_FILE="$work/exempt-bad.txt" "$script" --out "$out" debimg >/dev/null 2>"$work/err"; then ko "missing copyright file without note: expected failure"; else
  grep -q "no copyright file" "$work/err" && ok "missing copyright file without the note: fails" || ko "wrong message: $(cat "$work/err")"; fi
export FAKE_DPKG='ffmpeg\t7:5.1.9-0+deb12u1\tffmpeg\t7:5.1.9-0+deb12u1\nglibc\t2.36-9\tlibc6\t2.36-9'
export FAKE_OS_RELEASE=$'ID=alpine\nVERSION_ID=3.24.3'

# Alpine: an exempt package (nginx.org's nginx) needs its declared licence to match the entry; a different one refuses
export FAKE_OS_RELEASE=$'ID=alpine\nVERSION_ID=3.24.3'
export FAKE_APK_LIST=$'ffmpeg-libs-8.1.3-r0 x86_64 {ffmpeg} (GPL-2.0-or-later) [installed]\nnginx-1.31.9-r1 x86_64 {nginx} (2-clause BSD-like license) [installed]'
rm -rf "$out"
if "$script" --out "$out" alpineimg >/dev/null 2>"$work/err"; then ok "alpine exempt package: bundle succeeds"; else ko "alpine exempt package failed: $(cat "$work/err")"; fi
t4="$(ls "$out"/*.tar.gz 2>/dev/null | head -1)"
[ -n "$t4" ] && ! tar -xzOf "$t4" --wildcards '*/source-packages.tsv' | grep -q '^nginx' && ok "alpine exempt package: not in the source list" || ko "alpine exempt package: still in the source list"
export FAKE_APK_LIST=$'ffmpeg-libs-8.1.3-r0 x86_64 {ffmpeg} (GPL-2.0-or-later) [installed]\nnginx-1.31.9-r1 x86_64 {nginx} (GPL-2.0-or-later) [installed]'
rm -rf "$out"
if "$script" --out "$out" alpineimg >/dev/null 2>"$work/err"; then ko "alpine exempt package with another licence: expected failure"; else
  grep -q "declares" "$work/err" && ok "alpine exempt package with another licence: fails" || ko "wrong message: $(cat "$work/err")"; fi
export FAKE_APK_LIST=$'ffmpeg-libs-8.1.3-r0 x86_64 {ffmpeg} (GPL-2.0-or-later) [installed]\nmusl-1.2.5-r10 x86_64 {musl} (MIT) [installed]'

# untrusted APKBUILDs: stage B has no network, one read-only mount of the scratch dir, non-root; helpers are digest-pinned
export FAKE_DOCKER_LOG="$work/docker.log"; : > "$FAKE_DOCKER_LOG"; rm -rf "$out"
"$script" --out "$out" alpineimg >/dev/null 2>&1
stage_b="$(grep -- '--network none' "$FAKE_DOCKER_LOG" | head -1 || true)"
[ -n "$stage_b" ] && ok "alpine: the APKBUILD evaluation runs with --network none" || ko "alpine: no --network none container"
case "$stage_b" in *--user*--read-only*--cap-drop\ ALL*) ok "alpine: evaluation is non-root, read-only, no capabilities" ;; *) ko "alpine: evaluation flags: $stage_b" ;; esac
[ "$(grep -o -- ' -v ' <<<"$stage_b" | wc -l)" -eq 1 ] && grep -q -- ':/work:ro ' <<<"$stage_b" && ok "alpine: evaluation mounts only the scratch dir, read-only" || ko "alpine: evaluation mounts: $stage_b"
grep -q 'alpine:3.24@sha256:[0-9a-f]\{64\}' "$FAKE_DOCKER_LOG" && ok "alpine: helper image pinned by digest" || ko "alpine: helper image not digest-pinned"
grep -- ' run ' "$FAKE_DOCKER_LOG" | grep -- '-v ' | grep -q -- '--user ' && ok "alpine: network helpers run non-root" || ko "alpine: a helper runs as root"
: > "$FAKE_DOCKER_LOG"; rm -rf "$out"
FAKE_OS_RELEASE=$'ID=debian\nVERSION_ID="12"\nVERSION_CODENAME=bookworm' "$script" --out "$out" debimg >/dev/null 2>&1
grep -q 'debian:bookworm-slim@sha256:[0-9a-f]\{64\}' "$FAKE_DOCKER_LOG" && ok "debian: helper image pinned by digest" || ko "debian: helper image not digest-pinned"
# VERSION_ID / codename are never spliced into a script: hostile values are refused
rm -rf "$out"
FAKE_OS_RELEASE=$'ID=alpine\nVERSION_ID=3.24$(touch /tmp/pwned)' "$script" --out "$out" x >/dev/null 2>"$work/err" && ko "hostile VERSION_ID accepted" || { grep -q "unusable VERSION_ID" "$work/err" && ok "hostile VERSION_ID refused" || ko "hostile VERSION_ID: $(cat "$work/err")"; }
FAKE_OS_RELEASE=$'ID=debian\nVERSION_CODENAME=a;b' "$script" --out "$out" x >/dev/null 2>"$work/err" && ko "hostile codename accepted" || { grep -q "unusable VERSION_CODENAME" "$work/err" && ok "hostile codename refused" || ko "hostile codename: $(cat "$work/err")"; }
FAKE_OS_RELEASE=$'ID=alpine\nVERSION_ID=3.99.1' "$script" --out "$out" x >/dev/null 2>"$work/err" && ko "unpinned release accepted" || { grep -q "no pinned helper image" "$work/err" && ok "a release without a pinned helper image is refused" || ko "unpinned: $(cat "$work/err")"; }
FAKE_APK_LIST=$'bad-1.0-r0 x86_64 {bad;rm} (MIT) [installed]' "$script" --out "$out" x >/dev/null 2>"$work/err" && ko "hostile package name accepted" || { grep -q "unexpected characters" "$work/err" && ok "hostile package name refused" || ko "hostile package: $(cat "$work/err")"; }
unset FAKE_DOCKER_LOG

# unsupported distribution
FAKE_OS_RELEASE=$'ID=fedora\nVERSION_ID=40' "$script" --out "$out" x >/dev/null 2>"$work/err" && ko "unknown distro: expected failure" || { grep -q unsupported "$work/err" && ok "unknown distro refused" || ko "unknown distro: wrong message"; }
"$script" >/dev/null 2>&1 && ko "no argument: expected usage failure" || ok "no argument: usage error"

printf '\n%d passed, %d failed\n' "$passed" "$failed"
[ "$failed" -eq 0 ]
