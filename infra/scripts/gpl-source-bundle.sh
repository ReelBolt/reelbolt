#!/usr/bin/env bash
# Corresponding-source bundle for one ReelBolt container image (GPL-SOURCE-OFFER.md).
#
#   infra/scripts/gpl-source-bundle.sh [--out DIR] <image-ref | image-id>
#
# Reads the package database of that exact image (dpkg on Debian, apk on Alpine), then downloads the
# source packages at the installed versions: `apt-get source` on Debian, the APKBUILD and the files it
# names on Alpine. It also records the reference for the FFmpeg inside @remotion/compositor-* (npm
# package and version, Remotion tag, the version and build options the binary reports). Output, in DIR
# (default ./gpl-source-bundle):
#
#   reelbolt-source-<image>-<id12>.tar.gz          the bundle
#   reelbolt-source-<image>-<id12>.tar.gz.sha256   its SHA-256 (sha256sum format)
#
# <id12> is the first 12 hex digits of the image ID (the config digest), the one identity that exists
# before a push; MANIFEST.txt also records the registry digest when the image has one. Needs docker
# and network access (to the distribution mirrors, from inside a throwaway container). The image is
# only run with `sh -c` to read package data; nothing in it is modified.
set -euo pipefail

usage() { sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-2}"; }

out="./gpl-source-bundle"
image=""
while [ $# -gt 0 ]; do
  case "$1" in
    --out) [ $# -ge 2 ] || usage; out="$2"; shift 2 ;;
    -h|--help) usage 0 ;;
    -*) echo "gpl-source-bundle: unknown option $1" >&2; usage ;;
    *) [ -z "$image" ] || usage; image="$1"; shift ;;
  esac
done
[ -n "$image" ] || usage

die() { echo "gpl-source-bundle: $*" >&2; exit 1; }
script_dir="$(cd "$(dirname "$0")" && pwd)"
command -v docker >/dev/null 2>&1 || die "docker not found"

if ! docker image inspect "$image" >/dev/null 2>&1; then
  docker pull "$image" >&2 || die "cannot pull $image"
fi
image_id="$(docker image inspect --format '{{.Id}}' "$image")"
[ -n "$image_id" ] || die "cannot read the image ID of $image"
id12="${image_id#sha256:}"; id12="${id12:0:12}"
repo_digest="$(docker image inspect --format '{{range .RepoDigests}}{{.}} {{end}}' "$image" 2>/dev/null || true)"

# Image name for the file name: the last path element of the reference, without tag or digest.
ref="${image%@*}"; ref="${ref##*/}"; ref="${ref%%:*}"
case "$ref" in sha256|"") ref="image" ;; esac
case "$ref" in *[!A-Za-z0-9._-]*) ref="image" ;; esac

in_image() { docker run --rm --entrypoint sh "$image" -c "$1"; }

os_release="$(in_image 'cat /etc/os-release')" || die "cannot read /etc/os-release in the image"
os_field() { printf '%s\n' "$os_release" | sed -n "s/^$1=//p" | head -n 1 | tr -d '"'; }
os_id="$(os_field ID)"
os_ver="$(os_field VERSION_ID)"
os_codename="$(os_field VERSION_CODENAME)"

bundle="reelbolt-source-${ref}-${id12}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
work="$out/$bundle"
[ "${GPL_BUNDLE_FRESH:-0}" != 1 ] || rm -rf "$work"
mkdir -p "$work/sources"

# --- 1. what is installed: "<source package> <source version>" per line, plus the binary packages ----
case "$os_id" in
  debian|ubuntu)
    # source:Package / source:Version resolve a binary package to the source package it was built from.
    in_image "dpkg-query -W -f='\${source:Package}\t\${source:Version}\t\${Package}\t\${Version}\n'" \
      | sort -u > "$work/packages.tsv" || die "dpkg-query failed in the image"
    ;;
  alpine)
    # `apk list -I`: "<name>-<version> <arch> {<origin>} (<licence>) [installed]"; the origin is the
    # source package (aport), and a subpackage has the same version as its origin.
    in_image 'apk list -I' | awk '
      { nv=$1; origin=$3; gsub(/[{}]/, "", origin)
        if (match(nv, /-[0-9][^-]*-r[0-9]+$/)) { name=substr(nv, 1, RSTART-1); ver=substr(nv, RSTART+1) }
        else { next }
        printf "%s\t%s\t%s\t%s\n", origin, ver, name, ver }' \
      | sort -u > "$work/packages.tsv" || die "apk list failed in the image"
    # The apk database records, per package, the aports commit it was built from (`c:`): the exact recipe,
    # found without searching the history. Absent or unreadable = the lookup falls back to the branch.
    in_image 'awk "/^o:/{o=substr(\$0,3)} /^c:/{c=substr(\$0,3)} /^\$/{if(o!=\"\"&&c!=\"\")print o\"\\t\"c; o=\"\"; c=\"\"}" /lib/apk/db/installed 2>/dev/null' \
      | grep -P '^[A-Za-z0-9._+-]+\t[0-9a-f]{40}$' | sort -u > "$work/commits.tsv" || : > "$work/commits.tsv"
    ;;
  *) die "unsupported base distribution '$os_id' (supported: debian, ubuntu, alpine)" ;;
