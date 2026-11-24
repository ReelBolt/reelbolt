#!/usr/bin/env bash
# The gate that must pass before a ReelBolt image is pushed to a registry (GPL-SOURCE-OFFER.md,
# the image publication gate). Run it on the image as built, before the push:
#
#   infra/scripts/check-image-publish-gate.sh --image <ref | image-id> --bundle-dir <dir> [--pins <file>]
#
# Fails (exit 1, every failed check listed) unless, for that image:
#   1. the OCI labels source, licenses, revision, version, title, description are set, and
#      `licenses` names AGPL-3.0-only;
#   2. /licenses holds LICENSE, THIRD_PARTY_NOTICES.md and GPL-SOURCE-OFFER.md, none a placeholder;
#   3. no ffmpeg or ffprobe, and no libavcodec, in the image reports or contains libfdk-aac or
#      --enable-nonfree, EXCEPT the named exception: Remotion's own @remotion/compositor-linux-
#      {x64,arm64}-gnu at the pinned version whose files all hash to the SHA-256 values in
#      infra/image-pins.env (the image publication gate);
#   4. the installed ffmpeg / poppler-utils equal the pins in infra/image-pins.env, and the Remotion
#      compositor version equals REMOTION_VERSION; an ffmpeg that no package pins is a failure;
#   5. a source bundle for this image (infra/scripts/gpl-source-bundle.sh output, named by the first
#      12 hex digits of the image ID) is in --bundle-dir, with a SHA-256 file that verifies.
# It runs the image only with `sh -c` and reads; it pushes nothing.
#
# Post-push mode (the pushed image's other platform; the gate above builds linux/amd64 only):
#
#   infra/scripts/check-image-publish-gate.sh --post-push <registry/image@sha256:...> --platform linux/arm64 [--pins <file>]
#
# Resolves that platform's manifest in the pushed index (`docker buildx imagetools inspect`), checks its
# labels from the registry (1), pulls only that platform, and reads its filesystem WITHOUT running it
# (`docker create` + `docker export`, no emulator needed): /licenses (2), the compositor files, their
# SHA-256 against the pins and any unlisted libav*/ffmpeg-like file (3), the compositor version and the
# installed ffmpeg / poppler-utils versions from the package database (4). Not covered: running the
# foreign-architecture ffmpeg (-buildconf), and the source bundle (5, built once per image); the
# Dockerfile guard already ran during that platform's build. Exit 1 lists every failure.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
pins="$here/../image-pins.env"
image=""
bundle_dir=""
post_push=""
platform="linux/arm64"
while [ $# -gt 0 ]; do
  case "$1" in
    --image) image="${2:-}"; shift 2 ;;
    --bundle-dir) bundle_dir="${2:-}"; shift 2 ;;
    --pins) pins="${2:-}"; shift 2 ;;
    --post-push) post_push="${2:-}"; shift 2 ;;
    --platform) platform="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,36p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "check-image-publish-gate: unknown argument $1" >&2; exit 2 ;;
  esac
done
if [ -z "$post_push" ] && { [ -z "$image" ] || [ -z "$bundle_dir" ]; }; then
  echo "usage: check-image-publish-gate.sh --image <ref> --bundle-dir <dir> [--pins <file>]" >&2
  echo "   or: check-image-publish-gate.sh --post-push <registry/image@sha256:...> --platform linux/arm64 [--pins <file>]" >&2
  exit 2
fi
[ -f "$pins" ] || { echo "check-image-publish-gate: pins file not found: $pins" >&2; exit 2; }
command -v docker >/dev/null 2>&1 || { echo "check-image-publish-gate: docker not found" >&2; exit 2; }

fails=0
pass() { printf '  ok   %s\n' "$1"; }
fail() { printf '  FAIL %s\n' "$1"; fails=$((fails + 1)); }

