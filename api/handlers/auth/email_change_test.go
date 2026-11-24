package auth

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// The user's side of the pending-address email change (issue #1207). The services are stubbed;
// the PostgreSQL behaviour is proven in services/email_change_pg_test.go. Names carry the
// threat-model row (plan.md) they prove.

type ecCalls struct {
	hashes      []string
	invalidated []uuid.UUID
	cancelVia   []string
	cancelActor []services.AdminActor
}

func installEmailChange(t *testing.T, err error) (*harness, *ecCalls) {
	t.Helper()
	h := setup(t, true)
	old := emailChange
	t.Cleanup(func() { emailChange = old })
	c := &ecCalls{}
	user := models.ApplicationUser{ID: uuid.New(), Email: "new@example.com", DisplayName: "Ada"}
	emailChange.preview = func(hash string, now time.Time) (*services.EmailChangePreview, error) {
		c.hashes = append(c.hashes, hash)
		if err != nil {
			return nil, err
		}
		return &services.EmailChangePreview{CurrentEmail: "alice@example.com", NewEmail: "new@example.com", ExpiresAt: now.Add(time.Hour)}, nil
	}
	emailChange.confirm = func(hash string, actor services.AdminActor, now time.Time) (*services.EmailChangeConfirmed, error) {
		c.hashes = append(c.hashes, hash)
		if err != nil {
			return nil, err
		}
		return &services.EmailChangeConfirmed{User: user, OldEmail: "alice@example.com"}, nil
	}
	emailChange.cancelByToken = func(hash string, actor services.AdminActor, now time.Time) error {
		c.hashes = append(c.hashes, hash)
		return err
	}
	emailChange.cancel = func(id uuid.UUID, via string, actor services.AdminActor, now time.Time) error {
		c.cancelVia = append(c.cancelVia, via)
		c.cancelActor = append(c.cancelActor, actor)
		return nil
	}
	emailChange.invalidateUser = func(id uuid.UUID) { c.invalidated = append(c.invalidated, id) }
	h.store.users[user.ID] = &user
	return h, c
}

func ecServe(method, path, contentType, body string) *httptest.ResponseRecorder {
	router := mux.NewRouter()
	RegisterAuthRoutes(router)
	req := httptest.NewRequestWithContext(context.Background(), method, path, strings.NewReader(body))
	if contentType != "" {
		req.Header.Set("Content-Type", contentType)
	}
	rec := httptest.NewRecorder()
	router.ServeHTTP(rec, req)
	return rec
}

var ecPaths = []string{"/api/v1/auth/email-change/preview", "/api/v1/auth/email-change/confirm", "/api/v1/auth/email-change/cancel"}

// TM2 + TM3: the token is accepted only in a JSON POST body. A GET (what a link scanner or prefetch
// sends) is not routed, a non-JSON body is 415, and a token in the query string is ignored.
func TestEmailChange_TM2_TM3_TokenOnlyInJSONPostBody(t *testing.T) {
	_, c := installEmailChange(t, nil)
	for _, p := range ecPaths {
		if rec := ecServe(http.MethodGet, p+"?token=abc", "", ""); rec.Code != http.StatusMethodNotAllowed {
			t.Errorf("GET %s: %d", p, rec.Code)
		}
		if rec := ecServe(http.MethodPost, p, "text/plain", `{"token":"abc"}`); rec.Code != http.StatusUnsupportedMediaType {
			t.Errorf("text/plain %s: %d", p, rec.Code)
		}
		if rec := ecServe(http.MethodPost, p+"?token=abc", "", ""); rec.Code != http.StatusBadRequest ||
			!strings.Contains(rec.Body.String(), "EMAIL_CHANGE_TOKEN_INVALID") {
			t.Errorf("query token %s: %d %s", p, rec.Code, rec.Body)
		}
	}
	if len(c.hashes) != 0 {
		t.Fatalf("a service ran without a body token: %v", c.hashes)
	}
	// Only the hash reaches the service.
	ecServe(http.MethodPost, ecPaths[0], "application/json", `{"token":" abc "}`)
	if len(c.hashes) != 1 || c.hashes[0] != services.HashToken("abc") {
		t.Fatalf("hashes %v", c.hashes)
	}
}