esac
[ -s "$work/packages.tsv" ] || die "the image reported no installed packages"

# Packages from outside the distribution with a permissive licence (infra/gpl-source-exempt.txt) need no
# source. An entry only counts if the image's own copyright file for it names no copyleft licence.
exempt_file="${GPL_BUNDLE_EXEMPT_FILE:-$script_dir/../gpl-source-exempt.txt}"
: > "$work/exempt.tsv"
if [ -f "$exempt_file" ]; then
  while IFS="$(printf '\t')" read -r bpkg lic note _; do
    case "$bpkg" in ""|\#*) continue ;; esac
    case "$bpkg" in *[!a-z0-9.+-]*) die "unusable package name '$bpkg' in $exempt_file" ;; esac
    awk -F'\t' -v p="$bpkg" '$3 == p' "$work/packages.tsv" | grep -q . || continue
    if printf '%s' "$lic" | grep -q -i -E 'GPL|General Public|Mozilla Public|\bMPL\b|Eclipse Public|Common Development and Distribution'; then
      die "the exempt entry for $bpkg names a copyleft licence ($lic)"
    fi
    if [ "$os_id" = alpine ]; then
      # Alpine: the evidence is the licence the package database declares for it; it must equal the entry's.
      lic_img="$(in_image 'apk list -I' | awk -v p="$bpkg" '{ nv=$1; if (match(nv, /-[0-9][^-]*-r[0-9]+$/) && substr(nv, 1, RSTART-1) == p && match($0, /\(.*\) \[/)) print substr($0, RSTART+1, RLENGTH-4) }' | head -n 1)"
      [ "$lic_img" = "$lic" ] || die "$bpkg is listed as exempt ($lic) but the image's package database declares '$lic_img'"
      cr="x"; note=no-copyright-file
    else
      cr="$(in_image "cat /usr/share/doc/$bpkg/copyright 2>/dev/null || true")"
    fi
    # On Debian a package with no copyright file is accepted only if its entry says so (column 3: no-copyright-file), so a
    # missing file never passes by accident.
    if [ -z "$cr" ] && [ "$note" != no-copyright-file ]; then
      die "$bpkg is listed as exempt ($lic) but the image has no copyright file for it, and the entry does not say no-copyright-file"
    fi
    if printf '%s' "$cr" | grep -q -i -E 'GNU (Affero |Lesser |Library )?General Public|\b(A|L)?GPL|Mozilla Public|\bMPL\b|Eclipse Public|Common Development and Distribution'; then
      die "$bpkg is listed as exempt ($lic) but its copyright file in the image names a copyleft licence"
    fi
    awk -F'\t' -v p="$bpkg" -v l="$lic" '$3 == p { print p "\t" $4 "\t" l }' "$work/packages.tsv" >> "$work/exempt.tsv"
    awk -F'\t' -v p="$bpkg" '$3 != p' "$work/packages.tsv" > "$work/packages.tsv.new" && mv "$work/packages.tsv.new" "$work/packages.tsv"
  done < "$exempt_file"
fi
[ -s "$work/packages.tsv" ] || die "no packages are left to fetch sources for"
cut -f1,2 "$work/packages.tsv" | sort -u > "$work/source-packages.tsv"
printf '%s\n' "$os_release" > "$work/os-release"

# --- 2. fetch the sources at those versions, inside throwaway containers of the same release ---------
# Package names and versions come out of the image's package database: they are data, never script
# text. They are checked against a strict pattern here and handed to the helper through files.
if grep -qv -P '^[A-Za-z0-9._+-]+\t[A-Za-z0-9._+:~-]+$' "$work/source-packages.tsv"; then
  die "a source package name or version in the image's package database has unexpected characters"