# ---------------------------------------------------------------------------------------------------
# Post-push mode: one platform of the pushed index, read without running it.
# ---------------------------------------------------------------------------------------------------
if [ -n "$post_push" ]; then
  case "$post_push" in *@sha256:*) ;; *) echo "check-image-publish-gate: --post-push needs <image>@sha256:<digest> (the pushed digest)" >&2; exit 2 ;; esac
  case "$platform" in linux/amd64|linux/arm64) ;; *) echo "check-image-publish-gate: --platform must be linux/amd64 or linux/arm64" >&2; exit 2 ;; esac
  command -v jq >/dev/null 2>&1 || { echo "check-image-publish-gate: jq not found" >&2; exit 2; }
  # shellcheck disable=SC1090
  pin() { local n="$1"; [[ "$n" =~ ^[A-Z0-9_]+$ ]] || return 0; ( . "$pins" >/dev/null 2>&1; printf '%s' "${!n:-}" ); }
  repo="${post_push%@*}"; parch="${platform#*/}"
  case "$parch" in arm64) want_pkg=compositor-linux-arm64-gnu ;; amd64) want_pkg=compositor-linux-x64-gnu ;; esac
  echo "gate (post-push): $post_push, $platform"
  cname="gate-postpush-$$"; tmp="$(mktemp -d)"
  cleanup() { docker rm -f "$cname" >/dev/null 2>&1 || true; rm -rf "$tmp"; }
  trap cleanup EXIT

  raw="$(docker buildx imagetools inspect "$post_push" --raw 2>/dev/null)" || { echo "check-image-publish-gate: cannot read $post_push from the registry" >&2; exit 2; }
  pdigest="$(printf '%s' "$raw" | jq -r --arg a "$parch" '[.manifests[]? | select(.platform.os == "linux" and .platform.architecture == $a) | .digest][0] // empty')"
  if [ -z "$pdigest" ]; then fail "the pushed index has no $platform manifest"
  else
    pass "index has a $platform manifest ($pdigest)"
    pref="$repo@$pdigest"
    # 1. labels, from the registry
    cfg="$(docker buildx imagetools inspect "$pref" --format '{{json .Image}}' 2>/dev/null || true)"
    for l in source licenses revision version title description; do
      v="$(printf '%s' "$cfg" | jq -r --arg k "org.opencontainers.image.$l" '.config.Labels[$k] // empty' 2>/dev/null || true)"
      if [ -z "$v" ]; then fail "label org.opencontainers.image.$l is missing on $platform"
      elif [ "$l" = licenses ] && [ "${v#*AGPL-3.0-only}" = "$v" ]; then fail "label licenses is '$v' on $platform, it must name AGPL-3.0-only"
      else pass "label $l ($platform)"; fi
    done
    # filesystem, without running anything: create (never start) a container and export the files we read
    if ! docker pull --quiet --platform "$platform" "$pref" >/dev/null 2>&1; then fail "cannot pull $pref for $platform"
    elif ! docker create --platform "$platform" --label reelbolt-gate-postpush=1 --name "$cname" "$pref" /nonexistent >/dev/null 2>&1; then fail "cannot create a container from $pref"
    else
      mkdir -p "$tmp/fs"
      # tar exits non-zero for a pattern that matches nothing (an image without a compositor); the checks
      # below decide what must be present.
      docker export "$cname" 2>/dev/null | tar -x -C "$tmp/fs" --no-same-owner --wildcards \
        'licenses/*' './licenses/*' '*@remotion/compositor-*' 'var/lib/dpkg/status' './var/lib/dpkg/status' \
        'lib/apk/db/installed' './lib/apk/db/installed' >/dev/null 2>&1 || true
      # 2. /licenses
      for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do
        lf="$tmp/fs/licenses/$f"
        if [ ! -f "$lf" ] || [ ! -s "$lf" ]; then fail "/licenses/$f is missing or empty on $platform"
        elif [ "$(head -c 11 "$lf")" = PLACEHOLDER ]; then fail "/licenses/$f is the placeholder on $platform"
        else pass "/licenses/$f ($platform)"; fi
      done
      # 3 + 4. compositor: platform, version, every pinned file's hash, no unlisted libav*/ffmpeg-like file
      n_comp=0
      while IFS= read -r d; do
        n_comp=$((n_comp + 1)); pkg="${d##*/}"
        if [ "$pkg" != "$want_pkg" ]; then fail "unexpected compositor package $pkg in the $platform image (expected $want_pkg)"; continue; fi
        varname="COMPOSITOR_SHA256_$(printf '%s' "${pkg#compositor-}" | tr 'a-z-' 'A-Z_')"
        hashes="$(pin "$varname")"
        [ -n "$hashes" ] || { fail "$varname is not pinned in $pins"; continue; }
        ver="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$d/package.json" 2>/dev/null | head -n 1)"
        if [ -n "$ver" ] && [ "$ver" = "$(pin REMOTION_VERSION)" ]; then pass "compositor $ver equals pin REMOTION_VERSION ($platform)"
        else fail "compositor $pkg is '$ver', pin REMOTION_VERSION is '$(pin REMOTION_VERSION)'"; fi
        for e in $hashes; do
          name="${e%%=*}"; want="${e#*=}"
          if [ -f "$d/$name" ] && [ ! -L "$d/$name" ]; then have="$(sha256sum "$d/$name" | cut -d' ' -f1)"; else have=""; fi
          if [ "$have" = "$want" ]; then pass "$pkg/$name hash equals the pin"; else fail "$pkg/$name hash '${have:-missing}' differs from the pin ($want)"; fi
        done
        names=" $(for e in $hashes; do printf '%s ' "${e%%=*}"; done)"
        while IFS= read -r cf; do
          case "$names" in *" ${cf##*/} "*) ;; *) fail "$pkg/${cf##*/} is a libav*/ffmpeg-like file that is not in the pin list" ;; esac
        done < <(find "$d" -type f \( -name 'libav*' -o -name 'libsw*' -o -name 'libpostproc*' -o -name 'ffmpeg*' -o -name 'ffprobe*' -o -name '*fdk*' \) 2>/dev/null)
      done < <(find "$tmp/fs" -type d -path '*/@remotion/compositor-*' -name 'compositor-*' 2>/dev/null)
      [ "$n_comp" -gt 0 ] && pass "$n_comp compositor package(s) checked" || echo "  note no @remotion/compositor-* in this image"
      # installed distro packages, from the package database (not by running them)
      dpkg_status="$tmp/fs/var/lib/dpkg/status"; apk_db="$tmp/fs/lib/apk/db/installed"
      pkgv() { # <package>
        if [ -f "$dpkg_status" ]; then awk -v p="$1" '$1=="Package:" {cur=$2} $1=="Version:" && cur==p {print $2; exit}' "$dpkg_status"
        elif [ -f "$apk_db" ]; then awk -v p="$1" '$0 ~ /^P:/ {cur=substr($0,3)} $0 ~ /^V:/ && cur==p {print substr($0,3); exit}' "$apk_db"; fi
      }
      for pv in "ffmpeg FFMPEG_DEBIAN_VERSION FFMPEG_ALPINE_VERSION" "poppler-utils POPPLER_ALPINE_VERSION POPPLER_ALPINE_VERSION"; do
        set -- $pv; have="$(pkgv "$1")"; [ -n "$have" ] || continue
        if [ -f "$dpkg_status" ]; then want="$(pin "$2")"; else want="$(pin "$3")"; fi
        if [ "$have" = "$want" ]; then pass "$1 $have equals pin ($platform)"; else fail "$1 is $have on $platform, pin is '$want'"; fi
      done
    fi
  fi
  echo "gate (post-push): verified on $platform WITHOUT running the image: labels (registry), /licenses, compositor files + SHA-256 + unlisted-file scan, installed package versions."
  echo "gate (post-push): NOT verified here: ffmpeg -buildconf output of the $platform binaries (needs an $parch runner) and the source bundle; the Dockerfile guard ran during the $platform build."
  if [ "$fails" -gt 0 ]; then echo "gate (post-push): FAILED ($fails check(s)); do not release this image" >&2; exit 1; fi
  echo "gate (post-push): passed"
  exit 0
