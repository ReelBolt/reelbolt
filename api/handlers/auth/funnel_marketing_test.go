package auth

import (
	"context"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"
)

// The new-user funnel's two account-creation writes (docs/design/new-user-funnel.md section 6):
// the persona a landing page suggested, and the campaign parameters the signup link carried. Both
// belong to a NEW account only, both are sanitised and dropped rather than refused, and neither may
// change what the enumeration-safe signup answers.

// dummyDB installs a database handle that is never connected to. The best-effort writers are no-ops
// on a nil handle, so a test that observes them needs one; the inserts themselves are replaced.
func dummyDB(t *testing.T) {
	t.Helper()
	sqlDB, err := sql.Open("pgx", "postgres://u:p@127.0.0.1:1/x?sslmode=disable&connect_timeout=1")
	if err != nil {
		t.Fatal(err)
	}
	db, err := gorm.Open(postgres.New(postgres.Config{Conn: sqlDB, WithoutQuotingCheck: true}),
		&gorm.Config{SkipDefaultTransaction: true, DisableAutomaticPing: true})
	if err != nil {
		t.Fatal(err)
	}
	old := database.DB
	database.DB = db
	t.Cleanup(func() { database.DB = old })
}

// observePersonas captures attempted user_onboarding_profiles inserts.
func observePersonas(t *testing.T) *[]*models.UserOnboardingProfile {
	t.Helper()
	rows := &[]*models.UserOnboardingProfile{}
	restore := services.ObserveOnboardingProfileWrites(func(r *models.UserOnboardingProfile) { *rows = append(*rows, r) })
	t.Cleanup(restore)
	dummyDB(t)
	return rows
}

// urlCapture turns ATTRIBUTION_URL_CAPTURE_ENABLED on for the test. setup() restores config.Cfg.
func urlCapture(on bool) { config.Cfg.AttributionURLCaptureEnabled = on }

// signupWith is goodSignup with extra members appended to the JSON object.
func signupWith(extra string) string {
	return strings.TrimSuffix(goodSignup, "}") + extra + "}"
}

// signupReq posts a signup body, optionally with cookies.
func signupReq(body string, cookies ...*http.Cookie) *httptest.ResponseRecorder {
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/x", strings.NewReader(body))
	req.Header.Set("X-Real-IP", "203.0.113.9")
	for _, c := range cookies {
		req.AddCookie(c)
	}
	rec := httptest.NewRecorder()
	handleSignup(rec, req)
	return rec
}

const linkAttribution = `,"attribution":{"source":" HackerNews ","medium":"community","campaign":"launch-2026-11","content":"hn-launch","term":"bad value!","landingPath":"/For/Software-Teams?utm_source=x"}`

func str(p *string) string {
	if p == nil {
		return "<nil>"
	}
	return *p
}

// ---- persona ----

func TestSignup_PersonaIsStoredForANewAccountOnly(t *testing.T) {
	h := setup(t, true)
	rows := observePersonas(t)

	if rec := signupReq(signupWith(`,"persona":"creator"`)); rec.Code != 202 {
		t.Fatalf("status = %d, want 202", rec.Code)
	}
	u, _ := h.store.FindUserByEmail("new@example.com")
	if u == nil {
		t.Fatal("account not created")
	}
	if len(*rows) != 1 {
		t.Fatalf("persona inserts = %d, want 1", len(*rows))
	}
	r := (*rows)[0]
	if r.UserID != u.ID || str(r.SuggestedPersona) != "creator" {
		t.Fatalf("row = %+v (suggested %s), want user %s suggested creator", r, str(r.SuggestedPersona), u.ID)
	}
	// Only the suggestion is the Go API's to write: the answer columns stay untouched.
	if r.Persona != nil || r.Material != nil || r.AnsweredAt != nil || r.SkippedAt != nil {
		t.Fatalf("the Go API wrote an answer column: %+v", r)
	}
	if r.CreatedAt.IsZero() || r.UpdatedAt.IsZero() {
		t.Fatalf("created_at/updated_at must be set by the writer: %+v", r)
	}

	// The same address again, naming another persona: the account exists, so nothing is written.
	if rec := signupReq(signupWith(`,"persona":"agency"`)); rec.Code != 202 {
		t.Fatalf("duplicate status = %d, want 202", rec.Code)
	}
	if len(*rows) != 1 {
		t.Fatalf("an existing address wrote a persona: %d inserts", len(*rows))
	}
}

// What a signup body may put in `persona` and `attribution`, and what the handler then has in hand.
// Signup must never fail on marketing data, so a member of the wrong JSON type is not a decode error
// (which would be a 400): it decodes to nothing and is dropped like any other invalid value. Pure
// decoding, no account: the handler tests below take one case of each kind through the whole path.
func TestSignupRequest_MarketingMembersNeverFailTheDecode(t *testing.T) {
	for name, tc := range map[string]struct {
		members string
		want    signupMarketing
	}{
		"absent":                  {``, signupMarketing{}},
		"persona":                 {`,"persona":"creator"`, signupMarketing{persona: "creator"}},
		"persona null":            {`,"persona":null`, signupMarketing{}},
		"persona a number":        {`,"persona":42`, signupMarketing{}},
		"persona an object":       {`,"persona":{"a":"creator"}`, signupMarketing{}},
		"persona an array":        {`,"persona":["creator"]`, signupMarketing{}},
		"persona a boolean":       {`,"persona":true`, signupMarketing{}},
		"attribution":             {`,"attribution":{"source":"hn","medium":"m","campaign":"c","content":"n","term":"t","landingPath":"/x"}`, signupMarketing{carried: services.URLAttribution{Source: "hn", Medium: "m", Campaign: "c", Content: "n", Term: "t", LandingPath: "/x"}}},
		"attribution empty":       {`,"attribution":{}`, signupMarketing{}},
		"attribution null":        {`,"attribution":null`, signupMarketing{}},
		"attribution a string":    {`,"attribution":"utm_source=hn"`, signupMarketing{}},
		"attribution an array":    {`,"attribution":["hn"]`, signupMarketing{}},
		"attribution a number":    {`,"attribution":7`, signupMarketing{}},
		"members of wrong types":  {`,"attribution":{"source":7,"medium":{"x":1},"campaign":["a"],"content":null,"term":false}`, signupMarketing{}},
		"wrong types and a value": {`,"attribution":{"source":7,"campaign":"spring"}`, signupMarketing{carried: services.URLAttribution{Campaign: "spring"}}},
		"unknown members":         {`,"attribution":{"gclid":"x","referrer":"evil.example","source":"hn"}`, signupMarketing{carried: services.URLAttribution{Source: "hn"}}},
	} {
		var req SignupRequest
		if err := json.Unmarshal([]byte(signupWith(tc.members)), &req); err != nil {
			t.Errorf("%s: decode failed, which the handler answers with a 400: %v", name, err)
			continue
		}
		if got := req.marketing(); got != tc.want {
			t.Errorf("%s: carried %+v, want %+v", name, got, tc.want)
		}
		if req.Email == "" || !req.AcceptTerms || req.TermsVersion != "2026-10" {
			t.Errorf("%s: the rest of the body was lost: %+v", name, req)
		}
	}
}

func TestSignup_InvalidPersonaIsDroppedNeverRefused(t *testing.T) {
	for name, tc := range map[string]struct {
		member string
		want   string // "" = no row
	}{
		"other is in the set": {`,"persona":"other"`, "other"},
		"trimmed and lowered": {`,"persona":"  Educator "`, "educator"},
		"unknown token":       {`,"persona":"wizard"`, ""},
		"wrong JSON type":     {`,"persona":{"a":"creator"}`, ""},
		"injection shaped":    {`,"persona":"creator'; DROP TABLE x;--"`, ""},
	} {
		t.Run(name, func(t *testing.T) {
			h := setup(t, true)
			rows := observePersonas(t)
			rec := signupReq(signupWith(tc.member))
			if rec.Code != 202 {
				t.Fatalf("status = %d %s, want 202: marketing data must never refuse a signup", rec.Code, rec.Body)
			}
			if u, _ := h.store.FindUserByEmail("new@example.com"); u == nil {
				t.Fatal("the account was not created")
			}
			if tc.want == "" {
				if len(*rows) != 0 {
					t.Fatalf("an invalid persona was written: %s", str((*rows)[0].SuggestedPersona))
				}
				return
			}
			if len(*rows) != 1 || str((*rows)[0].SuggestedPersona) != tc.want {
				t.Fatalf("rows = %d, want one %q", len(*rows), tc.want)
			}
		})
	}
}