fi
# Helper images are pinned by digest (tag kept for readers). A release without a pin is refused unless
# GPL_BUNDLE_HELPER_IMAGE names one (pin it by digest too). Update the table when the base images move.
helper_pin() {
  case "$1" in
    debian:bookworm) echo "debian:bookworm-slim@sha256:e67eeead736ff7bbe258ef088bff683635c561b1fcfefad0806c1df4ab49ce0e" ;;
    alpine:3.24)     echo "alpine:3.24@sha256:be2656061b536c1883c4034790e21d0a09c5aaaf51968b89368a3c0371ea2c9a" ;;
  esac
}
# Retries and per-file timeouts: a stalled mirror must fail or resume, never hang the run. Finished
# packages leave a marker in $work/done, so a rerun on the same image id resumes where it stopped
# (set GPL_BUNDLE_FRESH=1 to start over).
mkdir -p "$work/done"; rm -f "$work/missing.txt"
case "$os_id" in
  debian|ubuntu)
    case "$os_codename" in ""|*[!a-z0-9-]*) die "unusable VERSION_CODENAME '$os_codename' in the image's os-release" ;; esac
    helper_image="${GPL_BUNDLE_HELPER_IMAGE:-$(helper_pin "$os_id:$os_codename")}"
    [ -n "$helper_image" ] || die "no pinned helper image for $os_id:$os_codename (set GPL_BUNDLE_HELPER_IMAGE to an image pinned by digest)"
    helper='set -eu
trap '"'"'chown -R "$HOST_UID:$HOST_GID" /work'"'"' EXIT
if [ -f /etc/apt/sources.list.d/debian.sources ]; then sed -i "s/^Types: deb$/Types: deb deb-src/" /etc/apt/sources.list.d/debian.sources; fi
if [ -f /etc/apt/sources.list.d/ubuntu.sources ]; then sed -i "s/^Types: deb$/Types: deb deb-src/" /etc/apt/sources.list.d/ubuntu.sources; fi
apt_opts="-o Acquire::Retries=5 -o Acquire::http::Timeout=60 -o Acquire::https::Timeout=60 -o APT::Sandbox::User=root"
apt-get $apt_opts update >/dev/null
cd /work/sources
while IFS="$(printf "\t")" read -r pkg ver; do
  [ -e "/work/done/$pkg=$ver" ] && continue
  if timeout 1800 apt-get $apt_opts source --download-only "$pkg=$ver" >/dev/null; then : > "/work/done/$pkg=$ver"
  else echo "MISSING $pkg $ver" >&2; echo "$pkg $ver" >> /work/missing.txt; fi
done < /work/source-packages.tsv'
    # apt needs root (and its _apt user switch), so this one container keeps the default capabilities; it
    # runs a fixed script, never a downloaded one (--download-only does not unpack or build anything).
    docker run --rm --security-opt no-new-privileges -e "HOST_UID=$(id -u)" -e "HOST_GID=$(id -g)" -v "$work:/work" \
      "$helper_image" sh ${GPL_BUNDLE_DEBUG:+-x} -c "$helper" >&2 || die "source download failed (see output above)"
    ;;
  alpine)
    case "$os_ver" in [0-9]*.[0-9]*) ;; *) die "unusable VERSION_ID '$os_ver' in the image's os-release" ;; esac
    case "$os_ver" in *[!0-9.]*) die "unusable VERSION_ID '$os_ver' in the image's os-release" ;; esac
    alp_rel="${os_ver%.*}"
    [ "${alp_rel#*.}" != "$alp_rel" ] || alp_rel="$os_ver"
    helper_image="${GPL_BUNDLE_HELPER_IMAGE:-$(helper_pin "alpine:$alp_rel")}"
    [ -n "$helper_image" ] || die "no pinned helper image for alpine:$alp_rel (set GPL_BUNDLE_HELPER_IMAGE to an image pinned by digest)"
    # Stage A (network, no APKBUILD is ever executed): find, per package, the APKBUILD at the installed
    # version. The match reads the literal pkgver= and pkgrel= lines as text.
    # Stage B (NO network, only the scratch dir mounted read-only, non-root, read-only root, no
    # capabilities): the downloaded APKBUILDs are untrusted shell; this is the only place they run, and only
    # to print their source= list.
    # Stage C (network, non-root, no APKBUILD is executed): download exactly that list.
    stage_a='set -eu
