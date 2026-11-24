#!/bin/sh
# msi_version SEMVER -> the numeric ProductVersion Windows Installer needs.
#
#   1.4.2            -> 1.4.2
#   1.4.2-beta.3     -> 1.4.2.3     (also rc.N, alpha.N: the last number in the prerelease)
#   1.4.2-beta       -> 1.4.2
#
# Windows Installer compares only the first THREE fields and ignores the fourth, so
# the 4th is informative (it keeps beta builds distinguishable in Programs and
# Features) and does NOT order prereleases; runner.wixl.wxs therefore allows
# same-version upgrades. Limits: major and minor <= 255, build <= 65535, 4th <= 65535.
# The full semver stays in the binary stamp, the manifest and the MSI description.
msi_version() {
	_v=$1
	# x.y.z, then an optional prerelease introduced by "-" or "." (the shapes the
	# release workflow accepts).
	_core=$(printf '%s' "$_v" | sed -n 's/^\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\)\([-.].*\)\{0,1\}$/\1/p')
	if [ -z "$_core" ]; then
		echo "msi_version: '$_v' is not x.y.z[-prerelease]" >&2
		return 1
	fi
	_pre=${_v#"$_core"}
	_pre=${_pre#[-.]}
	# strip leading zeros (but keep a lone 0)
	_major=$(printf '%s' "$_core" | cut -d. -f1 | sed 's/^0*\(.\)/\1/')
	_minor=$(printf '%s' "$_core" | cut -d. -f2 | sed 's/^0*\(.\)/\1/')
	_patch=$(printf '%s' "$_core" | cut -d. -f3 | sed 's/^0*\(.\)/\1/')
	if [ "${#_major}" -gt 3 ] || [ "${#_minor}" -gt 3 ] || [ "${#_patch}" -gt 5 ] ||
		[ "$_major" -gt 255 ] || [ "$_minor" -gt 255 ] || [ "$_patch" -gt 65535 ]; then
		echo "msi_version: '$_v' is out of range (255.255.65535)" >&2
		return 1
	fi
	_out="$_major.$_minor.$_patch"
	if [ -n "$_pre" ]; then
		_n=$(printf '%s' "$_pre" | grep -Eo '[0-9]+' | tail -n 1 | sed 's/^0*\(.\)/\1/')
		if [ -n "$_n" ] && [ "$_n" != 0 ]; then
			if [ "${#_n}" -gt 5 ] || [ "$_n" -gt 65535 ]; then
				echo "msi_version: prerelease number $_n > 65535" >&2
				return 1
			fi
			_out="$_out.$_n"
		fi
	fi
	printf '%s\n' "$_out"
}
