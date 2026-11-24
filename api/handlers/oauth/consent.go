package oauth

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"html/template"
	"log"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/services"
)

// The consent step. `/oauth/authorize` renders a page listing what the client is asking for; the page
// submits back a single sealed value that carries the request the user actually saw. Nothing about the
// request is taken from the submitted form itself, so a page that renders one consent cannot be turned
// into a POST that grants another.

// consentRequest is the frozen authorization request: exactly the parameters that were validated on
// the GET, plus who consented and when it stops being valid. It is signed, never encrypted — nothing
// in it is a secret (the client id, redirect URI and PKCE challenge all arrived in the URL).
type consentRequest struct {
	UserID         string `json:"uid"`
	ExpiresAt      int64  `json:"exp"`
	ClientID       string `json:"client_id"`
	ClientName     string `json:"client_name"`
	RedirectURI    string `json:"redirect_uri"`
	ResponseType   string `json:"response_type"`
	RequestedScope string `json:"requested_scope"`
	// GrantedScope is what the consent page SHOWED. The POST can only ever grant a subset of it, so a
	// consent screen that said "tools only" cannot produce a token with admin on it.
	GrantedScope        string `json:"granted_scope"`
	State               string `json:"state"`
	CodeChallenge       string `json:"code_challenge"`
	CodeChallengeMethod string `json:"code_challenge_method"`
}

// sealConsent signs the request. The key is the deployment's JWT signing key, which is already
// required to be at least 32 random characters at startup and is not shared with any client, so a
// consent handle cannot be forged by anyone who can reach the endpoint.
func sealConsent(req consentRequest) (string, error) {
	payload, err := json.Marshal(req)
	if err != nil {
		return "", fmt.Errorf("encode consent: %w", err)
	}
	mac := hmac.New(sha256.New, []byte(config.Cfg.JWTSigningKey))
	mac.Write(payload)
	sig := mac.Sum(nil)

	return base64.RawURLEncoding.EncodeToString(payload) + "." + base64.RawURLEncoding.EncodeToString(sig), nil
}

// openConsent verifies the signature, refuses an expired handle, and returns the request it carries.
// The boolean is false for anything that does not verify — a tampered handle, a handle signed with a
// rotated key, or one that has simply aged out.
func openConsent(value string, now time.Time) (consentRequest, bool) {
	var req consentRequest

	payloadPart, sigPart, ok := strings.Cut(value, ".")
	if !ok {
		return req, false
	}
	payload, err := base64.RawURLEncoding.DecodeString(payloadPart)
	if err != nil {
		return req, false
	}
	sig, err := base64.RawURLEncoding.DecodeString(sigPart)
	if err != nil {
		return req, false
	}

	mac := hmac.New(sha256.New, []byte(config.Cfg.JWTSigningKey))
	mac.Write(payload)
	if !hmac.Equal(sig, mac.Sum(nil)) {
		return req, false
	}

	if err := json.Unmarshal(payload, &req); err != nil {
		return req, false
	}
	if req.ExpiresAt == 0 || now.Unix() > req.ExpiresAt {
		return req, false
	}
	return req, true
}

// consentScope is one line of the consent screen.
type consentScope struct {
	Name        string
	Description string
}

// consentScopeDescriptions is what the user is told each scope means
// (docs/design/mcp-oauth.md §3). An unknown scope can never reach here — GrantOAuthScopes has already
// dropped it — but the lookup is a map read rather than an index so a future scope without a
// description degrades to its bare name instead of panicking.
var consentScopeDescriptions = map[string]string{
	services.OAuthScopeAssistantTools: "Use the ReelBolt assistant's tools on your behalf: read your projects, workflows and files, propose changes, and run workflows.",
	services.OAuthScopeAssistantAdmin: "Use the assistant's administrative tools too: manage inference providers, read the decision-calibration report, and assign skills to your workspace's agents.",
	services.OAuthScopeRunsWatch:      "See whether your runs are waiting, running, finished or failed, and how far they have got. It cannot start runs, change anything or read your files.",
}

