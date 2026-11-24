// njs script for cookie-based JWT auth translation

function getCookie(r, name) {
    var cookies = r.headersIn['Cookie'];
    if (!cookies) return '';
    var parts = cookies.split(';');
    for (var i = 0; i < parts.length; i++) {
        var pair = parts[i].trim().split('=');
        if (pair[0] === name) {
            return pair.slice(1).join('=');
        }
    }
    return '';
}

// Called via js_set to provide the Authorization header value
//
// Browsers authenticate with the httpOnly `reelbolt_token` cookie, which this
// translates into a bearer token. Non-browser clients cannot hold that cookie:
// the platform CLI and the MCP server (which now exposes the assistant's tools
// to Claude Code) send `Authorization: Bearer <jwt>` directly. Returning '' for
// those would be worse than useless — every `/api/v1/` location does
// `proxy_set_header Authorization $auth_header`, so an empty value OVERWRITES
// and blanks the client's own header, turning a valid token into a 401.
//
// So pass an incoming Authorization header through unchanged. This grants
// nothing on its own: the Inference API validates the JWT signature, issuer and
// audience itself (and the Go API does the same), so an unsigned or foreign
// token is rejected exactly as before. The cookie stays the primary path for
// browsers.
function getAuthHeader(r) {
    var token = getCookie(r, 'reelbolt_token');
    if (token) {
        return 'Bearer ' + token;
    }
    var incoming = r.headersIn['Authorization'];
    if (incoming) {
        return incoming;
    }
    return '';
}

// Handle logout: clear cookies and return 200
function handleLogout(r) {
    var secure = r.variables.scheme === 'https' ? ' Secure;' : '';
    r.headersOut['Set-Cookie'] = [
        'reelbolt_token=; Path=/; HttpOnly;' + secure + ' SameSite=Lax; Max-Age=0',
        'reelbolt_user=; Path=/;' + secure + ' SameSite=Lax; Max-Age=0'
    ];
    r.headersOut['Content-Type'] = 'application/json';
    r.return(200, '{"ok":true}');
}

// Fixed-time comparison of two strings of the same length. The lengths themselves are compared
// first: the length of the token is not the secret.
function sameSecret(a, b) {
    if (a.length !== b.length) return false;
    var diff = 0;
    for (var i = 0; i < a.length; i++) {
        diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
    }
    return diff === 0;
}

// Called via js_set ($site_relay_token_ok, nginx.conf): '1' when the request carries the marketing
// site's relay token, '' otherwise.
//
// The token is SITE_FORWARD_TOKEN, the secret the site (and its Cloudflare Pages Functions) already
// shares with the Inference API. nginx reads it from its own environment (`env SITE_FORWARD_TOKEN;`
// in nginx.conf). It decides one thing only: which rate-limit zone a tracked-link click falls in
// (link_relay for the site's relay, link_redirect for everybody else). It grants no access to
// anything, and a wrong, missing or unconfigured token is never an error: the request is simply an
// ordinary one. Unset or empty on this side means nobody is the relay.
function siteRelayVerified(r) {
    var expected = process.env.SITE_FORWARD_TOKEN;
    var sent = r.headersIn['X-ReelBolt-Site-Token'];
    if (!expected || !sent) return '';
    return sameSecret(sent, expected) ? '1' : '';
}

export default { getAuthHeader, handleLogout, siteRelayVerified };
