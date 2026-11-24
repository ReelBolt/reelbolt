#!/bin/sh
# Package and sign the Windows runner on Linux (replaces package.ps1's job).
#
#   package.sh --version 1.4.2 --dist <dir> --out <dir> --signing none|trusted-signing|pfx
#
# --dist holds build-binaries.sh's output. Outputs, in --out:
#   reelbolt-runner-<v>-windows-amd64.msi   the per-user installer
#   reelbolt-runner-<v>-windows-amd64.exe   the bare signed daemon (manifest "exe")
#
# ORDER MATTERS (same reason as package.ps1): the exes are signed FIRST, then the
# MSI is built embedding the signed files, then the MSI is signed. The runner's
# own update path refuses an artifact WinVerifyTrust does not accept.
#
# Signing is jsign (JSIGN_JAR, pinned by sha256 in Dockerfile.toolchain):
#   trusted-signing  Azure Trusted Signing. env AZURE_TRUSTED_SIGNING_TOKEN (an
#                    Azure AD access token, e.g. from GitHub OIDC), TS_ENDPOINT
#                    (https://<region>.codesigning.azure.net), TS_ACCOUNT, TS_PROFILE.
#   pfx              env WINDOWS_SIGNING_PFX_BASE64, WINDOWS_SIGNING_PFX_PASSWORD.
#   TS_TIMESTAMP_URL RFC3161 server (default http://timestamp.acs.microsoft.com;
#                    for pfx pick your CA's, default http://timestamp.digicert.com).
# Secrets never go on argv: jsign reads them from "env:NAME" / "file:PATH".
# The PFX is written to a 0600 temp file in a 0700 dir and shredded on exit.
# With --signing != none every signature is verified with osslsigncode and any
# unsigned or unverifiable file fails the run.
#   EXPECTED_SIGNER_SUBJECT / EXPECTED_SIGNER_THUMBPRINT  the signer (leaf) certificate's
#                    EXACT subject DN and/or SHA-256 (see signer-check.sh for the format).
#                    The workflow passes the repo variables RUNNER_WINDOWS_SIGNER_SUBJECT /
#                    RUNNER_WINDOWS_SIGNER_THUMBPRINT and refuses to publish with neither.
set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)

version=""
dist=""
out=""
signing=none
while [ $# -gt 0 ]; do
	case "$1" in
	--version) version=${2:-}; shift 2 ;;
	--dist) dist=${2:-}; shift 2 ;;
	--out) out=${2:-}; shift 2 ;;
	--signing) signing=${2:-}; shift 2 ;;
	*) echo "package.sh: unknown argument $1" >&2; exit 2 ;;
	esac
done
case "$version" in
[0-9]*.[0-9]*.[0-9]*) ;;
*) echo "package.sh: --version '$version' is not a dotted version (1.4.2)" >&2; exit 2 ;;
esac
. "$here/msi-version.sh"
. "$here/signer-check.sh"
# Windows Installer needs a numeric a.b.c(.d) ProductVersion; the semver stays in the
# binaries, the manifest and the MSI description.
msi_ver=$(msi_version "$version") || exit 2
case "$signing" in none | trusted-signing | pfx) ;; *) echo "package.sh: --signing must be none, trusted-signing or pfx" >&2; exit 2 ;; esac
[ -n "$dist" ] && [ -d "$dist" ] || { echo "package.sh: --dist must be an existing directory" >&2; exit 2; }
[ -n "$out" ] || { echo "package.sh: --out is required" >&2; exit 2; }
dist=$(CDPATH='' cd -- "$dist" && pwd)
mkdir -p "$out"
out=$(CDPATH='' cd -- "$out" && pwd)

for f in reelbolt-runner.exe reelbolt-runner-desktop.exe ffmpeg.exe ffprobe.exe THIRD_PARTY_NOTICES.md; do
	[ -f "$dist/$f" ] || { echo "package.sh: $f is missing from $dist; run build-binaries.sh first" >&2; exit 1; }
