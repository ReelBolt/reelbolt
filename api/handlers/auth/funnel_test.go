package auth

import (
	"context"
	"database/sql"
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"github.com/vecchiotom/reelbolt/telemetry"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"
)

// The alpha funnel's signup event (alpha-launch T-018). The browser cannot emit it: the signup
// endpoint answers a uniform 202 whether or not the address already had an account, so the client
// cannot know one was created — and firing on the 202 would count an address that already had one.
// The Go handler is the only place that knows, so it emits there, and only when a row was written.

// observeFunnelEvents swaps the emitter for a recorder and returns what it collects.
func observeFunnelEvents(t *testing.T) *[]string {
	t.Helper()
	old := recordFunnelEvent
	t.Cleanup(func() { recordFunnelEvent = old })
	events := &[]string{}
	recordFunnelEvent = func(_ context.Context, event string) { *events = append(*events, event) }
	return events
}

func TestSignup_EmitsAlphaSignupOnceTheAccountIsCreated(t *testing.T) {
	setup(t, true)
	events := observeFunnelEvents(t)

	rec := post(handleSignup, goodSignup)
	if rec.Code != 202 {
		t.Fatalf("status = %d, want 202", rec.Code)
	}
	if len(*events) != 1 || (*events)[0] != telemetry.FunnelEventSignup {
		t.Fatalf("funnel events = %v, want exactly one %q", *events, telemetry.FunnelEventSignup)
	}
}

func TestSignup_DuplicateAddressEmitsNoFunnelEventAndKeepsTheUniform202(t *testing.T) {
	setup(t, true)
	if rec := post(handleSignup, goodSignup); rec.Code != 202 {
		t.Fatalf("first signup status = %d, want 202", rec.Code)
	}
	events := observeFunnelEvents(t)

	rec := post(handleSignup, goodSignup)

	// The anti-enumeration property is the point: the second answer is byte-identical to the first.
	if rec.Code != 202 {
		t.Fatalf("duplicate signup status = %d, want 202", rec.Code)
	}
	if !strings.Contains(rec.Body.String(), acceptedMessage) {
		t.Fatalf("duplicate signup body = %q, want the uniform accepted message", rec.Body.String())
	}
	if len(*events) != 0 {
		t.Fatalf("funnel events = %v, want none: no account row was written", *events)
	}
}

func TestSignup_RefusedSignupEmitsNoFunnelEvent(t *testing.T) {
	setup(t, true)
	events := observeFunnelEvents(t)

	// A valid body with no terms acceptance is refused before anything is written.
	rec := post(handleSignup, `{"email":"new@example.com","password":"correct horse","acceptTerms":false}`)

	if rec.Code != 400 {
		t.Fatalf("status = %d, want 400", rec.Code)
	}
	if len(*events) != 0 {
		t.Fatalf("funnel events = %v, want none", *events)
	}
}

// A dead database must never break signup: the attribution insert is best-effort.
func TestSignup_AttributionInsertFailureDoesNotBreakSignup(t *testing.T) {
	setup(t, true)
	sqlDB, err := sql.Open("pgx", "postgres://u:p@127.0.0.1:1/x?sslmode=disable&connect_timeout=1")
	if err != nil {
		t.Fatal(err)
	}
	db, err := gorm.Open(postgres.New(postgres.Config{Conn: sqlDB, WithoutQuotingCheck: true}), &gorm.Config{SkipDefaultTransaction: true, DisableAutomaticPing: true})
	if err != nil {
		t.Fatal(err)
	}
	oldDB := database.DB
	database.DB = db
	t.Cleanup(func() { database.DB = oldDB })

	ft := base64.RawURLEncoding.EncodeToString([]byte(`{"v":1,"s":"ads"}`))
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/x", strings.NewReader(goodSignup))
	req.Header.Set("X-Real-IP", "203.0.113.9")
	req.AddCookie(&http.Cookie{Name: "rb_ft", Value: ft})
	rec := httptest.NewRecorder()
	handleSignup(rec, req)
	if rec.Code != 202 {
		t.Fatalf("status = %d, want 202", rec.Code)
	}
	cleared := false
	for _, c := range rec.Result().Cookies() {
		if c.Name == "rb_ft" && c.MaxAge < 0 && c.Path == "/" {
			cleared = true
		}
	}
	if !cleared {
		t.Fatal("rb_ft was not cleared")
	}
}

func TestSignup_NoCookieNoAttributionAndNoSetCookie(t *testing.T) {
	setup(t, true)
	called := false
	old := recordSignupAttribution
	t.Cleanup(func() { recordSignupAttribution = old })
	recordSignupAttribution = func(w http.ResponseWriter, r *http.Request, id uuid.UUID, carried services.URLAttribution) {
		called = true
		old(w, r, id, carried)
	}
	rec := post(handleSignup, goodSignup)
	if rec.Code != 202 || !called {
		t.Fatalf("status %d called %v", rec.Code, called)
	}
	for _, c := range rec.Result().Cookies() {
		if c.Name == "rb_ft" {
			t.Fatal("rb_ft must not be touched when absent")
		}
	}
}