// ---- attribution: cookie first, then the URL-carried values behind the flag ----

func TestSignup_CookieBeatsTheURLCarriedAttribution(t *testing.T) {
	setup(t, true)
	urlCapture(true)
	rows := observeAttribution(t)

	rec := signupReq(signupWith(linkAttribution), ftCookie())
	if rec.Code != 202 {
		t.Fatalf("status = %d, want 202", rec.Code)
	}
	assertAttributionRow(t, *rows, rec) // one row, the cookie's values, cookie cleared
	if got := str((*rows)[0].Capture); got != services.AttributionCaptureCookie {
		t.Fatalf("capture = %s, want cookie", got)
	}
	if (*rows)[0].UTMCampaign != nil || (*rows)[0].UTMContent != nil {
		t.Fatalf("URL-carried values leaked into the cookie row: %+v", (*rows)[0])
	}
}

func TestSignup_CookieRowIsWrittenWithTheFlagOff(t *testing.T) {
	setup(t, true)
	urlCapture(false)
	rows := observeAttribution(t)

	rec := signupReq(goodSignup, ftCookie())
	assertAttributionRow(t, *rows, rec)
	if got := str((*rows)[0].Capture); got != services.AttributionCaptureCookie {
		t.Fatalf("capture = %s, want cookie", got)
	}
}

func TestSignup_URLCaptureOffWritesNoRow(t *testing.T) {
	setup(t, true)
	urlCapture(false) // the default
	rows := observeAttribution(t)

	rec := signupReq(signupWith(linkAttribution))
	if rec.Code != 202 {
		t.Fatalf("status = %d, want 202", rec.Code)
	}
	if len(*rows) != 0 {
		t.Fatalf("URL capture is off, yet %d row(s) were written: %+v", len(*rows), (*rows)[0])
	}
	if len(rec.Result().Cookies()) != 0 {
		t.Fatalf("no cookie was sent, so none may be set: %v", rec.Result().Cookies())
	}
}

func TestSignup_URLCaptureOnWritesAURLRow(t *testing.T) {
	h := setup(t, true)
	urlCapture(true)
	rows := observeAttribution(t)

	rec := signupReq(signupWith(linkAttribution))
	if rec.Code != 202 {
		t.Fatalf("status = %d, want 202", rec.Code)
	}
	u, _ := h.store.FindUserByEmail("new@example.com")
	if u == nil || len(*rows) != 1 {
		t.Fatalf("user %v, attribution inserts = %d, want 1", u, len(*rows))
	}
	r := (*rows)[0]
	if r.UserID != u.ID || str(r.Capture) != services.AttributionCaptureURL {
		t.Fatalf("row user %s capture %s, want %s url", r.UserID, str(r.Capture), u.ID)
	}
	// Sanitised with the cookie's rules: lower-cased and trimmed, the invalid term dropped, the
	// landing path cut at its query.
	if r.SourceLabel != "hackernews" || str(r.UTMSource) != "hackernews" || str(r.UTMMedium) != "community" ||
		str(r.UTMCampaign) != "launch-2026-11" || str(r.UTMContent) != "hn-launch" || r.UTMTerm != nil ||
		str(r.LandingPath) != "/for/software-teams" {
		t.Fatalf("row not sanitised as expected: %+v", r)
	}
	// A request carries no referrer and no visitor id, and the touch is the signup itself.
	if r.ReferrerHost != nil || r.VisitorID != nil {
		t.Fatalf("a URL row must have no referrer and no visitor id: %+v", r)
	}
	if r.FirstTouchAt == nil || !r.FirstTouchAt.Equal(h.now) {
		t.Fatalf("first_touch_at = %v, want the signup instant %v", r.FirstTouchAt, h.now)
	}
	if len(rec.Result().Cookies()) != 0 {
		t.Fatalf("the URL path must set no cookie: %v", rec.Result().Cookies())
	}
}

func TestSignup_URLRowLabelAndEmptiness(t *testing.T) {
	for name, tc := range map[string]struct {
		member    string
		wantRow   bool
		wantLabel string
	}{
		"no source is direct":      {`,"attribution":{"campaign":"spring","content":"flyer-1"}`, true, "direct"},
		"own host is not a source": {`,"attribution":{"source":"reelbolt.ai","medium":"owned"}`, true, "direct"},
		"nothing carried":          {``, false, ""},
		"every value invalid":      {`,"attribution":{"source":"a b","campaign":"../x","landingPath":"//evil.example","term":"` + strings.Repeat("a", 65) + `"}`, false, ""},
		"not an object":            {`,"attribution":"utm_source=hn"`, false, ""},
		"wrong types and a value":  {`,"attribution":{"source":7,"campaign":"spring"}`, true, "direct"},
	} {
		t.Run(name, func(t *testing.T) {
			h := setup(t, true)
			urlCapture(true)
			rows := observeAttribution(t)
			rec := signupReq(signupWith(tc.member))
			if rec.Code != 202 {
				t.Fatalf("status = %d %s, want 202: marketing data must never refuse a signup", rec.Code, rec.Body)
			}
			if u, _ := h.store.FindUserByEmail("new@example.com"); u == nil {
				t.Fatal("the account was not created")
			}
			if !tc.wantRow {
				if len(*rows) != 0 {
					t.Fatalf("want no row, got %+v", (*rows)[0])
				}
				return
			}
			if len(*rows) != 1 || (*rows)[0].SourceLabel != tc.wantLabel || str((*rows)[0].Capture) != "url" {
				t.Fatalf("rows = %d, want one url row labelled %q", len(*rows), tc.wantLabel)
			}
		})
	}
}

// A cookie that parses to nothing gives no cookie row, so the URL-carried values are the row; the
// cookie the request sent is still cleared.
func TestSignup_UnreadableCookieFallsBackToTheURL(t *testing.T) {
	setup(t, true)
	urlCapture(true)
	rows := observeAttribution(t)

	rec := signupReq(signupWith(linkAttribution), &http.Cookie{Name: "rb_ft", Value: "not-a-first-touch"})
	if rec.Code != 202 {
		t.Fatalf("status = %d, want 202", rec.Code)
	}
	if len(*rows) != 1 || str((*rows)[0].Capture) != "url" || (*rows)[0].SourceLabel != "hackernews" {
		t.Fatalf("want one url row from the request, got %d", len(*rows))
	}
	cleared := false
	for _, c := range rec.Result().Cookies() {
		if c.Name == "rb_ft" && c.MaxAge < 0 {
			cleared = true
		}
	}
	if !cleared {
		t.Fatal("rb_ft was sent, so it must be cleared")
	}
}

// The uniform 202 with everything this change adds in play: persona, URL-carried attribution, URL
// capture on, with and without the cookie. A new and an existing address answer the same status,
// body and headers, and the existing one writes nothing.
func TestSignup_MarketingDataKeepsTheUniform202AndAnExistingAddressWritesNothing(t *testing.T) {
	body := signupWith(`,"persona":"creator"` + linkAttribution)
	for _, withCookie := range []bool{false, true} {
		setup(t, true)
		urlCapture(true)
		attributions := observeAttribution(t)
		personas := &[]*models.UserOnboardingProfile{}
		t.Cleanup(services.ObserveOnboardingProfileWrites(func(r *models.UserOnboardingProfile) { *personas = append(*personas, r) }))
		events := observeFunnelEvents(t)

		do := func() *httptest.ResponseRecorder {
			if withCookie {
				return signupReq(body, ftCookie())
			}
			return signupReq(body)
		}
		first := do()
		if first.Code != 202 || len(*attributions) != 1 || len(*personas) != 1 || len(*events) != 1 {
			t.Fatalf("cookie=%v: new account: status %d, %d attribution, %d persona, %d funnel events; want 202 and one of each",
				withCookie, first.Code, len(*attributions), len(*personas), len(*events))
		}
		second := do()
		if first.Code != second.Code || first.Body.String() != second.Body.String() {
			t.Fatalf("cookie=%v: status/body differ: %d %q vs %d %q", withCookie, first.Code, first.Body, second.Code, second.Body)
		}
		if h1, h2 := first.Header(), second.Header(); !reflect.DeepEqual(h1, h2) {
			t.Fatalf("cookie=%v: headers differ: %v vs %v", withCookie, h1, h2)
		}
		if len(*attributions) != 1 || len(*personas) != 1 || len(*events) != 1 {
			t.Fatalf("cookie=%v: an existing address wrote something: %d attribution, %d persona, %d funnel events",
				withCookie, len(*attributions), len(*personas), len(*events))
		}
	}
}