// TM10: every token failure, and an internal error too, gets the same 400 body.
func TestEmailChange_TM10_UniformTokenInvalid(t *testing.T) {
	var bodies []string
	for _, err := range []error{services.ErrInvalidToken, errors.New("db down")} {
		installEmailChange(t, err)
		for _, p := range ecPaths {
			rec := ecServe(http.MethodPost, p, "application/json", `{"token":"abc"}`)
			if rec.Code != http.StatusBadRequest {
				t.Errorf("%s %v: %d", p, err, rec.Code)
			}
			bodies = append(bodies, rec.Body.String())
		}
	}
	for _, malformed := range []string{`{}`, `{"token":""}`, `not json`} {
		rec := ecServe(http.MethodPost, ecPaths[1], "application/json", malformed)
		bodies = append(bodies, rec.Body.String())
	}
	for _, b := range bodies {
		if b != bodies[0] {
			t.Fatalf("bodies differ: %q vs %q", b, bodies[0])
		}
	}
	if errCodeOf(bodies[0]) != "EMAIL_CHANGE_TOKEN_INVALID" {
		t.Fatalf("body %s", bodies[0])
	}
}

func errCodeOf(body string) string {
	var m map[string]string
	_ = json.Unmarshal([]byte(body), &m)
	return m["code"]
}