fi

docker image inspect "$image" >/dev/null 2>&1 || { echo "check-image-publish-gate: no such image: $image" >&2; exit 2; }
image_id="$(docker image inspect --format '{{.Id}}' "$image")"
id12="${image_id#sha256:}"; id12="${id12:0:12}"
echo "gate: $image ($image_id)"

# shellcheck disable=SC1090
pin() { local n="$1"; [[ "$n" =~ ^[A-Z0-9_]+$ ]] || return 0; ( . "$pins" >/dev/null 2>&1; printf '%s' "${!n:-}" ); }
in_image() { docker run --rm --entrypoint sh "$image" -c "$1" 2>&1; }

# 1. labels
for l in source licenses revision version title description; do
  v="$(docker image inspect --format "{{ index .Config.Labels \"org.opencontainers.image.$l\" }}" "$image" 2>/dev/null || true)"
  if [ -z "$v" ] || [ "$v" = "<no value>" ]; then fail "label org.opencontainers.image.$l is missing"
  elif [ "$l" = licenses ] && [ "${v#*AGPL-3.0-only}" = "$v" ]; then fail "label licenses is '$v', it must name AGPL-3.0-only"
  else pass "label $l"; fi
done

# 2. /licenses
for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do
  head="$(in_image "test -s /licenses/$f && head -c 200 /licenses/$f" || true)"
  if [ -z "$head" ]; then fail "/licenses/$f is missing or empty"
  elif [ "${head#PLACEHOLDER}" != "$head" ]; then fail "/licenses/$f is the placeholder (image built without --build-context legal=)"
  else pass "/licenses/$f"; fi
done

# 3 + 4. every ffmpeg in the image, then installed package versions
probe="$(in_image '
for b in $(find / -xdev -type f \( -name ffmpeg -o -name ffprobe \) -perm -u+x 2>/dev/null); do
  out="$("$b" -hide_banner -buildconf 2>&1)$("$b" -hide_banner -encoders 2>&1)"
  case "$out" in *libfdk*|*nonfree*) echo "NONFREE $b";; *) echo "FREE $b";; esac