// A dead database must never break signup, whichever of the new writes it kills.
func TestSignup_MarketingWriteFailureDoesNotBreakSignup(t *testing.T) {
	h := setup(t, true)
	urlCapture(true)
	dummyDB(t) // the real writers run against a connection that is refused

	rec := signupReq(signupWith(`,"persona":"creator"` + linkAttribution))
	if rec.Code != 202 || !strings.Contains(rec.Body.String(), acceptedMessage) {
		t.Fatalf("status = %d %s, want the uniform 202", rec.Code, rec.Body)
	}
	if u, _ := h.store.FindUserByEmail("new@example.com"); u == nil {
		t.Fatal("the account was not created")
	}
	if len(h.mails) != 1 || h.mails[0].tmpl != services.TemplateVerifyEmail {
		t.Fatalf("the verification mail was not sent: %v", h.mails)
	}
}

// ---- invite signup ----

func inviteSignupWith(email, token, extra string) string {
	return strings.TrimSuffix(inviteSignupBody(email, token), "}") + extra + "}"
}

func TestInviteSignup_StoresPersonaAndURLAttribution(t *testing.T) {
	e := newInviteEnv(t)
	urlCapture(true)
	attributions := observeAttribution(t)
	personas := &[]*models.UserOnboardingProfile{}
	t.Cleanup(services.ObserveOnboardingProfileWrites(func(r *models.UserOnboardingProfile) { *personas = append(*personas, r) }))

	rec := e.do("POST", "/api/v1/auth/signup", inviteSignupWith("new@example.com", e.token, `,"persona":"agency"`+linkAttribution), "")
	if rec.Code != 200 {
		t.Fatalf("invite signup got %d %s", rec.Code, rec.Body)
	}
	u, _ := e.store.FindUserByEmail("new@example.com")
	if u == nil {
		t.Fatal("account not created")
	}
	if len(*personas) != 1 || (*personas)[0].UserID != u.ID || str((*personas)[0].SuggestedPersona) != "agency" {
		t.Fatalf("persona inserts = %d, want one agency row for the new user", len(*personas))
	}
	if len(*attributions) != 1 || str((*attributions)[0].Capture) != "url" || (*attributions)[0].SourceLabel != "hackernews" {
		t.Fatalf("attribution inserts = %d, want one url row", len(*attributions))
	}
}

func TestInviteSignup_URLCaptureOffWritesNoAttribution(t *testing.T) {
	e := newInviteEnv(t)
	attributions := observeAttribution(t)

	rec := e.do("POST", "/api/v1/auth/signup", inviteSignupWith("new@example.com", e.token, linkAttribution), "")
	if rec.Code != 200 {
		t.Fatalf("invite signup got %d %s", rec.Code, rec.Body)
	}
	if len(*attributions) != 0 {
		t.Fatalf("URL capture is off, yet a row was written: %+v", (*attributions)[0])
	}
}

// An address that already has an account is refused on the invite path (409), and writes nothing.
func TestInviteSignup_ExistingAccountWritesNothing(t *testing.T) {
	e := newInviteEnv(t)
	urlCapture(true)
	e.existingUser("new@example.com")
	attributions := observeAttribution(t)
	personas := &[]*models.UserOnboardingProfile{}
	t.Cleanup(services.ObserveOnboardingProfileWrites(func(r *models.UserOnboardingProfile) { *personas = append(*personas, r) }))

	rec := e.do("POST", "/api/v1/auth/signup", inviteSignupWith("new@example.com", e.token, `,"persona":"agency"`+linkAttribution), "")
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d, want 409", rec.Code)
	}
	if len(*attributions) != 0 || len(*personas) != 0 {
		t.Fatalf("an existing account wrote %d attribution and %d persona rows", len(*attributions), len(*personas))
	}
}

// ---- OAuth: the state parameter carries, the cookie does not ----

func oauthStart(t *testing.T, query string) (*httptest.ResponseRecorder, url.Values) {
	t.Helper()
	req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/auth/oauth/google/start"+query, nil)
	req = mux.SetURLVars(req, map[string]string{"provider": "google"})
	rec := httptest.NewRecorder()
	handleOAuthStart(rec, req)
	if rec.Code != http.StatusFound {
		t.Fatalf("start got %d %s", rec.Code, rec.Body)
	}
	loc, err := url.Parse(rec.Header().Get("Location"))
	if err != nil {
		t.Fatalf("location: %v", err)
	}
	return rec, loc.Query()
}

func TestOAuthStart_PacksSanitisedValuesIntoTheStateParameterNotTheCookie(t *testing.T) {
	oauthSetup(t)
	urlCapture(true)
	installGoogle(t, newFakeProvider(t))

	rec, q := oauthStart(t, "?for=Creator&utm_source=%20HackerNews%20&utm_medium=community&utm_campaign=launch-2026-11"+
		"&utm_content=hn-launch&utm_term=bad%20value!&lp=%2FFor%2FCreators%3Futm_source%3Dx"+
		"&next=https%3A%2F%2Fevil.example&email=someone%40example.com")

	nonce, suffix := splitOAuthState(q.Get("state"))
	if nonce == "" || suffix == "" {
		t.Fatalf("state %q: want <random>.<suffix>", q.Get("state"))
	}
	if len(suffix) > maxOAuthCarryChars {
		t.Fatalf("suffix is %d characters, over the %d bound", len(suffix), maxOAuthCarryChars)
	}
	raw, err := base64.RawURLEncoding.DecodeString(suffix)
	if err != nil {
		t.Fatalf("suffix is not unpadded base64url: %v", err)
	}
	// Exactly the sanitised values, and nothing else the query named.
	if got, want := string(raw), `{"v":1,"p":"creator","s":"hackernews","m":"community","c":"launch-2026-11","n":"hn-launch","l":"/for/creators"}`; got != want {
		t.Fatalf("carried JSON = %s, want %s", got, want)
	}

	// The cookie is what it always was: provider, the random part, the PKCE verifier. Nothing carried
	// is stored on the device, and the random part is the one the callback will compare.
	c := cookieMap(rec)[oauthStateCookie]
	if c == nil {
		t.Fatal("no state cookie")
	}
	parts := strings.Split(c.Value, ":")
	if len(parts) != 3 || parts[0] != "google" || parts[1] != nonce {
		t.Fatalf("state cookie %q: want google:<the random part>:<verifier>", c.Value)
	}
	if strings.Contains(c.Value, suffix) || strings.Contains(c.Value, "hackernews") {
		t.Fatalf("carried values reached the cookie: %q", c.Value)
	}
	if q.Get("code_challenge") != pkceChallenge(parts[2]) {
		t.Fatal("code_challenge does not match the stored verifier")
	}
}

// With URL capture off (the default) the callback would record no campaign parameter and no landing
// path, so none is sent through the provider: the state carries the persona alone.
func TestOAuthStart_URLCaptureOffCarriesOnlyThePersona(t *testing.T) {
	oauthSetup(t)
	installGoogle(t, newFakeProvider(t))

	for _, query := range []string{oauthLinkQuery, oauthLinkQueryWithLanding, "?for=creator&lp=%2Ffor%2Fcreators"} {
		_, q := oauthStart(t, query)
		_, suffix := splitOAuthState(q.Get("state"))
		raw, err := base64.RawURLEncoding.DecodeString(suffix)
		if err != nil || string(raw) != `{"v":1,"p":"creator"}` {
			t.Fatalf("query %q: carried JSON = %s (err %v), want the persona alone", query, raw, err)
		}
	}
	// And with no persona either, the state is the bare random value.
	for _, query := range []string{
		"?utm_source=hackernews&utm_campaign=launch-2026-11",
		"?utm_source=hackernews&lp=%2Ffor%2Fcreators",
		"?lp=%2Ffor%2Fcreators",
	} {
		_, q := oauthStart(t, query)
		if state := q.Get("state"); state == "" || strings.Contains(state, oauthCarrySeparator) {
			t.Fatalf("query %q: state %q, want the bare random value", query, state)
		}
	}
}