done
for t in wixl msibuild msiinfo osslsigncode; do command -v "$t" >/dev/null 2>&1 || { echo "package.sh: $t is not on PATH (use the toolchain container)" >&2; exit 1; }; done

name="reelbolt-runner-$version-windows-amd64"
upgrade_code=${UPGRADE_CODE:-6C1E4E4B-4B2E-4C31-9E4B-1E0C9C2F0C21}
tmp=$(mktemp -d)
chmod 0700 "$tmp"
cleanup() {
	if [ -f "$tmp/codesign.pfx" ]; then shred -u "$tmp/codesign.pfx" 2>/dev/null || rm -f "$tmp/codesign.pfx"; fi
	rm -rf "$tmp"
}
trap cleanup EXIT INT TERM

if [ "$signing" = pfx ]; then
	: "${JSIGN_JAR:?package.sh: JSIGN_JAR is not set}"
	[ -n "${WINDOWS_SIGNING_PFX_BASE64:-}" ] && [ -n "${WINDOWS_SIGNING_PFX_PASSWORD:-}" ] || {
		echo "package.sh: --signing pfx needs WINDOWS_SIGNING_PFX_BASE64 and WINDOWS_SIGNING_PFX_PASSWORD" >&2
		exit 1
	}
	umask 077
	printf '%s' "$WINDOWS_SIGNING_PFX_BASE64" | base64 -d >"$tmp/codesign.pfx"
	tsa=${TS_TIMESTAMP_URL:-http://timestamp.digicert.com}
elif [ "$signing" = trusted-signing ]; then
	: "${JSIGN_JAR:?package.sh: JSIGN_JAR is not set}"
	for v in AZURE_TRUSTED_SIGNING_TOKEN TS_ENDPOINT TS_ACCOUNT TS_PROFILE; do
		[ -n "$(printenv "$v" || true)" ] || { echo "package.sh: --signing trusted-signing needs $v" >&2; exit 1; }
	done
	tsa=${TS_TIMESTAMP_URL:-http://timestamp.acs.microsoft.com}
fi

sign() {
	for f in "$@"; do
		case "$signing" in
		pfx)
			java -jar "$JSIGN_JAR" --storetype PKCS12 --keystore "$tmp/codesign.pfx" \
				--storepass env:WINDOWS_SIGNING_PFX_PASSWORD --alg SHA-256 --tsmode RFC3161 --tsaurl "$tsa" "$f"
			;;
		trusted-signing)
			java -jar "$JSIGN_JAR" --storetype TRUSTEDSIGNING --keystore "$TS_ENDPOINT" \
				--storepass env:AZURE_TRUSTED_SIGNING_TOKEN --alias "$TS_ACCOUNT/$TS_PROFILE" \
				--alg SHA-256 --tsmode RFC3161 --tsaurl "$tsa" "$f"
			;;
		esac
	done
}

# osslsigncode verify: "Number of verified signatures: 1" means a signature is
# present AND its message digest matches the file, so an unsigned, truncated or
# tampered file fails. Chain trust to Microsoft's roots is NOT checked here
# (those roots are not in the Debian CA bundle; osslsigncode exits non-zero for
# that reason on a perfectly good Trusted Signing signature). That last step is
# WinVerifyTrust on a real Windows host: see the manual smoke checklist.
verify() {
	for f in "$@"; do
		res=$(osslsigncode verify -in "$f" 2>&1 || true)
		echo "$res" | grep -q "Number of verified signatures: [1-9]" || {
			echo "$res" | tail -5 >&2
			echo "package.sh: $f is not validly signed; it must not be published" >&2
			exit 1
		}
		# Optional pins on WHO signed (exact subject DN and/or leaf SHA-256): see
		# signer-check.sh. A signature by any other certificate fails even though
		# its digest is fine.
		signer_check "$f" "$res" || { echo "package.sh: $f must not be published" >&2; exit 1; }
		echo "package.sh: signed and verified $(basename "$f")"
	done
}