branch="$ALPINE_BRANCH"
api="https://gitlab.alpinelinux.org/api/v4/projects/alpine%2Faports/repository"
raw="https://gitlab.alpinelinux.org/alpine/aports/-/raw"
cgit="https://git.alpinelinux.org/aports/plain"
# wg URL OUT [ALT_URL]: tries URL then ALT_URL (a second Alpine host) on every round; on the last failure it
# prints the first response line of each, so a blocked or rate-limited runner is visible in the log.
wg() { n=0; until { timeout 300 wget -q -T 30 -O "$2" "$1" 2>/dev/null </dev/null || { [ -n "${3:-}" ] && timeout 300 wget -q -T 30 -O "$2" "$3" 2>/dev/null </dev/null; }; }; do n=$((n + 1)); if [ "$n" -eq 1 ]; then nf=1; for u in "$1" ${3:-}; do timeout 60 wget -S -q -O /dev/null "$u" 2>&1 </dev/null | grep -q "HTTP/[0-9.]* 404" || nf=0; done; [ "$nf" = 1 ] && { rm -f "$2"; return 1; }; fi; if [ "$n" -ge 4 ]; then rm -f "$2"; for u in "$1" ${3:-}; do echo "wget failed: $u: $(timeout 60 wget -S -q -O /dev/null "$u" 2>&1 </dev/null | grep -m1 HTTP || echo no-response)" >&2; done; return 1; fi; sleep $((n * 5)); done; }
mkdir -p /work/sources /work/tmp; cd /work/sources
while IFS="$(printf "\t")" read -r pkg ver; do
  [ -e "/work/done/$pkg-$ver.plan" ] && continue
  got=0
  for repo in main community testing; do
    d="$pkg-$ver"; mkdir -p "$d"
    # 0. the commit the image records for this package: the exact recipe in one request
    c="$(awk -F"\t" -v p="$pkg" "\$1 == p { print \$2; exit }" /work/commits.tsv 2>/dev/null || true)"
    if [ -n "$c" ] && wg "$raw/$c/$repo/$pkg/APKBUILD" "$d/APKBUILD" "$cgit/$repo/$pkg/APKBUILD?id=$c"; then
      pv="$(sed -n "s/^pkgver=//p" "$d/APKBUILD" | head -n 1 | sed "s/[ #].*//" | tr -d "\"\047")"
      pr="$(sed -n "s/^pkgrel=//p" "$d/APKBUILD" | head -n 1 | sed "s/[ #].*//" | tr -d "\"\047")"
      if [ "$pv-r$pr" = "$ver" ]; then
        printf "%s\t%s\t%s\t%s\n" "$pkg" "$ver" "$repo" "$c" > "/work/done/$d.plan"; got=1; break
      fi
    fi
    # 1. the release branch tip: one fast request, and usually the installed version
    wg "$raw/$branch/$repo/$pkg/APKBUILD" "$d/APKBUILD" "$cgit/$repo/$pkg/APKBUILD?h=$branch" || continue
    pv="$(sed -n "s/^pkgver=//p" "$d/APKBUILD" | head -n 1 | sed "s/[ #].*//" | tr -d "\"'"'"'")"
    pr="$(sed -n "s/^pkgrel=//p" "$d/APKBUILD" | head -n 1 | sed "s/[ #].*//" | tr -d "\"'"'"'")"
    if [ "$pv-r$pr" = "$ver" ]; then
      printf "%s\t%s\t%s\t%s\n" "$pkg" "$ver" "$repo" "$branch" > "/work/done/$d.plan"; got=1; break
    fi
    # 2. the tip moved on: the commits that touched this APKBUILD (the API is slow, so only now), newest
    #    first; the one whose pkgver-rN equals the installed version is the exact recipe
    wg "$api/commits?ref_name=$branch&path=$repo/$pkg/APKBUILD&per_page=100" /work/tmp/commits.json || continue
    # commits whose title names the installed pkgver first (an upgrade commit says "upgrade to X"), then the rest
    grep -o "\"id\":\"[0-9a-f]\{40\}\"[^}]*\"title\":\"[^\"]*\"" /work/tmp/commits.json | sed "s/^\"id\":\"\([0-9a-f]*\)\".*\"title\":\"\(.*\)\"$/\1 \2/" > /work/tmp/commits.txt || true
    shas="$(grep -F "${ver%-r*}" /work/tmp/commits.txt | cut -d" " -f1; grep -vF "${ver%-r*}" /work/tmp/commits.txt | cut -d" " -f1)"
    for sha in $shas; do
      wg "$raw/$sha/$repo/$pkg/APKBUILD" "$d/APKBUILD" || continue
      pv="$(sed -n "s/^pkgver=//p" "$d/APKBUILD" | head -n 1 | sed "s/[ #].*//" | tr -d "\"'"'"'")"
      pr="$(sed -n "s/^pkgrel=//p" "$d/APKBUILD" | head -n 1 | sed "s/[ #].*//" | tr -d "\"'"'"'")"
      [ "$pv-r$pr" = "$ver" ] || continue
      printf "%s\t%s\t%s\t%s\n" "$pkg" "$ver" "$repo" "$sha" > "/work/done/$d.plan"; got=1; break
    done
    [ "$got" = 1 ] && break
  done
  [ "$got" = 1 ] || { rm -rf "$pkg-$ver"; echo "MISSING $pkg $ver" >&2; echo "$pkg $ver" >> /work/missing.txt; }