func describeScopes(granted []string) []consentScope {
	out := make([]consentScope, 0, len(granted))
	for _, s := range granted {
		out = append(out, consentScope{Name: s, Description: consentScopeDescriptions[s]})
	}
	return out
}

// consentTemplate is deliberately small and self-contained: it is served by the Go API on a browser
// navigation, has no client-side JavaScript, and posts one hidden value back. No third-party asset is
// referenced, so the page cannot leak the authorization request through a referrer.
var consentTemplate = template.Must(template.New("consent").Parse(`<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<title>Authorize {{.ClientName}} - ReelBolt</title>
<style>
  /* One accent (the brand violet), one radius scale, one type system. Light and dark are both
     first-class and the system preference decides. No third-party asset is referenced anywhere on
     this page, so the authorization request cannot leak through a referrer. */
  :root {
    color-scheme: light dark;
    --bg: #f5f5f7;
    --glow-a: rgba(109, 74, 255, 0.10);
    --glow-b: rgba(109, 74, 255, 0.05);
    --shell: rgba(17, 17, 22, 0.035);
    --shell-line: rgba(17, 17, 22, 0.07);
    --core: #ffffff;
    --core-line: rgba(17, 17, 22, 0.07);
    --inset: rgba(255, 255, 255, 0.9);
    --text: #17171c;
    --muted: #55555f;
    --faint: #74747f;
    --accent: #5b3ee8;
    --accent-ink: #ffffff;
    --accent-soft: rgba(91, 62, 232, 0.09);
    --warn-bg: #fff8e6;
    --warn-line: #e6c46a;
    --warn-ink: #6a4c00;
    --focus: #5b3ee8;
    --r-shell: 22px;
    --r-core: 15px;
    --r-control: 11px;
    --ease: cubic-bezier(0.32, 0.72, 0, 1);
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #0a0a0d;
      --glow-a: rgba(139, 108, 255, 0.16);
      --glow-b: rgba(139, 108, 255, 0.06);
      --shell: rgba(255, 255, 255, 0.045);
      --shell-line: rgba(255, 255, 255, 0.09);
      --core: #14141a;
      --core-line: rgba(255, 255, 255, 0.08);
      --inset: rgba(255, 255, 255, 0.06);
      --text: #f2f2f5;
      --muted: #a6a6b4;
      --faint: #8c8c9a;
      --accent: #9d86ff;
      --accent-ink: #14101f;
      --accent-soft: rgba(157, 134, 255, 0.12);
      --warn-bg: rgba(255, 199, 71, 0.10);
      --warn-line: rgba(255, 199, 71, 0.34);
      --warn-ink: #ffd98a;
      --focus: #a78bfa;
    }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    min-height: 100dvh;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 2rem 1rem;
    background:
      radial-gradient(60rem 30rem at 50% -10%, var(--glow-a), transparent 62%),
      radial-gradient(40rem 24rem at 50% 110%, var(--glow-b), transparent 60%),
      var(--bg);
    color: var(--text);
    font-family: ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI Variable Text", "Segoe UI", sans-serif;
    font-size: 16px;
    line-height: 1.55;
    -webkit-font-smoothing: antialiased;
  }
  /* Double-bezel: an outer shell holds an inner core, so the card reads as one machined object
     rather than a rectangle floating on a background. */
  .shell {
    width: 100%;
    max-width: 30rem;
    padding: 6px;
    background: var(--shell);
    border: 1px solid var(--shell-line);
    border-radius: var(--r-shell);
  }
  .core {
    background: var(--core);
    border: 1px solid var(--core-line);
    border-radius: var(--r-core);
    box-shadow: inset 0 1px 0 var(--inset);
    padding: 1.75rem 1.75rem 1.5rem;
  }
  .wordmark {
    font-size: .78rem;
    font-weight: 650;
    letter-spacing: .01em;
    color: var(--faint);
  }
  .wordmark b { color: var(--text); font-weight: 700; }
  h1 {
    margin: .85rem 0 .4rem;
    font-size: 1.4rem;
    font-weight: 620;
    letter-spacing: -.021em;
    line-height: 1.25;
  }
  .lede { margin: 0; color: var(--muted); font-size: .9375rem; }
  .label {
    margin: 0 0 .5rem;
    font-size: .6875rem;
    font-weight: 600;
    letter-spacing: .1em;
    text-transform: uppercase;
    color: var(--faint);
  }
  /* The workspace is the star of this page: the token it grants is scoped to exactly one org. */
  .org {
    margin: 1.5rem 0 0;
    padding: .95rem 1.05rem;
    background: var(--accent-soft);
    border: 1px solid var(--core-line);
    border-radius: 13px;
  }
  .org-name {
    margin: 0;
    font-size: 1.0625rem;
    font-weight: 620;
    letter-spacing: -.012em;
    word-break: break-word;
  }
  .org-meta {
    margin: .45rem 0 0;
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: .5rem .6rem;
    font-size: .8125rem;
    color: var(--muted);
  }
  .chip {
    padding: .15rem .5rem;
    border: 1px solid var(--core-line);
    border-radius: 999px;
    background: var(--core);
    font-size: .75rem;
    color: var(--text);
  }
  .grants { margin: 1.5rem 0 0; }
  .grants-intro { margin: 0 0 .5rem; color: var(--muted); font-size: .875rem; }
  ul { list-style: none; margin: 0; padding: 0; }
  li { padding: .7rem 0; border-top: 1px solid var(--core-line); }
  li:first-child { border-top: 0; padding-top: 0; }
  .scope {
    font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace;
    font-size: .8125rem;
    color: var(--accent);
  }
  li p { margin: .2rem 0 0; font-size: .875rem; color: var(--muted); }
  .notice {
    margin: 1.25rem 0 0;
    padding: .8rem .9rem;
    border: 1px solid var(--warn-line);
    border-radius: 12px;
    background: var(--warn-bg);
    color: var(--warn-ink);
    font-size: .875rem;
  }
  .notice strong { font-weight: 650; }
  form { margin: 1.5rem 0 0; }
  .actions { display: flex; gap: .6rem; }
  button {
    font: inherit;
    font-weight: 600;
    padding: .7rem 1.1rem;
    border-radius: var(--r-control);
    border: 1px solid transparent;
    cursor: pointer;
    transition: transform .18s var(--ease), background-color .18s var(--ease), border-color .18s var(--ease);
  }
  button:active { transform: translateY(1px); }
  button:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
  .allow { flex: 1; background: var(--accent); color: var(--accent-ink); }
  .allow:hover { filter: brightness(1.06); }
  .deny { background: transparent; color: var(--muted); border-color: var(--shell-line); }
  .deny:hover { color: var(--text); border-color: var(--faint); }
  .foot { margin: 1.15rem 0 0; font-size: .8125rem; color: var(--faint); }
  /* ONE fade of the whole card, deliberately NOT a per-element stagger. This page's only job is to be
     read and then answered, so no element may sit invisible while its neighbour is already on screen,
     and a stagger makes the very first paint look broken. */
  @media (prefers-reduced-motion: no-preference) {
    .shell { animation: settle .22s var(--ease) both; }
    @keyframes settle {
      from { opacity: 0; transform: translateY(4px); }
      to { opacity: 1; transform: none; }
    }
  }
</style>
</head>
<body>
<main class="shell">
  <div class="core">
    <p class="wordmark"><b>ReelBolt</b></p>

    <h1>Connect {{.ClientName}}</h1>
    <p class="lede">This application is asking to connect to ReelBolt. It will act as you, and everything it does is recorded against your account.</p>

    <section class="org" aria-labelledby="org-label">
      <p class="label" id="org-label">Authorizing as</p>
      <p class="org-name">{{.Org.Name}}</p>
      <p class="org-meta">
        <span class="chip">{{.Org.KindLabel}}</span>
        <span>Your role: {{.Org.RoleLabel}}</span>
      </p>
    </section>

    <section class="grants">
      <p class="grants-intro">It will be able to:</p>
      <ul>
        {{range .Scopes}}
        <li>
          <span class="scope">{{.Name}}</span>{{if .IsAdmin}} <span class="chip">Admin</span>{{end}}
          <p>{{.Description}}</p>
        </li>
        {{end}}
      </ul>
    </section>

    {{if .AdminGranted}}
    <p class="notice"><strong>This includes administrative access.</strong> You switched admin tools on for MCP in your settings, so this application can also change your workspace's agents and, on a deployment you administer, its inference providers.</p>
    {{else if .AdminRequested}}
    <p class="notice">It also asked for administrative access, which is <strong>not</strong> being granted: admin tools are switched off in your settings.</p>
    {{end}}

    <form method="post" action="/oauth/authorize">
      <input type="hidden" name="consent" value="{{.Consent}}">
      <div class="actions">
        <button class="allow" type="submit" name="decision" value="allow">Allow</button>
        <button class="deny" type="submit" name="decision" value="deny">Deny</button>
      </div>
    </form>

    <p class="foot">Signed in as {{.UserEmail}}. Approving sends you back to {{.RedirectHost}}. You can revoke this access at any time in your ReelBolt settings.</p>
  </div>
</main>
</body>
</html>
`))