# Signing happens in place in --dist: refuse an input that already carries a
# signature, so running this twice (or on a signed tree) cannot stack signatures.
if [ "$signing" != none ]; then
	for f in reelbolt-runner.exe reelbolt-runner-desktop.exe ffmpeg.exe ffprobe.exe; do
		if ! osslsigncode verify -in "$dist/$f" 2>&1 | grep -q "No signature found"; then
			echo "package.sh: $dist/$f already carries a signature (or cannot be read); run build-binaries.sh again to get unsigned inputs" >&2
			exit 1
		fi
	done
fi

if [ "$signing" != none ]; then
	echo "package.sh: signing the executables before the MSI is built, so the MSI embeds signed files"
	sign "$dist/reelbolt-runner.exe" "$dist/reelbolt-runner-desktop.exe" "$dist/ffmpeg.exe" "$dist/ffprobe.exe"
	verify "$dist/reelbolt-runner.exe" "$dist/reelbolt-runner-desktop.exe" "$dist/ffmpeg.exe" "$dist/ffprobe.exe"
fi

msi="$out/$name.msi"
# The MSI is built with wixl (msitools) from runner.wixl.wxs, NOT with "wix build":
# WiX 6 on Linux rejects Directory names (WIX0389) and then needs native
# msi.dll/wixnative.exe to bind, so it cannot produce an MSI there at all. wixl
# does no ICE validation either; the structure is checked below with msitools.
wixl -a x64 -D "Version=$msi_ver" -D "SemVer=$version" -D "DistDir=$dist" -D "UpgradeCode=$upgrade_code" \
	-o "$tmp/runner.msi" "$here/runner.wixl.wxs"

# wixl has no <Environment>, so the per-user PATH entry (first, removed on
# uninstall) is added as an Environment table row plus the two standard actions.
printf 'Environment\tName\tValue\tComponent_\ns72\tl255\tL255\ts72\nEnvironment\tEnvironment\nRunnerPathEntry\t=PATH\t[RunnerDir];[~]\tUserPath\n' >"$tmp/Environment.idt"
msibuild "$tmp/runner.msi" -i "$tmp/Environment.idt"
msiinfo export "$tmp/runner.msi" InstallExecuteSequence >"$tmp/seq.idt"
printf 'RemoveEnvironmentStrings\t\t3300\nWriteEnvironmentStrings\t\t5200\n' >>"$tmp/seq.idt"
msibuild "$tmp/runner.msi" -i "$tmp/seq.idt"
msiinfo export "$tmp/runner.msi" Environment | grep -q 'RunnerPathEntry' || { echo "package.sh: PATH entry missing from the MSI" >&2; exit 1; }
msiinfo export "$tmp/runner.msi" InstallExecuteSequence | grep -q '^WriteEnvironmentStrings' || { echo "package.sh: WriteEnvironmentStrings missing from the MSI" >&2; exit 1; }
for f in reelbolt-runner.exe reelbolt-runner-desktop.exe ffmpeg.exe ffprobe.exe; do
	msiinfo export "$tmp/runner.msi" File | grep -q "$f" || { echo "package.sh: $f missing from the MSI" >&2; exit 1; }
done
cp "$tmp/runner.msi" "$msi"
[ -s "$msi" ] || { echo "package.sh: no MSI produced" >&2; exit 1; }
echo "package.sh: $msi"

if [ "$signing" != none ]; then
	sign "$msi"
	verify "$msi"
fi

cp "$dist/reelbolt-runner.exe" "$out/$name.exe"
echo "package.sh: $out/$name.exe (raw daemon: the manifest exe payload)"

if [ "$signing" = none ]; then
	echo "package.sh: NOT SIGNED - no signing backend was selected. This output must not be published to a channel."
	[ -z "${GITHUB_OUTPUT:-}" ] || echo "signed=false" >>"$GITHUB_OUTPUT"
else
	[ -z "${GITHUB_OUTPUT:-}" ] || echo "signed=true" >>"$GITHUB_OUTPUT"
fi