// Enumeration safety: a new and an already-registered address answer identically, cookie or not.
func TestSignup_NewAndExistingAnswerIdentically(t *testing.T) {
	for _, withCookie := range []bool{false, true} {
		setup(t, true)
		do := func(body string) *httptest.ResponseRecorder {
			req := httptest.NewRequestWithContext(context.Background(), "POST", "/x", strings.NewReader(body))
			req.Header.Set("X-Real-IP", "203.0.113.9")
			if withCookie {
				req.AddCookie(&http.Cookie{Name: "rb_ft", Value: base64.RawURLEncoding.EncodeToString([]byte(`{"v":1,"s":"ads"}`))})
			}
			rec := httptest.NewRecorder()
			handleSignup(rec, req)
			return rec
		}
		first := do(goodSignup)
		second := do(goodSignup)
		if first.Code != second.Code || first.Body.String() != second.Body.String() {
			t.Fatalf("cookie=%v: status/body differ: %d %q vs %d %q", withCookie, first.Code, first.Body, second.Code, second.Body)
		}
		if h1, h2 := first.Header(), second.Header(); !reflect.DeepEqual(h1, h2) {
			t.Fatalf("cookie=%v: headers differ: %v vs %v", withCookie, h1, h2)
		}
		if withCookie && len(first.Result().Cookies()) == 0 {
			t.Fatal("rb_ft should be cleared on both")
		}
	}
}

const attributionCookieJSON = `{"v":1,"s":" Ads ","r":"news.example.com","l":"/Pricing?x=1","vid":"3f2b8c1e-5a4d-4e6f-9b7a-1c2d3e4f5a6b"}`

// observeAttribution captures attempted user_attribution inserts. database.DB must be non-nil for
// the best-effort writer to run, so a dummy handle is installed; the writer itself is replaced.
func observeAttribution(t *testing.T) *[]*models.UserAttribution {
	t.Helper()
	rows := &[]*models.UserAttribution{}
	restore := services.ObserveAttributionWrites(func(r *models.UserAttribution) { *rows = append(*rows, r) })
	oldDB := database.DB
	sqlDB, err := sql.Open("pgx", "postgres://u:p@127.0.0.1:1/x?sslmode=disable&connect_timeout=1")
	if err != nil {
		t.Fatal(err)
	}
	db, err := gorm.Open(postgres.New(postgres.Config{Conn: sqlDB}), &gorm.Config{SkipDefaultTransaction: true, DisableAutomaticPing: true})
	if err != nil {
		t.Fatal(err)
	}
	database.DB = db
	t.Cleanup(func() { restore(); database.DB = oldDB })
	return rows
}

func assertAttributionRow(t *testing.T, rows []*models.UserAttribution, rec *httptest.ResponseRecorder) {
	t.Helper()
	if len(rows) != 1 {
		t.Fatalf("attribution inserts = %d, want 1", len(rows))
	}
	r := rows[0]
	if r.SourceLabel != "ads" || r.ReferrerHost == nil || *r.ReferrerHost != "news.example.com" ||
		r.LandingPath == nil || *r.LandingPath != "/pricing" || r.VisitorID == nil {
		t.Fatalf("row not sanitised as expected: %+v", r)
	}
	for _, c := range rec.Result().Cookies() {
		if c.Name == "rb_ft" && c.MaxAge < 0 && c.Path == "/" {
			return
		}
	}
	t.Fatal("rb_ft was not cleared")
}

func ftCookie() *http.Cookie {
	return &http.Cookie{Name: "rb_ft", Value: base64.RawURLEncoding.EncodeToString([]byte(attributionCookieJSON))}
}

func TestInviteSignup_RecordsAttributionAndClearsCookie(t *testing.T) {
	e := newInviteEnv(t)
	rows := observeAttribution(t)
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/api/v1/auth/signup",
		strings.NewReader(inviteSignupBody("New@Example.com", e.token)))
	req.Header.Set("X-Real-IP", "203.0.113.9")
	req.Header.Set("Content-Type", "application/json")
	req.AddCookie(ftCookie())
	rec := httptest.NewRecorder()
	e.router.ServeHTTP(rec, req)
	if rec.Code >= 300 {
		t.Fatalf("invite signup got %d %s", rec.Code, rec.Body)
	}
	assertAttributionRow(t, *rows, rec)
}

func TestOAuthFirstSignIn_RecordsAttributionAndClearsCookie(t *testing.T) {
	oauthSetup(t)
	f := newFakeProvider(t)
	f.userInfo = `{"sub":"123","email":"ada@example.com","email_verified":true,"name":"Ada"}`
	installGoogle(t, f)
	rows := observeAttribution(t)
	req := httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/auth/oauth/google/callback?code=c&state=st-1", nil)
	req.Header.Set("X-Real-IP", "203.0.113.9")
	req = mux.SetURLVars(req, map[string]string{"provider": "google"})
	req.AddCookie(&http.Cookie{Name: oauthStateCookie, Value: "google:st-1:verifier-abc"})
	req.AddCookie(ftCookie())
	rec := httptest.NewRecorder()
	handleOAuthCallback(rec, req)
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	assertAttributionRow(t, *rows, rec)
}
