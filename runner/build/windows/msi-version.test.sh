#!/bin/sh
# Shell test for msi-version.sh.
here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
. "$here/msi-version.sh"
fail=0
ok() { # input expected
	got=$(msi_version "$1" 2>/dev/null) || got="ERR"
	if [ "$got" = "$2" ]; then echo "PASS $1 -> $2"; else echo "FAIL $1 -> $got (want $2)"; fail=1; fi
}
ok 1.4.2 1.4.2
ok 0.0.0 0.0.0
ok 1.4.2-beta.1 1.4.2.1
ok 1.4.2-beta.12 1.4.2.12
ok 0.0.0-qa 0.0.0
ok 0.0.0-rehearsal 0.0.0
ok 1.4.2-rc.2 1.4.2.2
ok 1.4.2.beta.3 1.4.2.3
ok 1.4.2.7 1.4.2.7
ok 01.04.002 1.4.2
ok 255.255.65535 255.255.65535
ok 256.0.0 ERR
ok 1.2.65536 ERR
ok 1.4.2-beta.70000 ERR
ok 1.4 ERR
ok abc ERR
exit $fail