func TestOAuthStart_NothingValidMeansTheBareState(t *testing.T) {
	oauthSetup(t)
	urlCapture(true)
	installGoogle(t, newFakeProvider(t))

	for _, query := range []string{
		"",
		"?for=wizard",
		"?utm_source=a%20b&utm_campaign=..%2Fx",
		"?for=&utm_source=",
		"?utm_source=" + strings.Repeat("a", 65),
		"?utm_campaign=" + strings.Repeat("x", 9000),
		"?landingPath=%2Ffor%2Fcreators&source=hn", // not parameters the start endpoint reads
		// A landing path is a path of ours: no host, no scheme, nothing that is not a path character.
		"?lp=%2F%2Fevil.example%2Ffor%2Fcreators",
		"?lp=https%3A%2F%2Fevil.example%2F",
		"?lp=pricing",
		"?lp=%2Ffor%2F%3Cscript%3E",
		"?lp=%2Ffor%2Fa%20b",
		"?lp=",
		"?lp=%2F" + strings.Repeat("a", 120),
		"?lp=%2F" + strings.Repeat("x", 9000),
	} {
		_, q := oauthStart(t, query)
		if state := q.Get("state"); state == "" || strings.Contains(state, oauthCarrySeparator) {
			t.Fatalf("query %q: state %q, want the bare random value", query, state)
		}
	}
}

func TestOAuthCarry_RoundTripAndBounds(t *testing.T) {
	// The largest carry the sanitisers let through: the longest persona, five campaign values of
	// the full 64 characters and a landing path of the full 120.
	long := strings.Repeat("a", 64)
	full := signupMarketing{persona: "nonprofit", carried: services.URLAttribution{
		Source: long, Medium: long, Campaign: long, Content: long, Term: long,
		LandingPath: "/" + strings.Repeat("p", 119),
	}}
	if got := full.carried.Sanitised(); got != full.carried {
		t.Fatalf("the largest carry must itself be valid, or this test measures nothing: %+v", got)
	}
	enc := encodeOAuthCarry(full)
	if enc == "" || len(enc) > maxOAuthCarryChars {
		t.Fatalf("the largest valid carry encodes to %d characters; it must fit the %d bound", len(enc), maxOAuthCarryChars)
	}
	// The whole state parameter (random part, separator, suffix) stays short enough for any
	// authorization URL: the providers document no limit of their own, and 1,000 characters leaves
	// the rest of the URL (client id, redirect, scope, PKCE challenge) far inside 2,000.
	nonce, _, err := newOAuthState()
	if err != nil {
		t.Fatal(err)
	}
	if total := len(nonce) + len(oauthCarrySeparator) + maxOAuthCarryChars; total > 1000 {
		t.Fatalf("a state parameter can reach %d characters, over 1,000", total)
	}
	// One character more in any value and the sanitiser drops that value, so nothing valid can be
	// larger than `full`: the bound never silently discards a real carry.
	if got := (services.URLAttribution{LandingPath: "/" + strings.Repeat("p", 120)}).Sanitised(); !got.IsZero() {
		t.Fatalf("a 121-character landing path survived sanitising: %+v", got)
	}
	if strings.ContainsAny(enc, ".:+/=") {
		t.Fatalf("the suffix must be unpadded base64url with no separator in it: %q", enc)
	}
	if got := decodeOAuthCarry(enc); got != full {
		t.Fatalf("round trip: got %+v, want %+v", got, full)
	}

	// Raw values never leave: the encoder sanitises whatever it is handed. The landing path is cut
	// at its query and lowercased, like the signup body's.
	dirty := signupMarketing{persona: "Wizard", carried: services.URLAttribution{
		Source: " Reddit ", Campaign: "has space", LandingPath: " /For/Creators?utm_source=x#top ",
	}}
	if got, want := decodeOAuthCarry(encodeOAuthCarry(dirty)), (signupMarketing{carried: services.URLAttribution{Source: "reddit", LandingPath: "/for/creators"}}); got != want {
		t.Fatalf("dirty carry: got %+v, want %+v", got, want)
	}
	// A landing path that is not a path of ours is dropped on its own; the rest still travels.
	for _, bad := range []string{"//evil.example/for/creators", "https://evil.example/", "pricing", "/for/<script>", `/for\creators`} {
		mk := signupMarketing{persona: "creator", carried: services.URLAttribution{Source: "reddit", LandingPath: bad}}
		if got, want := decodeOAuthCarry(encodeOAuthCarry(mk)), (signupMarketing{persona: "creator", carried: services.URLAttribution{Source: "reddit"}}); got != want {
			t.Fatalf("landing path %q: got %+v, want %+v", bad, got, want)
		}
	}
	if enc := encodeOAuthCarry(signupMarketing{persona: "wizard"}); enc != "" {
		t.Fatalf("nothing valid must encode to nothing, got %q", enc)
	}

	b64 := func(s string) string { return base64.RawURLEncoding.EncodeToString([]byte(s)) }
	for name, suffix := range map[string]string{
		"empty":             "",
		"not base64url":     "!!! not base64 !!!",
		"padded base64":     base64.URLEncoding.EncodeToString([]byte(`{"v":1,"p":"creator"}`)) + "=",
		"not JSON":          b64("for=creator"),
		"a JSON array":      b64(`["creator"]`),
		"a JSON string":     b64(`"creator"`),
		"another version":   b64(`{"v":2,"p":"creator"}`),
		"no version":        b64(`{"p":"creator"}`),
		"oversized":         b64(`{"v":1,"p":"creator","x":"` + strings.Repeat("y", 900) + `"}`),
		"a second dot part": b64(`{"v":1,"p":"creator"}`) + "." + b64(`{"v":1}`),
	} {
		if got := decodeOAuthCarry(suffix); got != (signupMarketing{}) {
			t.Errorf("%s: decoded %+v, want nothing", name, got)
		}
	}
	// Wrong member types are dropped one by one, like a signup body's.
	if got, want := decodeOAuthCarry(b64(`{"v":1,"p":7,"s":"hn","c":["x"],"l":{"path":"/x"}}`)), (signupMarketing{carried: services.URLAttribution{Source: "hn"}}); got != want {
		t.Fatalf("mixed types: got %+v, want %+v", got, want)
	}
}

// oauthRoundTrip starts a flow with startQuery, then answers the callback as the provider would:
// the state it was given, echoed back, with the cookie the start set (and any extra cookies).
func oauthRoundTrip(t *testing.T, startQuery string, extra ...*http.Cookie) *httptest.ResponseRecorder {
	t.Helper()
	start, q := oauthStart(t, startQuery)
	req := httptest.NewRequestWithContext(context.Background(), "GET",
		"/api/v1/auth/oauth/google/callback?code=the-code&state="+url.QueryEscape(q.Get("state")), nil)
	req.Header.Set("X-Real-IP", "203.0.113.9")
	req = mux.SetURLVars(req, map[string]string{"provider": "google"})
	req.AddCookie(cookieMap(start)[oauthStateCookie])
	for _, c := range extra {
		req.AddCookie(c)
	}
	rec := httptest.NewRecorder()
	handleOAuthCallback(rec, req)
	return rec
}

const oauthLinkQuery = "?for=creator&utm_source=HackerNews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch"

// The same link with the landing page the signup link named (`lp`), as the marketing site and the
// dashboard write it: a path, here with a capital and a query the sanitiser removes.
const oauthLinkQueryWithLanding = oauthLinkQuery + "&lp=%2FFor%2FCreators%3Futm_source%3Dx"

