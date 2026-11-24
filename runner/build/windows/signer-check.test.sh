#!/bin/sh
# Test signer-check.sh and require-signer-pin.sh with a throwaway CA + leaf made in a
# temp dir (never committed). Needs openssl, osslsigncode and mingw gcc, i.e. run it in
# the toolchain container:
#   runner/build/windows/run-in-container.sh -- sh runner/build/windows/signer-check.test.sh
here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
. "$here/signer-check.sh"
w=$(mktemp -d)
trap 'rm -rf "${w:?}"' EXIT
cd "$w" || exit 1
fail=0
echo 'int main(void){return 0;}' >h.c
x86_64-w64-mingw32-gcc h.c -o h.exe
openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.pem -days 2 -subj "/C=IT/O=Throwaway CA/CN=Test Root CA" 2>/dev/null
openssl req -newkey rsa:2048 -nodes -keyout leaf.key -out leaf.csr -subj "/C=IT/O=ReelBolt Test Inc/CN=ReelBolt Test Signer" 2>/dev/null
printf 'extendedKeyUsage=codeSigning\n' >ext.cnf
openssl x509 -req -in leaf.csr -CA ca.pem -CAkey ca.key -CAcreateserial -out leaf.pem -days 2 -extfile ext.cnf 2>/dev/null
openssl pkcs12 -export -inkey leaf.key -in leaf.pem -certfile ca.pem -out t.pfx -passout pass:x
osslsigncode sign -pkcs12 t.pfx -pass x -in h.exe -out s.exe >/dev/null
SUBJ="/C=IT/O=ReelBolt Test Inc/CN=ReelBolt Test Signer"
THUMB=$(openssl x509 -in leaf.pem -noout -fingerprint -sha256 | sed 's/.*=//')
res_signed=$(osslsigncode verify -in s.exe 2>&1 || true)
res_unsigned=$(osslsigncode verify -in h.exe 2>&1 || true)

t() { # name want(0|1) subject thumbprint file output
	name=$1 want=$2
	( EXPECTED_SIGNER_SUBJECT=$3 EXPECTED_SIGNER_THUMBPRINT=$4 signer_check "$5" "$6" ) >/dev/null 2>&1
	rc=$?
	if [ "$rc" = "$want" ]; then echo "PASS $name"; else echo "FAIL $name (rc=$rc want=$want)"; fail=1; fi
}
t exact-dn 0 "$SUBJ" "" s.exe "$res_signed"
t exact-dn-padded 0 "  $SUBJ  " "" s.exe "$res_signed"
t substring 1 "ReelBolt Test" "" s.exe "$res_signed"
t prefix 1 "/C=IT/O=ReelBolt Test Inc" "" s.exe "$res_signed"
t cn-only 1 "CN=ReelBolt Test Signer" "" s.exe "$res_signed"
t wrong-dn 1 "/C=IT/O=Other/CN=Other" "" s.exe "$res_signed"
t ca-subject 1 "/C=IT/O=Throwaway CA/CN=Test Root CA" "" s.exe "$res_signed"
t thumb-match 0 "" "$THUMB" s.exe "$res_signed"
t thumb-lower-nocolon 0 "" "$(printf '%s' "$THUMB" | tr -d ':' | tr 'A-F' 'a-f')" s.exe "$res_signed"
t thumb-mismatch 1 "" "$(printf '%064d' 0)" s.exe "$res_signed"
t thumb-of-ca 1 "" "$(openssl x509 -in ca.pem -noout -fingerprint -sha256 | sed 's/.*=//')" s.exe "$res_signed"
t both-match 0 "$SUBJ" "$THUMB" s.exe "$res_signed"
t dn-ok-thumb-bad 1 "$SUBJ" "$(printf '%064d' 0)" s.exe "$res_signed"
t dn-bad-thumb-ok 1 "/C=IT/O=Other/CN=Other" "$THUMB" s.exe "$res_signed"
t unsigned 1 "$SUBJ" "" h.exe "$res_unsigned"
t no-pin-passes-here 0 "" "" s.exe "$res_signed"

p() { # name want PUBLISH SIGNING subject thumb
	( PUBLISH=$3 SIGNING=$4 EXPECTED_SIGNER_SUBJECT=$5 EXPECTED_SIGNER_THUMBPRINT=$6 sh "$here/require-signer-pin.sh" ) >/dev/null 2>&1
	rc=$?
	if [ "$rc" = "$2" ]; then echo "PASS $1"; else echo "FAIL $1 (rc=$rc want=$2)"; fail=1; fi
}
p publish-no-pin-refused 1 true trusted-signing "" ""
p publish-no-pin-pfx-refused 1 true pfx "" ""
p publish-subject-ok 0 true trusted-signing "$SUBJ" ""
p publish-thumb-ok 0 true pfx "" "$THUMB"
p dryrun-no-pin-warns 0 false none "" ""
p dryrun-signing-no-pin-warns 0 false pfx "" ""
exit $fail