done
if command -v dpkg-query >/dev/null 2>&1; then
  for p in ffmpeg poppler-utils; do v="$(dpkg-query -W -f="\${Version}" $p 2>/dev/null)"; [ -n "$v" ] && echo "PKG $p $v"; done
fi
if command -v apk >/dev/null 2>&1; then
  for p in ffmpeg poppler-utils; do l="$(apk list -I $p 2>/dev/null)"; v="${l%% *}"; [ -n "$v" ] && echo "PKG $p ${v#$p-}"; done
fi
for f in $(find / -xdev -path "*/@remotion/compositor-*/package.json" 2>/dev/null); do
  v="$(sed -n "s/.*\"version\": *\"\([^\"]*\)\".*/\1/p" "$f" | head -1)"; echo "COMPOSITOR $f $v"
done
for l in $(find / -xdev -type f -name "libavcodec*.so*" 2>/dev/null); do
  if grep -q libfdk_aac "$l" 2>/dev/null; then echo "NONFREE $l"; fi
done
for d in $(find / -xdev -type d -path "*/@remotion/compositor-linux-*-gnu" 2>/dev/null); do
  for f in $(find "$d" -type f \( -name "libav*" -o -name "libsw*" -o -name "libpostproc*" -o -name "ffmpeg*" -o -name "ffprobe*" -o -name "*fdk*" \) 2>/dev/null); do echo "CFILE $f"; done
  for f in "$d"/ffmpeg "$d"/ffprobe "$d"/remotion "$d"/libav*.so "$d"/libsw*.so; do
    [ -f "$f" ] && echo "HASH $f $(sha256sum "$f" | cut -d" " -f1)"
  done
done
' || true)"

# The named exception: Remotion's compositor, only at the pinned version and with every pinned file hash.
nonfree_ok() { # <path of the offending binary>
  local path="$1" d pkg varname hashes e name want have ver
  d="${path%/*}"; pkg="${d##*/}"
  case "$pkg" in
    compositor-linux-x64-gnu|compositor-linux-arm64-gnu) ;;
    *) fail "$path reports libfdk-aac or --enable-nonfree (not the named Remotion exception)"; return ;;
  esac
  case "$d" in */@remotion/compositor-linux-*-gnu) ;; *) fail "$path reports libfdk-aac or --enable-nonfree (not under @remotion)"; return ;; esac
  varname="COMPOSITOR_SHA256_$(printf '%s' "${pkg#compositor-}" | tr 'a-z-' 'A-Z_')"
  hashes="$(pin "$varname")"
  [ -n "$hashes" ] || { fail "$path has libfdk-aac and $varname is not pinned in $pins"; return; }
  # the flagged file itself must be one of the pinned files: a versioned libavcodec.so.N (or any other
  # unlisted libav*/ffmpeg-like file) next to pinned files is not covered by the exception
  case " $(for e in $hashes; do printf '%s ' "${e%%=*}"; done)" in
    *" ${path##*/} "*) ;;
    *) fail "$path reports libfdk-aac or --enable-nonfree and is not one of the pinned files of $pkg; the Remotion exception does not apply"; return ;;
  esac
  ver="$(printf '%s\n' "$probe" | awk -v p="$d/package.json" '$1=="COMPOSITOR" && $2==p {print $3}' | head -n 1)"
  if [ -z "$ver" ] || [ "$ver" != "$(pin REMOTION_VERSION)" ]; then fail "$d is version '$ver', the exception covers only $(pin REMOTION_VERSION)"; return; fi
  for e in $hashes; do
    name="${e%%=*}"; want="${e#*=}"
    have="$(printf '%s\n' "$probe" | awk -v p="$d/$name" '$1=="HASH" && $2==p {print $3}' | head -n 1)"
    if [ "$have" != "$want" ]; then fail "$d/$name hash '${have:-missing}' differs from the pin ($want); the Remotion exception does not apply"; return; fi
  done
  pass "named exception: Remotion compositor $pkg $ver at pinned file hashes (libfdk-aac, non-free; documented)"
}