func TestOAuth_RoundTripStoresPersonaAndURLAttributionOnANewAccount(t *testing.T) {
	h := oauthSetup(t)
	urlCapture(true)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	attributions := observeAttribution(t)
	personas := &[]*models.UserOnboardingProfile{}
	t.Cleanup(services.ObserveOnboardingProfileWrites(func(r *models.UserOnboardingProfile) { *personas = append(*personas, r) }))

	rec := oauthRoundTrip(t, oauthLinkQuery)
	if rec.Code != http.StatusFound || rec.Header().Get("Location") != "https://reelbolt.test/app/accept-terms" {
		t.Fatalf("got %d to %q, want the first-login redirect", rec.Code, rec.Header().Get("Location"))
	}
	u, _ := h.store.FindUserByEmail("ada@example.com")
	if u == nil {
		t.Fatal("account not created")
	}
	if len(*personas) != 1 || (*personas)[0].UserID != u.ID || str((*personas)[0].SuggestedPersona) != "creator" {
		t.Fatalf("persona inserts = %d, want one creator row for the new user", len(*personas))
	}
	if len(*attributions) != 1 {
		t.Fatalf("attribution inserts = %d, want 1", len(*attributions))
	}
	r := (*attributions)[0]
	if r.UserID != u.ID || str(r.Capture) != "url" || r.SourceLabel != "hackernews" || str(r.UTMMedium) != "community" ||
		str(r.UTMCampaign) != "launch-2026-11" || str(r.UTMContent) != "hn-launch" || r.LandingPath != nil || r.VisitorID != nil {
		t.Fatalf("row not as carried: %+v", r)
	}
	if r.FirstTouchAt == nil || !r.FirstTouchAt.Equal(h.now) {
		t.Fatalf("first_touch_at = %v, want %v", r.FirstTouchAt, h.now)
	}
}

func TestOAuth_URLCaptureOffStoresThePersonaButNoAttribution(t *testing.T) {
	oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	attributions := observeAttribution(t)
	personas := &[]*models.UserOnboardingProfile{}
	t.Cleanup(services.ObserveOnboardingProfileWrites(func(r *models.UserOnboardingProfile) { *personas = append(*personas, r) }))

	if rec := oauthRoundTrip(t, oauthLinkQuery); rec.Code != http.StatusFound {
		t.Fatalf("got %d", rec.Code)
	}
	if len(*attributions) != 0 {
		t.Fatalf("URL capture is off, yet a row was written: %+v", (*attributions)[0])
	}
	if len(*personas) != 1 {
		t.Fatalf("the persona is not attribution and is not behind the flag: %d inserts", len(*personas))
	}
}

func TestOAuth_CookieBeatsTheCarriedAttribution(t *testing.T) {
	oauthSetup(t)
	urlCapture(true)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	attributions := observeAttribution(t)

	rec := oauthRoundTrip(t, oauthLinkQuery, ftCookie())
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d", rec.Code)
	}
	assertAttributionRow(t, *attributions, rec)
	if got := str((*attributions)[0].Capture); got != "cookie" {
		t.Fatalf("capture = %s, want cookie", got)
	}
}

// The landing page the signup link named survives the provider round trip and is stored on the new
// account like the landing path of a password signup: sanitised, and only as a URL-captured row.
func TestOAuth_RoundTripStoresTheLandingPathOnANewAccount(t *testing.T) {
	h := oauthSetup(t)
	urlCapture(true)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	attributions := observeAttribution(t)

	rec := oauthRoundTrip(t, oauthLinkQueryWithLanding)
	if rec.Code != http.StatusFound || rec.Header().Get("Location") != "https://reelbolt.test/app/accept-terms" {
		t.Fatalf("got %d to %q, want the first-login redirect", rec.Code, rec.Header().Get("Location"))
	}
	u, _ := h.store.FindUserByEmail("ada@example.com")
	if u == nil {
		t.Fatal("account not created")
	}
	if len(*attributions) != 1 {
		t.Fatalf("attribution inserts = %d, want 1", len(*attributions))
	}
	r := (*attributions)[0]
	if r.UserID != u.ID || str(r.Capture) != "url" || r.SourceLabel != "hackernews" ||
		str(r.UTMContent) != "hn-launch" || str(r.LandingPath) != "/for/creators" || r.VisitorID != nil || r.ReferrerHost != nil {
		t.Fatalf("row not as carried: %+v", r)
	}
}

// Whatever `lp` holds, the sign-in goes through; a value that is not a path of ours is not stored,
// and the campaign it came with still is.
func TestOAuth_InvalidLandingPathIsDroppedNeverRefused(t *testing.T) {
	for name, lp := range map[string]string{
		"another host":     "%2F%2Fevil.example%2Ffor%2Fcreators",
		"an absolute url":  "https%3A%2F%2Fevil.example%2F",
		"no leading slash": "pricing",
		"markup":           "%2Ffor%2F%3Cscript%3E",
		"too long":         "%2F" + strings.Repeat("a", 120),
		"huge":             "%2F" + strings.Repeat("a", 9000),
		"repeated":         "%2F%2Fevil.example&lp=%2Fpricing",
	} {
		t.Run(name, func(t *testing.T) {
			h := oauthSetup(t)
			urlCapture(true)
			f := newFakeProvider(t)
			f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
			installGoogle(t, f)
			attributions := observeAttribution(t)

			rec := oauthRoundTrip(t, oauthLinkQuery+"&lp="+lp)
			if rec.Code != http.StatusFound || rec.Header().Get("Location") != "https://reelbolt.test/app/accept-terms" {
				t.Fatalf("got %d to %q: marketing data must never fail a sign-in", rec.Code, rec.Header().Get("Location"))
			}
			if u, _ := h.store.FindUserByEmail("ada@example.com"); u == nil {
				t.Fatal("account not created")
			}
			if len(*attributions) != 1 || (*attributions)[0].SourceLabel != "hackernews" || (*attributions)[0].LandingPath != nil {
				t.Fatalf("want the campaign row with no landing path, got %+v", *attributions)
			}
		})
	}
}

// The landing path is behind the same switch as the campaign parameters: with URL capture off it
// is neither carried nor stored, alone or with a campaign.
func TestOAuth_URLCaptureOffStoresNoLandingPath(t *testing.T) {
	for _, query := range []string{oauthLinkQueryWithLanding, "?lp=%2Ffor%2Fcreators"} {
		oauthSetup(t)
		f := newFakeProvider(t)
		f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
		installGoogle(t, f)
		attributions := observeAttribution(t)

		if rec := oauthRoundTrip(t, query); rec.Code != http.StatusFound || strings.Contains(rec.Header().Get("Location"), "error=") {
			t.Fatalf("query %q: got %d to %q, want a sign-in", query, rec.Code, rec.Header().Get("Location"))
		}
		if len(*attributions) != 0 {
			t.Fatalf("query %q: URL capture is off, yet a row was written: %+v", query, (*attributions)[0])
		}
	}
	// A state that was not minted here and names a landing path anyway is not recorded either.
	oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	attributions := observeAttribution(t)
	forged := "st-1." + base64.RawURLEncoding.EncodeToString([]byte(`{"v":1,"s":"hackernews","l":"/for/creators"}`))
	rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state="+forged, "google:st-1:verifier-abc")
	if rec.Code != http.StatusFound || strings.Contains(rec.Header().Get("Location"), "error=") {
		t.Fatalf("got %d to %q, want a sign-in", rec.Code, rec.Header().Get("Location"))
	}
	if len(*attributions) != 0 {
		t.Fatalf("URL capture is off, yet a forged state wrote a row: %+v", (*attributions)[0])
	}
}

// A landing path with no campaign parameter is stored the way the signup body's is: one URL row
// with no source, labelled direct. (The pages of ours send `lp` only next to a campaign parameter.)
func TestOAuth_LandingPathAloneIsStoredLikeTheSignupBodys(t *testing.T) {
	oauthSetup(t)
	urlCapture(true)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	attributions := observeAttribution(t)
	if rec := oauthRoundTrip(t, "?lp=%2Fpricing"); rec.Code != http.StatusFound || strings.Contains(rec.Header().Get("Location"), "error=") {
		t.Fatalf("got %d to %q, want a sign-in", rec.Code, rec.Header().Get("Location"))
	}
	oauthRow := *attributions

	// The same landing path through the password signup, for the comparison.
	setup(t, true)
	urlCapture(true)
	bodyRows := observeAttribution(t)
	if rec := signupReq(signupWith(`,"attribution":{"landingPath":"/pricing"}`)); rec.Code != 202 {
		t.Fatalf("signup status = %d", rec.Code)
	}
	if len(oauthRow) != 1 || len(*bodyRows) != 1 {
		t.Fatalf("rows: provider sign-in %d, password signup %d, want one each", len(oauthRow), len(*bodyRows))
	}
	a, b := oauthRow[0], (*bodyRows)[0]
	if a.SourceLabel != "direct" || str(a.Capture) != "url" || str(a.LandingPath) != "/pricing" || a.UTMSource != nil {
		t.Fatalf("provider sign-in row: %+v", a)
	}
	if a.SourceLabel != b.SourceLabel || str(a.Capture) != str(b.Capture) || str(a.LandingPath) != str(b.LandingPath) {
		t.Fatalf("the two paths disagree: provider %+v, password %+v", a, b)
	}
}