// consentOrg is the workspace the authorization will be scoped to, as the page shows it. The token a
// consent produces is bound to exactly one organization, so a person who belongs to more than one has
// to be able to see WHICH one they are connecting before they approve (docs/design/mcp-oauth.md §3).
// The zero value is valid and renders neutrally, which is what a lookup we could not complete leaves.
type consentOrg struct {
	Name string
	Kind string
	Role string
}

// KindLabel names the workspace kind in words a person reads, without a second source of truth to
// keep in step with the model's constants.
func (o consentOrg) KindLabel() string {
	if o.Kind == "" {
		return "Workspace"
	}
	return o.Kind + " workspace"
}

// RoleLabel is the caller's role in that workspace, or plain "unknown" when the lookup did not
// complete. Never a guess.
func (o consentOrg) RoleLabel() string {
	if o.Role == "" {
		return "unknown"
	}
	return o.Role
}

// consentPage is the template's data.
type consentPage struct {
	ClientName   string
	UserEmail    string
	RedirectHost string
	Org          consentOrg
	Scopes       []consentPageScope
	AdminGranted bool
	// AdminRequested is true when the client asked for admin but is not being granted it, so the page
	// can say so rather than silently showing a shorter list.
	AdminRequested bool
	Consent        string
}

type consentPageScope struct {
	Name        string
	Description string
	IsAdmin     bool
}

