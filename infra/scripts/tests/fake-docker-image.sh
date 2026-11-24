#!/usr/bin/env bash
# A `docker` shim that answers the read-only questions gpl-source-bundle.sh and
# check-image-publish-gate.sh ask about an image, from environment variables. It implements none of
# docker; it returns canned text keyed on the command string, so the scripts' own logic (what passes,
# what fails, what the bundle contains) is what the tests exercise. A real image is the operator's
# local `docker build` check (the image publication gate).
#
#   FAKE_ID               image ID (default sha256:abcdef0123456789...)
#   FAKE_REPO_DIGEST      RepoDigests line (default empty)
#   FAKE_LABEL_<name>     value of org.opencontainers.image.<name> (source, licenses, ...)
#   FAKE_OS_RELEASE       /etc/os-release text
#   FAKE_DPKG / FAKE_APK  package listings for the bundle script
#   FAKE_LICENSE_HEAD     first bytes of every /licenses file (empty = file missing)
#   FAKE_PROBE            the gate's ffmpeg/package/compositor probe output
#   FAKE_HAS_DPKG         1 when `command -v dpkg-query` should succeed
#   FAKE_COMPOSITOR       "name<TAB>version<TAB>dir" lines for the bundle's compositor query
#   FAKE_FETCH_MISSING    "pkg ver" line the helper container reports as missing
#   FAKE_INDEX_JSON       `docker buildx imagetools inspect <ref> --raw` output (the pushed index)
#   FAKE_IMAGE_CONFIG     `... --format '{{json .Image}}'` output (one platform's image config)
#   FAKE_EXPORT_TAR       tar file `docker export` streams (the platform's filesystem)
#   FAKE_DOCKER_LOG       append each argv line here
set -uo pipefail
[ -z "${FAKE_DOCKER_LOG:-}" ] || printf 'docker %s\n' "$*" >>"$FAKE_DOCKER_LOG"
cmd="${1:-}"; shift || true
case "$cmd" in
  pull) exit 0 ;;
  buildx)
    [ "${FAKE_NO_REGISTRY:-0}" = 1 ] && exit 1
    case " $* " in
      *" --raw "*) printf '%s' "${FAKE_INDEX_JSON:-}" ;;
      *"--format"*) printf '%s' "${FAKE_IMAGE_CONFIG:-}" ;;
    esac
    exit 0 ;;
  create) exit 0 ;;
  export) [ -n "${FAKE_EXPORT_TAR:-}" ] && cat "$FAKE_EXPORT_TAR"; exit 0 ;;
  image)
    [ "${1:-}" = inspect ] || exit 0
    shift
    fmt=""; [ "${1:-}" = --format ] && { fmt="$2"; shift 2; }
    [ "${FAKE_NO_IMAGE:-0}" = 1 ] && exit 1
    case "$fmt" in
      '{{.Id}}') echo "${FAKE_ID:-sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789}" ;;
      *RepoDigests*) printf '%s' "${FAKE_REPO_DIGEST:-}" ;;
      *'org.opencontainers.image.'*)
        name="${fmt#*org.opencontainers.image.}"; name="${name%%\"*}"
        var="FAKE_LABEL_$name"; echo "${!var:-<no value>}" ;;
    esac
    exit 0 ;;
  run)
    if [[ " $* " != *" --entrypoint "* && " $* " == *" -v "* ]]; then
      # the source-fetch helper container: "-e HOST_UID=.. -v <host>:/work <image> sh -c <script>"
      args=("$@"); host=""
      for i in "${!args[@]}"; do [ "${args[$i]}" = -v ] && host="${args[$((i + 1))]%%:*}"; done
      if [[ " $* " == *" --network none "* ]]; then printf 'ffmpeg\t8.1.3-r0\tcommunity\tmaster\tx.tar\n'; exit 0; fi   # the no-network APKBUILD evaluation
      mkdir -p "$host/sources/ffmpeg-8.1.3-r0"; echo "fake source" > "$host/sources/ffmpeg_5.1.9.orig.tar.xz"; echo "fake" > "$host/sources/ffmpeg-8.1.3-r0/x.tar"
      [ -z "${FAKE_FETCH_MISSING:-}" ] || echo "$FAKE_FETCH_MISSING" > "$host/missing.txt"
      exit 0
    fi
    c="${*: -1}"
    case "$c" in
      *'for b in $(find'*) printf '%b\n' "${FAKE_PROBE:-}" ;;
      'command -v dpkg-query') [ "${FAKE_HAS_DPKG:-0}" = 1 ]; exit $? ;;
      'cat /etc/os-release') printf '%s\n' "${FAKE_OS_RELEASE:-}" ;;
      *'dpkg-query -W -f='*) printf '%b\n' "${FAKE_DPKG:-}" ;;
      'apk list -I') printf '%b\n' "${FAKE_APK_LIST:-}" ;;
      *'head -c 200 /licenses/'*) printf '%s' "${FAKE_LICENSE_HEAD-GNU AFFERO GENERAL PUBLIC LICENSE}" ;;
      *'@remotion/compositor-'*'package.json'*) printf '%b\n' "${FAKE_COMPOSITOR:-}" ;;
      *'-hide_banner -version'*) echo "ffmpeg version 7.1 (fake)"; echo "--enable-gpl" ;;
    esac
    exit 0 ;;
esac
exit 0