done < /work/source-packages.tsv'
    stage_b='set -u
cd /work/sources
for plan in /work/done/*.plan; do
  [ -e "$plan" ] || continue
  IFS="$(printf "\t")" read -r pkg ver repo sha < "$plan"
  ( set +u; . "./$pkg-$ver/APKBUILD" >/dev/null 2>&1; set -f
    for s in $source; do printf "%s\t%s\t%s\t%s\t%s\n" "$pkg" "$ver" "$repo" "$sha" "$s"; done )
done'
    stage_c='set -eu
raw="https://gitlab.alpinelinux.org/alpine/aports/-/raw"
cgit="https://git.alpinelinux.org/aports/plain"
cd /work/sources
while IFS="$(printf "\t")" read -r pkg ver repo sha s; do
  d="$pkg-$ver"; alt=""
  case "$s" in
    *::*) n="${s%%::*}"; u="${s#*::}"; alt="https://distfiles.alpinelinux.org/distfiles/$ALPINE_REL/$n" ;;
    *://*) u="$s"; n="${s##*/}"; alt="https://distfiles.alpinelinux.org/distfiles/$ALPINE_REL/$n" ;;
    *) u="$raw/$sha/$repo/$pkg/$s"; n="$s"; alt="$cgit/$repo/$pkg/$s?id=$sha" ;;
  esac
  case "$n" in ""|*/*|.*) echo "MISSING $pkg $ver" >&2; echo "$pkg $ver" >> /work/missing.txt; continue ;; esac
  [ -s "$d/$n" ] && continue
  k=0
  until timeout 1800 wget -q -c -T 60 -O "$d/$n.part" "$u" </dev/null 2>/dev/null || { [ -n "$alt" ] && timeout 1800 wget -q -c -T 60 -O "$d/$n.part" "$alt" </dev/null 2>/dev/null; }; do
    k=$((k + 1)); [ "$k" -ge 4 ] && break; sleep $((k * 3))
  done
  if [ "$k" -lt 4 ]; then mv "$d/$n.part" "$d/$n"; else rm -f "$d/$n.part"; echo "MISSING $pkg $ver" >&2; echo "$pkg $ver" >> /work/missing.txt; fi
done < /work/sourcelist.tsv'
    uidgid="$(id -u):$(id -g)"
    docker run --rm --user "$uidgid" --read-only --cap-drop ALL --security-opt no-new-privileges \
      -e "ALPINE_BRANCH=${alp_rel}-stable" -v "$work:/work" "$helper_image" sh ${GPL_BUNDLE_DEBUG:+-x} -c "$stage_a" >&2 \
      || die "APKBUILD lookup failed (see output above)"
    docker run --rm --network none --user "$uidgid" --read-only --cap-drop ALL --security-opt no-new-privileges \
      --memory 512m --pids-limit 128 -v "$work:/work:ro" "$helper_image" sh -c "timeout 600 sh -c '$stage_b'" > "$work/sourcelist.tsv" \
      || die "reading the source lists failed"
    docker run --rm --user "$uidgid" --read-only --cap-drop ALL --security-opt no-new-privileges \
      -e "ALPINE_REL=v${alp_rel}" -v "$work:/work" "$helper_image" sh ${GPL_BUNDLE_DEBUG:+-x} -c "$stage_c" >&2 \
      || die "source download failed (see output above)"
    [ -s "$work/sourcelist.tsv" ] || die "no upstream source list could be read from the downloaded APKBUILDs (the bundle would hold recipes only)"
    # every package that lists a source must hold at least one file besides its APKBUILD
    cut -f1,2 "$work/sourcelist.tsv" | sort -u | while IFS="$(printf '\t')" read -r p v; do
      [ -n "$(find "$work/sources/$p-$v" -type f ! -name APKBUILD -print -quit 2>/dev/null)" ] || printf '%s %s\n' "$p" "$v"
    done >> "$work/missing.txt"
    rm -f "$work/sourcelist.tsv"; rm -rf "$work/tmp"
    ;;
esac
if [ -s "$work/missing.txt" ]; then
  sort -u "$work/missing.txt" -o "$work/missing.txt"
  echo "gpl-source-bundle: no source found for:" >&2; cat "$work/missing.txt" >&2
  die "the bundle would be incomplete"
fi
rm -f "$work/missing.txt"; rm -rf "$work/done"
n_src="$(find "$work/sources" -type f | wc -l | tr -d ' ')"
[ "$n_src" -gt 0 ] || die "no source files were downloaded"

# --- 3. the Remotion compositor FFmpeg: a reference (Remotion builds it, we only redistribute it) ----
comp="$(in_image 'for f in $(find / -xdev -path "*/@remotion/compositor-*/package.json" 2>/dev/null); do
  d=$(dirname "$f"); n=$(sed -n "s/.*\"name\": *\"\([^\"]*\)\".*/\1/p" "$f" | head -1); v=$(sed -n "s/.*\"version\": *\"\([^\"]*\)\".*/\1/p" "$f" | head -1)
  printf "%s\t%s\t%s\n" "$n" "$v" "$d"; done' || true)"
{
  echo "# FFmpeg inside the Remotion compositor"
  echo
  if [ -z "$comp" ]; then
    echo "This image has no @remotion/compositor-* package."
  else
    while IFS="$(printf '\t')" read -r n v d; do
      [ -n "$n" ] || continue
      echo "## $n $v"
      echo
      echo "- npm package: https://www.npmjs.com/package/$n/v/$v (tarball: https://registry.npmjs.org/$n/-/${n#@remotion/}-$v.tgz)"
      echo "- Remotion source and build scripts at the matching tag: https://github.com/remotion-dev/remotion/tree/v$v/packages/compositor"
      echo "- Location in the image: $d"
      echo
      echo '```'
      in_image "'$d/ffmpeg' -hide_banner -version 2>&1 | head -n 3; '$d/ffmpeg' -hide_banner -buildconf 2>&1" || true
      echo '```'
      echo
    done <<<"$comp"
    echo "The corresponding source of this FFmpeg is the FFmpeg release named in the version line above,"
    echo "plus the sources of the libraries named in its build options, built with the scripts at the tag"
    echo "above. This is a reference to Remotion's build, not a rebuild by ReelBolt."
  fi
} > "$work/COMPOSITOR-FFMPEG.md"

# --- 4. manifest, tarball, checksum ------------------------------------------------------------------
{
  echo "ReelBolt corresponding source bundle"
  echo "image:        $image"
  echo "image id:     $image_id"
  echo "registry:     ${repo_digest:-none (not pushed)}"
  echo "distribution: $os_id $os_ver"
  echo "created:      $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "source files: $n_src"
  echo
  echo "packages.tsv         source package, source version, binary package, binary version (as installed, without the exempt packages)"
  echo "source-packages.tsv  the source packages downloaded under sources/"
  echo "exempt.tsv          installed packages with a permissive licence that need no source (infra/gpl-source-exempt.txt): package, version, licence"
  echo "COMPOSITOR-FFMPEG.md reference for the FFmpeg inside @remotion/compositor-*"
  echo "See GPL-SOURCE-OFFER.md in the repository for the offer and how to use this."
} > "$work/MANIFEST.txt"

tarball="$out/$bundle.tar.gz"
tar -C "$out" -czf "$tarball" "$bundle"
( cd "$out" && sha256sum "$bundle.tar.gz" > "$bundle.tar.gz.sha256" )
rm -rf "$work"
echo "$tarball"
cat "$tarball.sha256"
