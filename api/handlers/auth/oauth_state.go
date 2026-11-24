package auth

import (
	"encoding/base64"
	"encoding/json"
	"net/url"
	"strings"

	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/services"
)

// What a sign-in started from a landing page carries through the provider round trip (new-user
// funnel, docs/design/new-user-funnel.md section 6).
//
// A password signup sends the landing page's persona and the campaign parameters in its request
// body. A provider sign-in has no body: the browser leaves for Google or GitHub and comes back on
// the callback. So the start endpoint accepts the same values as query parameters and packs them
// into the OAuth `state` parameter, which the provider echoes back untouched:
//
//	state = <random>                           nothing to carry (exactly as before)
//	state = <random>.<base64url(JSON)>         persona, campaign parameters, landing path
//
// Nothing is stored on the device for this: the state cookie still holds only the provider, the
// random part and the PKCE verifier, and the callback compares the random part with the cookie
// exactly as it always has. The suffix is not secret and not signed, and does not need to be. It can
// only be changed by whoever drives the browser through the flow, and all it can ever say is which
// closed-set persona to suggest and which sanitised campaign labels and landing path to record on
// that person's own new account. It is sanitised on the way out (only closed tokens leave for the provider) and again
// on the way back in, and a suffix that is missing, oversized or garbage is ignored: it can never
// fail a sign-in.

// maxOAuthCarryChars bounds the encoded suffix. The longest persona, five 64-character campaign
// values and a 120-character landing path encode to under 700 characters, so anything longer was
// not written by handleOAuthStart. With the 64-character random part in front, the whole state
// stays under 850 characters, well inside what an authorization URL can hold.
const maxOAuthCarryChars = 768

// oauthLandingParam is the query parameter that names the page of ours the visit began on. The
// marketing site and the dashboard put it on the links they build (web/lib/auth/signup-carry.ts,
// LANDING_PARAM); the signup body sends the same value as attribution.landingPath.
const oauthLandingParam = "lp"

// oauthCarrySeparator splits the random part from the suffix. The random part is hex and the
// suffix is unpadded base64url, so neither can contain it.
const oauthCarrySeparator = "."

// oauthCarry is the JSON inside the suffix. The keys follow the rb_ft cookie's (s, m, c, n, t and l,
// the landing path); p is the persona. v is the format version: the callback reads only v == 1.
type oauthCarry struct {
	V int             `json:"v"`
	P marketingString `json:"p,omitempty"`
	S marketingString `json:"s,omitempty"`
	M marketingString `json:"m,omitempty"`
	C marketingString `json:"c,omitempty"`
	N marketingString `json:"n,omitempty"`
	T marketingString `json:"t,omitempty"`
	L marketingString `json:"l,omitempty"`
}

// oauthCarryFromQuery reads `for`, the five utm_* parameters and `lp` (the landing path) of the
// start request and keeps what survives sanitising. An unknown persona or an invalid value is
// dropped, never refused. The landing path goes through the same sanitiser as the signup body's
// attribution.landingPath: lowercased, cut at its query, a path of ours and nothing else.
//
// The campaign parameters and the landing path are carried only while
// ATTRIBUTION_URL_CAPTURE_ENABLED is on. Off (the default) the callback would not record them, so
// they are not sent through the provider at all: the state then carries the persona alone, or
// nothing.
func oauthCarryFromQuery(q url.Values) signupMarketing {
	persona, _ := services.NormalizePersona(q.Get("for"))
	mk := signupMarketing{persona: persona}
	if config.Cfg.AttributionURLCaptureEnabled {
		mk.carried = services.URLAttribution{
			Source:      q.Get("utm_source"),
			Medium:      q.Get("utm_medium"),
			Campaign:    q.Get("utm_campaign"),
			Content:     q.Get("utm_content"),
			Term:        q.Get("utm_term"),
			LandingPath: q.Get(oauthLandingParam),
		}.Sanitised()
	}
	return mk
}