# Any libav*/libsw*/ffmpeg-like file in a compositor directory that the pin does not list fails, flagged or not.
while read -r kind a _; do
  [ "$kind" = CFILE ] || continue
  cd_="${a%/*}"; cpkg="${cd_##*/}"; cname="${a##*/}"
  # The directory name comes from the image under test: only the two known package names are ever turned into a
  # pin name; every other directory fails the gate (a hostile name must not reach a variable lookup).
  case "$cpkg" in
    compositor-linux-x64-gnu) chashes="$(pin COMPOSITOR_SHA256_LINUX_X64_GNU)" ;;
    compositor-linux-arm64-gnu) chashes="$(pin COMPOSITOR_SHA256_LINUX_ARM64_GNU)" ;;
    *) fail "$a is in a directory that is not a known Remotion compositor package"; continue ;;
  esac
  case " $(for e in $chashes; do printf '%s ' "${e%%=*}"; done)" in
    *" $cname "*) ;;
    *) fail "$a is a libav*/ffmpeg-like file in the compositor directory that is not in the pin list" ;;
  esac
done <<<"$probe"

n_ff=0
while read -r kind a _; do
  case "$kind" in
    FREE) n_ff=$((n_ff + 1)); pass "free build: $a" ;;
    NONFREE) n_ff=$((n_ff + 1)); nonfree_ok "$a" ;;
  esac
done <<<"$probe"

pkg_ver() { printf '%s\n' "$probe" | awk -v p="$1" '$1=="PKG" && $2==p {print $3}' | head -n 1; }
check_pin() { # <package> <pin variable>
  local have want; have="$(pkg_ver "$1")"; want="$(pin "$2")"
  [ -n "$have" ] || return 0
  [ -n "$want" ] || { fail "$1 is installed ($have) but $2 is not pinned in $pins"; return 0; }
  if [ "$have" = "$want" ]; then pass "$1 $have equals pin $2"; else fail "$1 is $have, pin $2 is $want"; fi
}
if printf '%s\n' "$probe" | grep -q '^PKG ffmpeg '; then
  # Debian and Alpine name different pins; pick by which package manager answered.
  if in_image 'command -v dpkg-query' >/dev/null 2>&1; then check_pin ffmpeg FFMPEG_DEBIAN_VERSION; else check_pin ffmpeg FFMPEG_ALPINE_VERSION; fi
fi
check_pin poppler-utils POPPLER_ALPINE_VERSION
# An ffmpeg that no package manager reports and that is not the compositor's is unpinned.
if printf '%s\n' "$probe" | grep -q '^\(FREE\|NONFREE\) ' && ! printf '%s\n' "$probe" | grep -q '^PKG ffmpeg ' \
   && ! printf '%s\n' "$probe" | grep -q '^COMPOSITOR '; then
  fail "an ffmpeg binary is in the image but no package pins it"
fi
while read -r kind path ver; do
  [ "$kind" = COMPOSITOR ] || continue
  want="$(pin REMOTION_VERSION)"
  if [ -n "$want" ] && [ "$ver" = "$want" ]; then pass "compositor $ver equals pin REMOTION_VERSION"
  else fail "compositor at $path is '$ver', pin REMOTION_VERSION is '$want'"; fi
done <<<"$probe"

# 5. source bundle for this image
tarball="$(ls "$bundle_dir"/reelbolt-source-*-"$id12".tar.gz 2>/dev/null | head -n 1 || true)"
if [ -z "$tarball" ]; then
  fail "no source bundle for image $id12 in $bundle_dir (run infra/scripts/gpl-source-bundle.sh)"
elif [ ! -s "$tarball.sha256" ]; then
  fail "source bundle has no .sha256 file: $tarball"
elif ! ( cd "$(dirname "$tarball")" && sha256sum -c "$(basename "$tarball").sha256" >/dev/null 2>&1 ); then
  fail "source bundle SHA-256 does not verify: $tarball"
elif ! tar -tzf "$tarball" 2>/dev/null | grep '/MANIFEST.txt$' >/dev/null; then # no -q: it would SIGPIPE tar
  fail "source bundle has no MANIFEST.txt: $tarball"
else
  pass "source bundle $(basename "$tarball") (SHA-256 verified)"
fi

if [ "$fails" -gt 0 ]; then echo "gate: FAILED ($fails check(s)); do not push this image" >&2; exit 1; fi
echo "gate: passed"