// The consented cookie still wins: its landing path is the row's, not the one the link named.
func TestOAuth_CookieLandingPathBeatsTheCarriedOne(t *testing.T) {
	oauthSetup(t)
	urlCapture(true)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	attributions := observeAttribution(t)

	rec := oauthRoundTrip(t, oauthLinkQueryWithLanding, ftCookie())
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d", rec.Code)
	}
	assertAttributionRow(t, *attributions, rec) // landing path /pricing, from the cookie
	if got := str((*attributions)[0].Capture); got != "cookie" {
		t.Fatalf("capture = %s, want cookie", got)
	}
}

// Signing in to (linking) an account that already exists writes nothing, whatever the link carried.
func TestOAuth_ExistingAccountWritesNothing(t *testing.T) {
	h := oauthSetup(t)
	urlCapture(true)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"1","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	verifiedAt := h.now.Add(-time.Hour)
	id := uuid.New()
	h.store.users[id] = &models.ApplicationUser{
		ID: id, Email: "ada@example.com", DisplayName: "Ada", AuthProvider: "Password", EmailVerifiedAt: &verifiedAt,
	}
	attributions := observeAttribution(t)
	personas := &[]*models.UserOnboardingProfile{}
	t.Cleanup(services.ObserveOnboardingProfileWrites(func(r *models.UserOnboardingProfile) { *personas = append(*personas, r) }))
	events := observeFunnelEvents(t)

	rec := oauthRoundTrip(t, oauthLinkQueryWithLanding)
	if rec.Code != http.StatusFound || strings.Contains(rec.Header().Get("Location"), "error=") {
		t.Fatalf("got %d to %q, want a sign-in", rec.Code, rec.Header().Get("Location"))
	}
	if len(h.store.users) != 1 {
		t.Fatalf("a second account was created: %d", len(h.store.users))
	}
	if len(*attributions) != 0 || len(*personas) != 0 || len(*events) != 0 {
		t.Fatalf("an existing account wrote %d attribution, %d persona, %d funnel events",
			len(*attributions), len(*personas), len(*events))
	}
}

// Whatever follows the random part, the sign-in succeeds when the random part matches the cookie,
// and a suffix the callback cannot read is simply not there. TestOAuthCarry_RoundTripAndBounds has
// the full table of unreadable suffixes; these take one of each kind through the whole callback.
func TestOAuthCallback_GarbageOrOversizedSuffixNeverFailsTheLogin(t *testing.T) {
	b64 := func(s string) string { return base64.RawURLEncoding.EncodeToString([]byte(s)) }
	for name, suffix := range map[string]string{
		"not base64url":  "%21%21%21",
		"oversized":      b64(`{"v":1,"p":"creator","s":"hn","x":"` + strings.Repeat("y", 4000) + `"}`),
		"invalid values": b64(`{"v":1,"p":"wizard","s":"a b","c":"../x"}`),
	} {
		t.Run(name, func(t *testing.T) {
			h := oauthSetup(t)
			urlCapture(true)
			f := newFakeProvider(t)
			f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
			installGoogle(t, f)
			attributions := observeAttribution(t)
			personas := &[]*models.UserOnboardingProfile{}
			t.Cleanup(services.ObserveOnboardingProfileWrites(func(r *models.UserOnboardingProfile) { *personas = append(*personas, r) }))

			rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=st-1."+suffix, "google:st-1:verifier-abc")
			if rec.Code != http.StatusFound || rec.Header().Get("Location") != "https://reelbolt.test/app/accept-terms" {
				t.Fatalf("got %d to %q: an unreadable suffix must not fail the login", rec.Code, rec.Header().Get("Location"))
			}
			if u, _ := h.store.FindUserByEmail("ada@example.com"); u == nil {
				t.Fatal("account not created")
			}
			if f.gotVerifier != "verifier-abc" {
				t.Fatalf("the PKCE verifier was not found from the random part: %q", f.gotVerifier)
			}
			if len(*attributions) != 0 || len(*personas) != 0 {
				t.Fatalf("an unreadable suffix wrote %d attribution and %d persona rows", len(*attributions), len(*personas))
			}
		})
	}
}

// The suffix buys nothing against the state check: the random part must still be the cookie's.
func TestOAuthCallback_SuffixDoesNotWeakenTheStateCheck(t *testing.T) {
	oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	suffix := encodeOAuthCarry(signupMarketing{persona: "creator"})

	for name, state := range map[string]string{
		"another random part":      "st-2." + suffix,
		"no random part":           "." + suffix,
		"the suffix alone":         suffix,
		"cookie value as a prefix": "st-1x." + suffix,
	} {
		rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state="+url.QueryEscape(state), "google:st-1:verifier-abc")
		// The failure may hand the carried persona back to the login page (see the tests below);
		// what matters here is that it IS the failure: the login page, the reason, and no session.
		loc := rec.Header().Get("Location")
		if rec.Code != http.StatusFound || !strings.HasPrefix(loc, "https://reelbolt.test/app/login?error=oauth_failed") {
			t.Errorf("%s: got %d to %q, want the oauth_failed redirect", name, rec.Code, loc)
		}
		if c := cookieMap(rec); c["reelbolt_token"] != nil || c["reelbolt_user"] != nil {
			t.Errorf("%s: a session was issued on a state that does not match", name)
		}
	}
}

// failedLogin is the redirect of a failed provider sign-in, taken apart: it must be the web login
// page of this deployment, with a reason, and it returns the whole query for the caller to compare.
func failedLogin(t *testing.T, rec *httptest.ResponseRecorder) url.Values {
	t.Helper()
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d, want a redirect: %s", rec.Code, rec.Body)
	}
	u, err := url.Parse(rec.Header().Get("Location"))
	if err != nil || u.Scheme+"://"+u.Host+u.Path != "https://reelbolt.test/app/login" {
		t.Fatalf("location %q is not the login page", rec.Header().Get("Location"))
	}
	if c := cookieMap(rec); c["reelbolt_token"] != nil || c["reelbolt_user"] != nil {
		t.Fatal("a failed sign-in issued a session")
	}
	return u.Query()
}

// wantLoginQuery compares a failed sign-in's query with the exact pairs expected: nothing missing,
// nothing extra.
func wantLoginQuery(t *testing.T, got url.Values, want map[string]string) {
	t.Helper()
	if len(got) != len(want) {
		t.Fatalf("query = %v, want exactly %v", got, want)
	}
	for k, v := range want {
		if vs := got[k]; len(vs) != 1 || vs[0] != v {
			t.Fatalf("query %s = %v, want %q (whole query %v)", k, vs, v, got)
		}
	}
}

var carriedLink = map[string]string{
	"for": "creator", "utm_source": "hackernews", "utm_medium": "community",
	"utm_campaign": "launch-2026-11", "utm_content": "hn-launch",
}

// carriedLinkWithLanding is what oauthLinkQueryWithLanding hands back after a failed sign-in.
var carriedLinkWithLanding = map[string]string{
	"for": "creator", "utm_source": "hackernews", "utm_medium": "community",
	"utm_campaign": "launch-2026-11", "utm_content": "hn-launch", "lp": "/for/creators",
}

func withReason(reason string, carry map[string]string) map[string]string {
	out := map[string]string{"error": reason}
	for k, v := range carry {
		out[k] = v
	}
	return out
}