// encodeOAuthCarry is the state suffix for mk, or "" when there is nothing to carry (the state is
// then the bare random value) or the result would break the size bound.
func encodeOAuthCarry(mk signupMarketing) string {
	persona, _ := services.NormalizePersona(mk.persona)
	a := mk.carried.Sanitised()
	if persona == "" && a.IsZero() {
		return ""
	}
	raw, err := json.Marshal(oauthCarry{
		V: 1, P: marketingString(persona),
		S: marketingString(a.Source), M: marketingString(a.Medium), C: marketingString(a.Campaign),
		N: marketingString(a.Content), T: marketingString(a.Term), L: marketingString(a.LandingPath),
	})
	if err != nil {
		return ""
	}
	enc := base64.RawURLEncoding.EncodeToString(raw)
	if len(enc) > maxOAuthCarryChars {
		return ""
	}
	return enc
}

// splitOAuthState separates the random part of a returned state from its suffix. A state with no
// separator is all random part, which is every state minted before the suffix existed and every
// state of a flow that carries nothing.
func splitOAuthState(state string) (nonce, suffix string) {
	nonce, suffix, _ = strings.Cut(state, oauthCarrySeparator)
	return nonce, suffix
}

// decodeOAuthCarry reads a state suffix defensively. Anything it does not understand (too long, not
// base64url, not JSON, another version) is the zero value: the sign-in goes on with nothing carried.
// What it returns is still raw; the recorders sanitise it again before anything is written.
func decodeOAuthCarry(suffix string) signupMarketing {
	if suffix == "" || len(suffix) > maxOAuthCarryChars {
		return signupMarketing{}
	}
	raw, err := base64.RawURLEncoding.DecodeString(suffix)
	if err != nil {
		return signupMarketing{}
	}
	var c oauthCarry
	if json.Unmarshal(raw, &c) != nil || c.V != 1 {
		return signupMarketing{}
	}
	return signupMarketing{
		persona: string(c.P),
		carried: services.URLAttribution{
			Source: string(c.S), Medium: string(c.M), Campaign: string(c.C),
			Content: string(c.N), Term: string(c.T), LandingPath: string(c.L),
		},
	}
}

// oauthCarryLoginQuery adds what a sign-in carried to the query of the login page a FAILED sign-in
// is sent back to, as the same parameters the web pages read (`for`, utm_* and `lp`).
//
// The carry lives only in the URL (nothing is stored on the device), so a redirect without it loses
// it for good: a person who pressed Cancel at the provider and then signed up with email would get
// no suggested persona and no campaign on the account. Putting it back on the login URL lets the
// login and signup pages pass it on exactly as if the person had never left.
//
// The same rules as on the way out apply. Only what survives sanitising is written: a closed-set
// persona, campaign labels of the closed pattern and a landing path of the closed pattern (it
// starts with one slash and holds no host, query or fragment), never a raw value. The campaign
// labels and the landing path are written only while ATTRIBUTION_URL_CAPTURE_ENABLED is on, as
// oauthCarryFromQuery packs them only then. The values come from the unsigned state suffix of the callback URL, so anybody can make
// this redirect say `for=creator`; that is no more than linking to /app/login?for=creator directly.
func oauthCarryLoginQuery(q url.Values, mk signupMarketing) {
	if persona, ok := services.NormalizePersona(mk.persona); ok {
		q.Set("for", persona)
	}
	if !config.Cfg.AttributionURLCaptureEnabled {
		return
	}
	a := mk.carried.Sanitised()
	for _, kv := range [...]struct{ key, value string }{
		{"utm_source", a.Source}, {"utm_medium", a.Medium}, {"utm_campaign", a.Campaign},
		{"utm_content", a.Content}, {"utm_term", a.Term}, {oauthLandingParam, a.LandingPath},
	} {
		if kv.value != "" {
			q.Set(kv.key, kv.value)
		}
	}
}
