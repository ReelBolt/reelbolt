#!/bin/sh
# Exchange this job's GitHub OIDC token for an Entra ID access token for Azure
# Trusted Signing, with curl and sh only (no az CLI, no azure/login, no sudo).
#
#   azure-oidc-token.sh --env-file "$GITHUB_ENV"     # appends AZURE_TRUSTED_SIGNING_TOKEN=...
#   azure-oidc-token.sh --out /path/token            # writes the bare token, mode 0600
#
# Environment
#   ACTIONS_ID_TOKEN_REQUEST_URL / ACTIONS_ID_TOKEN_REQUEST_TOKEN  set by GitHub when
#                                  the job has `id-token: write`
#   AZURE_TENANT_ID, AZURE_CLIENT_ID   the federated app registration (public ids)
#   AZURE_SCOPE                    default https://codesigning.azure.net/.default
#   AZURE_AUTHORITY_HOST           default https://login.microsoftonline.com (http only
#                                  for localhost, for the mock-endpoint test)
#
# Flow: GET <request url>&audience=api://AzureADTokenExchange with the request
# token -> OIDC JWT; POST it to <authority>/<tenant>/oauth2/v2.0/token as a
# client_assertion (client_credentials grant) -> access_token. The token is masked
# with ::add-mask:: BEFORE it is written anywhere and is never in argv (curl reads
# headers and bodies from a 0600 temp file), never echoed. Nothing is stored in
# the environment of this script beyond the shell variable.
set -eu

env_file=""
out=""
while [ $# -gt 0 ]; do
	case "$1" in
	--env-file) env_file=${2:-}; shift 2 ;;
	--out) out=${2:-}; shift 2 ;;
	*) echo "azure-oidc-token.sh: unknown argument $1" >&2; exit 2 ;;
	esac
done
if [ -z "$env_file" ] && [ -z "$out" ]; then
	echo "azure-oidc-token.sh: give --env-file or --out" >&2
	exit 2
fi
for v in ACTIONS_ID_TOKEN_REQUEST_URL ACTIONS_ID_TOKEN_REQUEST_TOKEN AZURE_TENANT_ID AZURE_CLIENT_ID; do
	eval "val=\${$v:-}"
	if [ -z "$val" ]; then
		echo "::error::azure-oidc-token.sh: $v is not set (the job needs id-token: write, and the tenant/client variables)"
		exit 1
	fi
done
scope=${AZURE_SCOPE:-https://codesigning.azure.net/.default}
authority=${AZURE_AUTHORITY_HOST:-https://login.microsoftonline.com}
authority=${authority%/}
case "$authority" in
https://*) ;;
http://localhost* | http://127.0.0.1*) ;;
*) echo "::error::azure-oidc-token.sh: AZURE_AUTHORITY_HOST must be https" ; exit 1 ;;
esac
case "$AZURE_TENANT_ID$AZURE_CLIENT_ID" in
*[!A-Za-z0-9._-]*) echo "::error::azure-oidc-token.sh: tenant/client id has unexpected characters"; exit 1 ;;
esac

umask 077
tmp=$(mktemp -d)
trap 'rm -rf "${tmp:?}"' EXIT INT TERM

# extract "key":"value" from a flat JSON response (JWTs contain no quotes).
jget() { sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\\([^\"]*\\)\".*/\\1/p" | head -n 1; }

# 1. the GitHub OIDC token. The bearer goes through a curl config file, not argv.
sep='?'
case "$ACTIONS_ID_TOKEN_REQUEST_URL" in *\?*) sep='&' ;; esac
{
	printf 'url = "%s%saudience=api://AzureADTokenExchange"\n' "$ACTIONS_ID_TOKEN_REQUEST_URL" "$sep"
	printf 'header = "Authorization: Bearer %s"\n' "$ACTIONS_ID_TOKEN_REQUEST_TOKEN"
} >"$tmp/oidc.cfg"
if ! curl -fsS --retry 2 -K "$tmp/oidc.cfg" -o "$tmp/oidc.json" 2>"$tmp/err"; then
	echo "::error::azure-oidc-token.sh: could not get the GitHub OIDC token: $(head -c 200 "$tmp/err")"
	exit 1
fi
jwt=$(jget value <"$tmp/oidc.json")
if [ -z "$jwt" ]; then
	echo "::error::azure-oidc-token.sh: the GitHub OIDC response had no token"
	exit 1
fi
echo "::add-mask::$jwt"

# 2. the Entra token.
{
	printf 'url = "%s/%s/oauth2/v2.0/token"\n' "$authority" "$AZURE_TENANT_ID"
	printf 'data-urlencode = "client_id=%s"\n' "$AZURE_CLIENT_ID"
	printf 'data-urlencode = "scope=%s"\n' "$scope"
	printf 'data-urlencode = "grant_type=client_credentials"\n'
	printf 'data-urlencode = "client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer"\n'
	printf 'data-urlencode = "client_assertion=%s"\n' "$jwt"
} >"$tmp/token.cfg"
# Not -f: Entra reports the reason in a JSON body with a 4xx status.
status=$(curl -sS --retry 2 -K "$tmp/token.cfg" -o "$tmp/token.json" -w '%{http_code}' 2>"$tmp/err") || {
	echo "::error::azure-oidc-token.sh: the token request failed: $(head -c 200 "$tmp/err")"
	exit 1
}
token=$(jget access_token <"$tmp/token.json")
if [ "$status" != 200 ] || [ -z "$token" ]; then
	# error / error_description are safe to show; the body can echo nothing secret.
	echo "::error::azure-oidc-token.sh: Entra refused the exchange (HTTP $status): $(jget error <"$tmp/token.json") $(jget error_description <"$tmp/token.json" | head -c 300)"
	exit 1
fi
echo "::add-mask::$token"

if [ -n "$env_file" ]; then
	printf 'AZURE_TRUSTED_SIGNING_TOKEN=%s\n' "$token" >>"$env_file"
fi
if [ -n "$out" ]; then
	printf '%s' "$token" >"$out"
	chmod 0600 "$out"
fi
echo "azure-oidc-token.sh: got a Trusted Signing token (not shown)"
