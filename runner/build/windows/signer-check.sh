#!/bin/sh
# signer_check FILE VERIFY_OUTPUT: pin WHO signed a Windows file.
#
# Environment (each one that is set must match; neither set = no pin, see below)
#   EXPECTED_SIGNER_SUBJECT     the signer (leaf) certificate's FULL subject DN, EXACTLY
#                               as `osslsigncode verify -in <signed.exe>` prints it after
#                               "Signer's certificate: ... Subject:", e.g.
#                               /C=US/ST=Washington/O=ReelBolt Inc/CN=ReelBolt Inc
#                               (leading/trailing blanks are ignored; nothing else is:
#                               no substring, no prefix, no case folding)
#   EXPECTED_SIGNER_THUMBPRINT  SHA-256 of the signer (leaf) certificate, hex, case and
#                               colons ignored. For PFX signing, where the certificate
#                               is fixed. Trusted Signing certificates rotate, so pin
#                               the subject there.
# The signer is the leaf certificate: a CA's subject in the chain never satisfies the pin.
# Read the values from a signed file with:
#   osslsigncode verify -in file.exe | sed -n "/Signer's certificate/,/Issuer/p"
#   osslsigncode extract-signature -in file.exe -out s.der &&
#     openssl pkcs7 -inform DER -in s.der -print_certs |
#     openssl x509 -noout -fingerprint -sha256     # (first cert is the leaf in most chains;
#                                                  #  check its subject against the above)

signer_subject() { # VERIFY_OUTPUT
	printf '%s\n' "$1" | awk '/Signer.s certificate:/ {f=1} f && /Subject:/ {sub(/.*Subject: */, ""); print; exit}'
}

# signer_thumbprints FILE SUBJECT: SHA-256 of every certificate embedded in FILE's
# signature whose subject equals SUBJECT (normally exactly the leaf).
signer_thumbprints() {
	_t=$(mktemp -d)
	osslsigncode extract-signature -in "$1" -out "$_t/sig.der" >/dev/null 2>&1 || { rm -rf "${_t:?}"; return 1; }
	openssl pkcs7 -inform DER -in "$_t/sig.der" -print_certs 2>/dev/null |
		awk -v d="$_t" '/-----BEGIN CERTIFICATE-----/ {n++; f=sprintf("%s/c%02d.pem", d, n)} f {print > f} /-----END CERTIFICATE-----/ {close(f); f=""}'
	for _c in "$_t"/c*.pem; do
		[ -f "$_c" ] || continue
		_s=$(openssl x509 -in "$_c" -noout -subject -nameopt compat 2>/dev/null | sed 's/^subject= *//')
		if [ "$_s" = "$2" ]; then
			openssl x509 -in "$_c" -noout -fingerprint -sha256 | sed 's/.*=//' | tr -d ':' | tr 'A-F' 'a-f'
		fi
	done
	rm -rf "${_t:?}"
}

# Returns 0 when no pin is configured or every configured pin matches.
signer_check() { # FILE VERIFY_OUTPUT
	_f=$1
	_want_subject=$(printf '%s' "${EXPECTED_SIGNER_SUBJECT:-}" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
	_want_thumb=$(printf '%s' "${EXPECTED_SIGNER_THUMBPRINT:-}" | tr -d ': \t' | tr 'A-F' 'a-f')
	[ -n "$_want_subject$_want_thumb" ] || return 0
	_subject=$(signer_subject "$2" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
	if [ -n "$_want_subject" ] && [ "$_subject" != "$_want_subject" ]; then
		echo "signer-check: $_f is signed by '$_subject', which is not exactly the pinned subject '$_want_subject'" >&2
		return 1
	fi
	if [ -n "$_want_thumb" ]; then
		_have=$(signer_thumbprints "$_f" "$_subject")
		case "
$_have
" in
		*"
$_want_thumb
"*) ;;
		*)
			echo "signer-check: $_f's signer certificate (subject '$_subject') does not have the pinned SHA-256 thumbprint" >&2
			return 1
			;;
		esac
	fi
	return 0
}