// Pressing Cancel at Google or GitHub is an ordinary path: the person then signs up with email.
// The persona and the campaign live only in the URL, so the failure redirect has to hand them back
// to the login page, or that email signup is unattributed and gets no suggested audience.
func TestOAuthCallback_CancelAtTheProviderKeepsTheCarry(t *testing.T) {
	oauthSetup(t)
	urlCapture(true)
	installGoogle(t, newFakeProvider(t))
	attributions := observeAttribution(t)

	start, q := oauthStart(t, oauthLinkQuery)
	// What the provider sends when the person declines: an error, no code, and the state echoed.
	rec := oauthCall(t, handleOAuthCallback, "google",
		"?error=access_denied&state="+url.QueryEscape(q.Get("state")), cookieMap(start)[oauthStateCookie].Value)
	wantLoginQuery(t, failedLogin(t, rec), withReason("oauth_failed", carriedLink))
	if len(*attributions) != 0 {
		t.Fatal("a cancelled sign-in created no account and must record nothing")
	}
	// The state cookie is spent either way.
	if c := cookieMap(rec)[oauthStateCookie]; c == nil || c.MaxAge >= 0 {
		t.Fatal("the state cookie was not cleared")
	}

	// The landing page the link named comes back too, as `lp`, sanitised: the signup form the
	// person goes on to sends it as attribution.landingPath.
	start, q = oauthStart(t, oauthLinkQueryWithLanding)
	rec = oauthCall(t, handleOAuthCallback, "google",
		"?error=access_denied&state="+url.QueryEscape(q.Get("state")), cookieMap(start)[oauthStateCookie].Value)
	wantLoginQuery(t, failedLogin(t, rec), withReason("oauth_failed", carriedLinkWithLanding))
	if loc := rec.Header().Get("Location"); !strings.Contains(loc, "lp=%2Ffor%2Fcreators") {
		t.Fatalf("location %q: the landing path must be percent-encoded as one query value", loc)
	}
}

// With URL capture off the campaign and the landing path never left for the provider, and they do
// not come back either, whatever the returned state claims. The persona is not attribution and
// always travels.
func TestOAuthCallback_FailureWithURLCaptureOffCarriesThePersonaOnly(t *testing.T) {
	oauthSetup(t)
	installGoogle(t, newFakeProvider(t))

	start, q := oauthStart(t, oauthLinkQueryWithLanding)
	rec := oauthCall(t, handleOAuthCallback, "google",
		"?error=access_denied&state="+url.QueryEscape(q.Get("state")), cookieMap(start)[oauthStateCookie].Value)
	wantLoginQuery(t, failedLogin(t, rec), map[string]string{"error": "oauth_failed", "for": "creator"})

	// A state that was not minted here and names a campaign anyway: still the persona only.
	forged := "st-1." + base64.RawURLEncoding.EncodeToString([]byte(`{"v":1,"p":"creator","s":"hackernews","n":"hn-launch","l":"/for/creators"}`))
	rec = oauthCall(t, handleOAuthCallback, "google", "?error=access_denied&state="+forged, "google:st-1:verifier-abc")
	wantLoginQuery(t, failedLogin(t, rec), map[string]string{"error": "oauth_failed", "for": "creator"})
}

// Every way a sign-in can fail after the provider answered keeps the carry too: the person is back
// on the login page with the reason, and can go on to the signup form with the link intact.
func TestOAuthCallback_LaterFailuresKeepTheCarry(t *testing.T) {
	cases := map[string]struct {
		arrange func(t *testing.T, h *harness, f *fakeProvider)
		reason  string
	}{
		"token exchange refused": {
			arrange: func(_ *testing.T, _ *harness, f *fakeProvider) { f.tokenStatus = http.StatusBadRequest },
			reason:  "oauth_failed",
		},
		"provider reports an unverified address": {
			arrange: func(_ *testing.T, _ *harness, f *fakeProvider) {
				f.userInfo = `{"sub":"1","email":"ada@example.com","email_verified":false}`
			},
			reason: "oauth_email_unverified",
		},
		"per-address account cap": {
			arrange: func(_ *testing.T, h *harness, _ *fakeProvider) {
				ip := "203.0.113.9"
				for i := 0; i < 3; i++ {
					id := uuid.New()
					h.store.users[id] = &models.ApplicationUser{
						ID: id, Email: fmt.Sprintf("x%d@y.z", i), SignupIP: &ip, CreatedAt: h.now.Add(-time.Minute),
					}
				}
			},
			reason: "oauth_rate_limited",
		},
		"the address has an unverified password account": {
			arrange: func(t *testing.T, h *harness, _ *fakeProvider) {
				useFakeTermsVersion(t, h.store)
				id := uuid.New()
				h.store.users[id] = &models.ApplicationUser{
					ID: id, Email: "ada@example.com", DisplayName: "Ada", AuthProvider: "Password", PasswordHash: "x",
				}
			},
			reason: "oauth_account_unverified",
		},
		"signup is closed": {
			arrange: func(_ *testing.T, _ *harness, _ *fakeProvider) { config.Cfg.SignupEnabled = false },
			reason:  "oauth_no_account",
		},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			h := oauthSetup(t)
			urlCapture(true)
			f := newFakeProvider(t)
			f.userInfo = `{"sub":"1","email":"ada@example.com","email_verified":true,"name":"Ada"}`
			installGoogle(t, f)
			c.arrange(t, h, f)
			attributions := observeAttribution(t)
			personas := &[]*models.UserOnboardingProfile{}
			t.Cleanup(services.ObserveOnboardingProfileWrites(func(r *models.UserOnboardingProfile) { *personas = append(*personas, r) }))

			rec := oauthRoundTrip(t, oauthLinkQueryWithLanding)
			wantLoginQuery(t, failedLogin(t, rec), withReason(c.reason, carriedLinkWithLanding))
			if len(*attributions) != 0 || len(*personas) != 0 {
				t.Fatalf("a failed sign-in wrote %d attribution and %d persona rows", len(*attributions), len(*personas))
			}
		})
	}
}

// An expired or missing state cookie (the person took longer than the cookie lives at the provider)
// fails the sign-in as before, and still hands back what the returned state carried.
func TestOAuthCallback_StateMismatchKeepsTheCarry(t *testing.T) {
	oauthSetup(t)
	urlCapture(true)
	installGoogle(t, newFakeProvider(t))
	_, q := oauthStart(t, oauthLinkQueryWithLanding)

	rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state="+url.QueryEscape(q.Get("state")), "")
	wantLoginQuery(t, failedLogin(t, rec), withReason("oauth_failed", carriedLinkWithLanding))
}

// What follows the random part is not trusted on the way back to the login page either: only a
// closed-set persona and labels of the closed pattern are written, and a suffix the callback cannot
// read adds nothing at all. The redirect can never be made to carry free text or another host.
func TestOAuthCallback_FailureNeverRelaysAnUnsanitisedSuffix(t *testing.T) {
	b64 := func(s string) string { return base64.RawURLEncoding.EncodeToString([]byte(s)) }
	bare := map[string]string{"error": "oauth_failed"}
	cases := map[string]struct {
		suffix string
		want   map[string]string
	}{
		"not base64url":    {suffix: "%21%21%21", want: bare},
		"not json":         {suffix: b64("hello"), want: bare},
		"another format":   {suffix: b64(`{"v":2,"p":"creator","s":"hn"}`), want: bare},
		"oversized":        {suffix: b64(`{"v":1,"p":"creator","s":"hn","x":"` + strings.Repeat("y", 4000) + `"}`), want: bare},
		"invalid values":   {suffix: b64(`{"v":1,"p":"wizard","s":"a b","m":"<script>","c":"../x","n":"https://evil.example/","t":"x&next=//evil.example","l":"//evil.example/login"}`), want: bare},
		"wrong json types": {suffix: b64(`{"v":1,"p":42,"s":["hn"],"c":{"a":1},"l":["/pricing"]}`), want: bare},
		"a landing path is a path of ours or nothing": {
			suffix: b64(`{"v":1,"p":"creator","l":"https://evil.example/for/creators"}`),
			want:   map[string]string{"error": "oauth_failed", "for": "creator"},
		},
		"a landing path is cut at its query, never echoed": {
			suffix: b64(`{"v":1,"s":"hackernews","l":" /Pricing?next=//evil.example&error=x#frag "}`),
			want:   map[string]string{"error": "oauth_failed", "utm_source": "hackernews", "lp": "/pricing"},
		},
		"our own host is not a source": {
			suffix: b64(`{"v":1,"p":"educator","s":"app.reelbolt.ai","n":"hn-launch"}`),
			want:   map[string]string{"error": "oauth_failed", "for": "educator", "utm_content": "hn-launch"},
		},
		"values are normalised, not echoed": {
			suffix: b64(`{"v":1,"p":" Creator ","s":" HackerNews ","c":"Launch-2026-11"}`),
			want:   map[string]string{"error": "oauth_failed", "for": "creator", "utm_source": "hackernews", "utm_campaign": "launch-2026-11"},
		},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			oauthSetup(t)
			urlCapture(true)
			installGoogle(t, newFakeProvider(t))
			rec := oauthCall(t, handleOAuthCallback, "google", "?error=access_denied&state=st-1."+c.suffix, "google:st-1:verifier-abc")
			wantLoginQuery(t, failedLogin(t, rec), c.want)
		})
	}

	// Nothing carried: the redirect is byte for byte what it was before the carry existed.
	oauthSetup(t)
	urlCapture(true)
	installGoogle(t, newFakeProvider(t))
	rec := oauthCall(t, handleOAuthCallback, "google", "?error=access_denied&state=st-1", "google:st-1:verifier-abc")
	if got := rec.Header().Get("Location"); got != "https://reelbolt.test/app/login?error=oauth_failed" {
		t.Fatalf("location %q", got)
	}
}