// renderConsent writes the consent screen. A template failure is a 500: the page is the only place the
// request can be approved, so rendering a half page would offer a button that grants the wrong thing.
func renderConsent(w http.ResponseWriter, req consentRequest, sealed, userEmail string, org consentOrg, granted []string, adminRequested bool) {
	scopes := describeScopes(granted)
	pageScopes := make([]consentPageScope, 0, len(scopes))
	adminGranted := false
	for _, s := range scopes {
		isAdmin := s.Name == services.OAuthScopeAssistantAdmin
		if isAdmin {
			adminGranted = true
		}
		pageScopes = append(pageScopes, consentPageScope{Name: s.Name, Description: s.Description, IsAdmin: isAdmin})
	}

	page := consentPage{
		ClientName:     req.ClientName,
		UserEmail:      userEmail,
		RedirectHost:   redirectHost(req.RedirectURI),
		Org:            org,
		Scopes:         pageScopes,
		AdminGranted:   adminGranted,
		AdminRequested: adminRequested && !adminGranted,
		Consent:        sealed,
	}

	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Referrer-Policy", "no-referrer")
	if err := consentTemplate.Execute(w, page); err != nil {
		// The header is already written; nothing useful can be added to the body, so this only
		// records the failure. It cannot be returned to the client as a JSON error at this point.
		log.Printf("oauth consent: render consent page: %v", err)
	}
}

// redirectHost is the host of a validated redirect URI, for display. The URI itself is already known
// to be an exact match against the registered value; this is only what the page shows.
func redirectHost(redirectURI string) string {
	u, err := url.Parse(redirectURI)
	if err != nil || u.Host == "" {
		return redirectURI
	}
	return u.Host
}
