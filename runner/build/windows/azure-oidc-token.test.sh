#!/bin/sh
# Test azure-oidc-token.sh against a local mock of the GitHub OIDC endpoint and Entra.
set -eu
here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
work=$(mktemp -d)
trap 'kill "${pid:-0}" 2>/dev/null || true; rm -rf "${work:?}"' EXIT
cat >"$work/mock.py" <<'PY'
import http.server, sys, urllib.parse, json
mode = sys.argv[2]
class H(http.server.BaseHTTPRequestHandler):
    def reply(self, code, obj):
        b = json.dumps(obj).encode()
        self.send_response(code); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        if self.headers.get("Authorization") != "Bearer reqtok" or "audience=api://AzureADTokenExchange" not in self.path:
            return self.reply(401, {"message": "bad"})
        if mode == "oidc-empty": return self.reply(200, {})
        self.reply(200, {"value": "gh.jwt.value"})
    def do_POST(self):
        body = urllib.parse.parse_qs(self.rfile.read(int(self.headers["Content-Length"])).decode())
        ok = (self.path == "/tenant-1/oauth2/v2.0/token" and body.get("client_id") == ["client-1"]
              and body.get("client_assertion") == ["gh.jwt.value"] and body.get("scope") == ["https://codesigning.azure.net/.default"]
              and body.get("grant_type") == ["client_credentials"])
        if not ok or mode == "entra-deny":
            return self.reply(401, {"error": "invalid_client", "error_description": "AADSTS700016 nope"})
        if mode == "entra-empty": return self.reply(200, {})
        self.reply(200, {"access_token": "entra-secret-token", "expires_in": 3600})
    def log_message(self, *a): pass
http.server.HTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
PY
fail=0
check() { # name expected-exit mode extra-env...
	name=$1 want=$2 mode=$3; shift 3
	port=$((20000 + $$ % 20000))
	python3 "$work/mock.py" "$port" "$mode" & pid=$!
	sleep 0.5
	: >"$work/env"; rm -f "$work/tok"
	set +e
	out=$(env ACTIONS_ID_TOKEN_REQUEST_URL="http://127.0.0.1:$port/oidc?x=1" ACTIONS_ID_TOKEN_REQUEST_TOKEN=reqtok \
		AZURE_TENANT_ID=tenant-1 AZURE_CLIENT_ID=client-1 AZURE_AUTHORITY_HOST="http://127.0.0.1:$port" "$@" \
		sh "$here/azure-oidc-token.sh" --env-file "$work/env" --out "$work/tok" 2>&1)
	rc=$?
	set -e
	kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true
	got=ok
	[ "$rc" = "$want" ] || got="exit $rc, wanted $want"
	if [ "$want" = 0 ]; then
		grep -qx 'AZURE_TRUSTED_SIGNING_TOKEN=entra-secret-token' "$work/env" || got="env file wrong"
		[ "$(stat -c %a "$work/tok")" = 600 ] || got="token file mode not 0600"
		printf '%s' "$out" | grep -q 'add-mask::entra-secret-token' || got="token not masked"
		printf '%s' "$out" | grep -v 'add-mask' | grep -q 'entra-secret-token' && got="token printed"
	else
		[ ! -s "$work/env" ] || got="token written on failure"
	fi
	if [ "$got" = ok ]; then echo "PASS $name"; else echo "FAIL $name: $got"; fail=1; fi
}
check success 0 ok
check oidc-endpoint-refuses 1 ok ACTIONS_ID_TOKEN_REQUEST_TOKEN=wrong
check oidc-response-empty 1 oidc-empty
check entra-denies 1 entra-deny
check entra-no-token 1 entra-empty
check wrong-client 1 ok AZURE_CLIENT_ID=other
check missing-tenant 1 ok AZURE_TENANT_ID=
check non-https-authority 1 ok AZURE_AUTHORITY_HOST=http://evil.example
exit $fail