// A deletion confirmation is not a sign-in from a landing page. Its state is minted without a
// suffix, so one that turns up on the way back was not written here and is not relayed.
func TestOAuthCallback_DeletionConfirmationFailureCarriesNothing(t *testing.T) {
	oauthSetup(t)
	urlCapture(true)
	installGoogle(t, newFakeProvider(t))
	suffix := encodeOAuthCarry(signupMarketing{persona: "creator", carried: services.URLAttribution{Source: "hackernews"}})
	cookie := "google:st-1:verifier-abc:" + oauthIntentDelete + ":" + uuid.New().String()

	rec := oauthCall(t, handleOAuthCallback, "google", "?error=access_denied&state=st-1."+suffix, cookie)
	wantLoginQuery(t, failedLogin(t, rec), map[string]string{"error": "oauth_failed"})
}

// A deletion confirmation is not a sign-in from a landing page: its state carries nothing.
func TestBeginProviderConfirmation_StateCarriesNothing(t *testing.T) {
	oauthSetup(t)
	installGoogle(t, newFakeProvider(t))
	rec := httptest.NewRecorder()
	start, err := beginProviderConfirmation(rec, &models.ApplicationUser{ID: uuid.New(), AuthProvider: "Google"})
	if err != nil || start.URL == "" {
		t.Fatalf("start %+v err %v", start, err)
	}
	u, err := url.Parse(start.URL)
	if err != nil {
		t.Fatal(err)
	}
	if state := u.Query().Get("state"); state == "" || strings.Contains(state, oauthCarrySeparator) {
		t.Fatalf("state %q, want the bare random value", state)
	}
}

// ---- alpha.signup names the Personal organization ----

type signupEvent struct {
	user uuid.UUID
	org  *uuid.UUID
}

func observeSignupProductEvents(t *testing.T) *[]signupEvent {
	t.Helper()
	old := recordSignupProductEvent
	t.Cleanup(func() { recordSignupProductEvent = old })
	got := &[]signupEvent{}
	recordSignupProductEvent = func(userID uuid.UUID, orgID *uuid.UUID) { *got = append(*got, signupEvent{userID, orgID}) }
	return got
}

// assertSignupEventForPersonalOrg: exactly one alpha.signup, for the stored user, naming the
// organization provisioning stamped on that user.
func assertSignupEventForPersonalOrg(t *testing.T, got []signupEvent, stored *models.ApplicationUser) {
	t.Helper()
	if stored == nil {
		t.Fatal("account not created")
	}
	if len(got) != 1 || got[0].user != stored.ID {
		t.Fatalf("alpha.signup events = %+v, want exactly one for %s", got, stored.ID)
	}
	if stored.LastActiveOrganizationID == nil || got[0].org == nil || *got[0].org != *stored.LastActiveOrganizationID {
		t.Fatalf("alpha.signup organization = %v, want the Personal organization %v", got[0].org, stored.LastActiveOrganizationID)
	}
}

func TestSignup_AlphaSignupNamesThePersonalOrganization(t *testing.T) {
	h := setup(t, true)
	got := observeSignupProductEvents(t)
	if rec := post(handleSignup, goodSignup); rec.Code != 202 {
		t.Fatalf("status = %d", rec.Code)
	}
	u, _ := h.store.FindUserByEmail("new@example.com")
	assertSignupEventForPersonalOrg(t, *got, u)

	// The existing address: no second event.
	if rec := post(handleSignup, goodSignup); rec.Code != 202 || len(*got) != 1 {
		t.Fatalf("duplicate signup: status %d, events %d", rec.Code, len(*got))
	}
}

func TestSignup_AlphaSignupIsStillWrittenWhenThePersonalOrganizationIsNotKnown(t *testing.T) {
	h := setup(t, true)
	h.store.noPersonalOrg = true
	got := observeSignupProductEvents(t)
	if rec := post(handleSignup, goodSignup); rec.Code != 202 {
		t.Fatalf("status = %d", rec.Code)
	}
	if len(*got) != 1 || (*got)[0].org != nil {
		t.Fatalf("alpha.signup events = %+v, want one with no organization", *got)
	}
}

func TestInviteSignup_AlphaSignupNamesThePersonalOrganizationNotTheTeam(t *testing.T) {
	e := newInviteEnv(t)
	got := observeSignupProductEvents(t)
	if rec := e.do("POST", "/api/v1/auth/signup", inviteSignupBody("new@example.com", e.token), ""); rec.Code != 200 {
		t.Fatalf("invite signup got %d %s", rec.Code, rec.Body)
	}
	u, _ := e.store.FindUserByEmail("new@example.com")
	assertSignupEventForPersonalOrg(t, *got, u)
	if *(*got)[0].org == e.inv.OrganizationID {
		t.Fatal("alpha.signup named the team the invite was for; alpha.invite_accepted is the row that does")
	}
}

func TestOAuthFirstSignIn_AlphaSignupNamesThePersonalOrganization(t *testing.T) {
	h := oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	got := observeSignupProductEvents(t)

	if rec := oauthCall(t, handleOAuthCallback, "google", "?code=the-code&state=st-1", "google:st-1:verifier-abc"); rec.Code != http.StatusFound {
		t.Fatalf("got %d", rec.Code)
	}
	u, _ := h.store.FindUserByEmail("ada@example.com")
	assertSignupEventForPersonalOrg(t, *got, u)
}

// ---- picked plan ----

func TestSignup_PickedPlanIsStoredAndDroppedWhenInvalid(t *testing.T) {
	long := strings.Repeat("a", 33)
	for name, tc := range map[string]struct {
		member      string
		wantPlan    string
		wantPersona string
		wantRow     bool
	}{
		"plan only":                {`,"plan":" Pro "`, "pro", "<nil>", true},
		"plan and persona":         {`,"plan":"team-2","persona":"agency"`, "team-2", "agency", true},
		"persona only":             {`,"persona":"creator"`, "<nil>", "creator", true},
		"oversize":                 {`,"plan":"` + long + `"`, "", "", false},
		"non-string":               {`,"plan":{"a":"pro"}`, "", "", false},
		"bad chars":                {`,"plan":"pro; drop"`, "", "", false},
		"invalid plan, ok persona": {`,"plan":"-x","persona":"creator"`, "<nil>", "creator", true},
	} {
		t.Run(name, func(t *testing.T) {
			h := setup(t, true)
			rows := observePersonas(t)
			if rec := signupReq(signupWith(tc.member)); rec.Code != 202 {
				t.Fatalf("status = %d, want 202", rec.Code)
			}
			if u, _ := h.store.FindUserByEmail("new@example.com"); u == nil {
				t.Fatal("account not created")
			}
			if !tc.wantRow {
				if len(*rows) != 0 {
					t.Fatalf("rows = %d, want none", len(*rows))
				}
				return
			}
			if len(*rows) != 1 {
				t.Fatalf("rows = %d, want 1", len(*rows))
			}
			r := (*rows)[0]
			if got := str(r.PickedPlan); got != orNil(tc.wantPlan) || str(r.SuggestedPersona) != tc.wantPersona {
				t.Fatalf("plan %s persona %s, want %s / %s", got, str(r.SuggestedPersona), tc.wantPlan, tc.wantPersona)
			}
		})
	}
}

func orNil(s string) string {
	if s == "" {
		return "<nil>"
	}
	return s
}

func TestSignup_PickedPlanOfAnExistingAddressStoresNothing(t *testing.T) {
	setup(t, true)
	rows := observePersonas(t)
	signupReq(signupWith(`,"plan":"pro"`))
	if len(*rows) != 1 {
		t.Fatalf("rows = %d, want 1", len(*rows))
	}
	signupReq(signupWith(`,"plan":"team"`))
	if len(*rows) != 1 {
		t.Fatalf("an existing address stored a plan: %d rows", len(*rows))
	}
}