// TM1 + TM5: a confirm issues no session, ends the user's cached sessions, sends the notice to the
// OLD address and a verify link to the NEW one, bound to it (sent_to_email = new).
func TestEmailChange_TM1_TM5_ConfirmNoSessionNoticeOldVerifyNew(t *testing.T) {
	h, c := installEmailChange(t, nil)
	rec := ecServe(http.MethodPost, ecPaths[1], "application/json", `{"token":"abc"}`)
	if rec.Code != http.StatusNoContent {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	if len(rec.Result().Cookies()) != 0 || rec.Body.Len() != 0 {
		t.Fatal("confirm issued a session or a body")
	}
	if len(c.invalidated) != 1 {
		t.Fatal("sessions not invalidated")
	}
	if len(h.mails) != 2 {
		t.Fatalf("%d mails: %+v", len(h.mails), h.mails)
	}
	if m := h.mails[0]; m.to != "alice@example.com" || m.tmpl != services.TemplateEmailChangeDone || m.data.NewEmail != "new@example.com" {
		t.Fatalf("notice %+v", m)
	}
	if m := h.mails[1]; m.to != "new@example.com" || m.tmpl != services.TemplateVerifyEmail {
		t.Fatalf("verify %+v", m)
	}
	var bound bool
	for _, tok := range h.store.tokens {
		if tok.Purpose == models.UserTokenVerifyEmail && tok.SentToEmail != nil && *tok.SentToEmail == "new@example.com" {
			bound = true
		}
	}
	if !bound {
		t.Fatal("the verify token is not bound to the new address")
	}
}

// TM8: the new address taken meanwhile is 409 EMAIL_IN_USE, nothing mailed, no session ended.
func TestEmailChange_TM8_ConfirmInUse(t *testing.T) {
	h, c := installEmailChange(t, services.ErrEmailInUse)
	rec := ecServe(http.MethodPost, ecPaths[1], "application/json", `{"token":"abc"}`)
	if rec.Code != http.StatusConflict || errCodeOf(rec.Body.String()) != "EMAIL_IN_USE" {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	if len(h.mails) != 0 || len(c.invalidated) != 0 {
		t.Fatal("side effects on a refused confirm")
	}
}

// Preview masks the current address and returns the new one; cancel by link is 204.
func TestEmailChange_PreviewMasksAndCancelIs204(t *testing.T) {
	installEmailChange(t, nil)
	rec := ecServe(http.MethodPost, ecPaths[0], "application/json", `{"token":"abc"}`)
	var p EmailChangePreviewResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &p); err != nil || rec.Code != http.StatusOK {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	if p.CurrentEmailMasked != "a***@example.com" || p.NewEmail != "new@example.com" || strings.Contains(rec.Body.String(), "alice") {
		t.Fatalf("preview %s", rec.Body)
	}
	if rec := ecServe(http.MethodPost, ecPaths[2], "application/json", `{"token":"abc"}`); rec.Code != http.StatusNoContent {
		t.Fatalf("cancel %d", rec.Code)
	}
}

// The signed-in user cancels their own request: 204, via "user", actor = the user.
func TestEmailChange_CancelSelf(t *testing.T) {
	_, c := installEmailChange(t, nil)
	id := uuid.New()
	req := httptest.NewRequestWithContext(context.Background(), http.MethodDelete, "/api/v1/auth/email-change", nil)
	req = req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey, middleware.UserContext{UserID: id.String()}))
	router := mux.NewRouter()
	RegisterAuthenticatedRoutes(router)
	rec := httptest.NewRecorder()
	router.ServeHTTP(rec, req)
	if rec.Code != http.StatusNoContent {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	if len(c.cancelVia) != 1 || c.cancelVia[0] != services.EmailChangeViaUser || c.cancelActor[0].UserID == nil || *c.cancelActor[0].UserID != id {
		t.Fatalf("cancel %+v %+v", c.cancelVia, c.cancelActor)
	}
}

// GET settings carries the open request.
func TestGetSettings_PendingEmailChange(t *testing.T) {
	h := newSettingsHarness(t, false)
	exp := time.Date(2026, 11, 17, 12, 0, 0, 0, time.UTC)
	settings.pendingEmailChange = func(uuid.UUID, time.Time) (*services.PendingEmailChange, error) {
		return &services.PendingEmailChange{NewEmail: "new@example.com", ExpiresAt: exp}, nil
	}
	rec := httptest.NewRecorder()
	handleGetSettings(rec, h.sessionRequest(http.MethodGet, ""))
	var got SettingsResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatal(err)
	}
	if got.PendingEmailChange == nil || got.PendingEmailChange.NewEmail != "new@example.com" || !got.PendingEmailChange.ExpiresAt.Equal(exp) {
		t.Fatalf("settings %s", rec.Body)
	}
}

// The post-confirm verify mail is server-initiated, once per confirm: the per-user emailed-token
// limiter must not drop it, or the new address is left unverified with no link.
func TestEmailChange_ConfirmVerifyMailIgnoresTokenLimiter(t *testing.T) {
	h, _ := installEmailChange(t, nil)
	var uid uuid.UUID
	for id := range h.store.users {
		uid = id
	}
	for i := 0; i < 3; i++ {
		h.store.tokens = append(h.store.tokens, &models.UserToken{
			ID: uuid.New(), UserID: uid, Purpose: models.UserTokenEmailChange, CreatedAt: h.now, ExpiresAt: h.now.Add(time.Hour),
		})
	}
	rec := ecServe(http.MethodPost, ecPaths[1], "application/json", `{"token":"abc"}`)
	if rec.Code != http.StatusNoContent {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	var sent bool
	for _, m := range h.mails {
		if m.to == "new@example.com" && m.tmpl == services.TemplateVerifyEmail {
			sent = true
		}
	}
	if !sent {
		t.Fatalf("verify mail to the new address dropped: %+v", h.mails)
	}
	var bound bool
	for _, tok := range h.store.tokens {
		if tok.Purpose == models.UserTokenVerifyEmail && tok.SentToEmail != nil && *tok.SentToEmail == "new@example.com" {
			bound = true
		}
	}
	if !bound {
		t.Fatal("verify token not bound to the new address")
	}
}
