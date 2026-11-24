package admin

import (
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/gorm"
)

// Admin side of the pending-address email change (issue #1207). The services are stubbed; the
// PostgreSQL behaviour is proven in services/email_change_pg_test.go. Names carry the threat-model
// row (plan.md) they prove.

type ecMail struct {
	to   string
	tmpl services.TemplateName
	data services.MailData
}

type ecHarness struct {
	mails     []ecMail
	requested []string
	cancelled []string
	tokens    int
	reqErr    error
	cancelErr error
	pending   map[uuid.UUID]services.PendingEmailChange
}

var ecTarget = uuid.MustParse("22222222-2222-2222-2222-222222222222")

func installEC(t *testing.T) *ecHarness {
	t.Helper()
	h := &ecHarness{pending: map[uuid.UUID]services.PendingEmailChange{}}
	oldReq, oldCancel, oldPending, oldCount, oldMail, oldAsync, oldGet := requestEmailChange, cancelEmailChange, pendingEmailChanges, countRecentTokens, sendMail, runAsync, getUserByID
	oldLink := config.Cfg.LinkBaseURL
	t.Cleanup(func() {
		requestEmailChange, cancelEmailChange, pendingEmailChanges, countRecentTokens, sendMail, runAsync, getUserByID = oldReq, oldCancel, oldPending, oldCount, oldMail, oldAsync, oldGet
		config.Cfg.LinkBaseURL = oldLink
	})
	config.Cfg.LinkBaseURL = "https://reelbolt.test"
	requestEmailChange = func(id uuid.UUID, newEmail string, actor services.AdminActor, now time.Time) (*services.EmailChangeRequested, error) {
		h.requested = append(h.requested, newEmail)
		if h.reqErr != nil {
			return nil, h.reqErr
		}
		return &services.EmailChangeRequested{
			User:         models.ApplicationUser{ID: id, Email: "current@example.com", DisplayName: "Ada"},
			PendingEmail: newEmail, ExpiresAt: now.Add(services.EmailChangeTTL), RawToken: "rawtoken123",
		}, nil
	}
	cancelEmailChange = func(id uuid.UUID, via string, actor services.AdminActor, now time.Time) error {
		h.cancelled = append(h.cancelled, via)
		return h.cancelErr
	}
	pendingEmailChanges = func(ids []uuid.UUID, now time.Time) (map[uuid.UUID]services.PendingEmailChange, error) {
		return h.pending, nil
	}
	countRecentTokens = countFunc(func(uuid.UUID, time.Time) (int, error) { return h.tokens, nil })
	sendMail = func(to string, tmpl services.TemplateName, data services.MailData) error {
		h.mails = append(h.mails, ecMail{to, tmpl, data})
		return nil
	}
	runAsync = func(f func()) { f() }
	return h
}

type countFunc func(uuid.UUID, time.Time) (int, error)

func (f countFunc) CountRecentTokens(id uuid.UUID, since time.Time) (int, error) { return f(id, since) }

func ecReq(method, body string) *http.Request {
	r := adminReq(method, "/users/"+ecTarget.String()+"/email-change", body)
	if body != "" {
		r.Header.Set("Content-Type", "application/json")
	}
	return r
}

func errCode(t *testing.T, body string) string {
	t.Helper()
	var m map[string]string
	_ = json.Unmarshal([]byte(body), &m)
	return m["code"]
}

// TM1 + TM6 + TM2/TM3: the request answers 202 and mails ONLY the current address, with the token
// in the URL fragment of the confirm page; nothing goes to the pending address.
func TestAdminEmailChange_TM1_TM6_MailsOnlyCurrentAddressWithFragmentLink(t *testing.T) {
	h := installEC(t)
	rec := serveAdmin(ecReq(http.MethodPost, `{"newEmail":"  New@Example.COM "}`))
	if rec.Code != http.StatusAccepted {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	var body RequestEmailChangeResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil || body.PendingEmail != "new@example.com" || body.ExpiresAt.IsZero() {
		t.Fatalf("body %s", rec.Body)
	}
	if strings.Contains(rec.Body.String(), "rawtoken123") {
		t.Fatal("the token is in the response")
	}
	if len(h.requested) != 1 || h.requested[0] != "new@example.com" {
		t.Fatalf("service got %v", h.requested)
	}
	if len(h.mails) != 1 {
		t.Fatalf("%d mails", len(h.mails))
	}
	m := h.mails[0]
	if m.to != "current@example.com" || m.tmpl != services.TemplateEmailChangeConfirm || m.data.NewEmail != "new@example.com" {
		t.Fatalf("mail %+v", m)
	}
	if m.data.Link != "https://reelbolt.test/app/email-change#token=rawtoken123" || strings.Contains(m.data.Link, "?") {
		t.Fatalf("link %q", m.data.Link)
	}
}

// TM11 and the other refusals map to their stable codes.
func TestAdminEmailChange_TM11_RefusalCodes(t *testing.T) {
	for _, tc := range []struct {
		err    error
		status int
		code   string
	}{
		{gorm.ErrRecordNotFound, http.StatusNotFound, ""},
		{services.ErrEmailChangeCurrentUnverified, http.StatusConflict, "EMAIL_CHANGE_CURRENT_UNVERIFIED"},
		{services.ErrEmailUnchanged, http.StatusConflict, "EMAIL_UNCHANGED"},
		{services.ErrEmailInUse, http.StatusConflict, "EMAIL_IN_USE"},
		{services.ErrEmailChangeRateLimited, http.StatusTooManyRequests, "EMAIL_CHANGE_RATE_LIMITED"},
	} {
		h := installEC(t)
		h.reqErr = tc.err
		rec := serveAdmin(ecReq(http.MethodPost, `{"newEmail":"new@example.com"}`))
		if rec.Code != tc.status || errCode(t, rec.Body.String()) != tc.code {
			t.Errorf("%v: got %d %s", tc.err, rec.Code, rec.Body)
		}
		if len(h.mails) != 0 {
			t.Errorf("%v: a mail was sent", tc.err)
		}
	}
}

// An invalid or reserved address is 400 EMAIL_INVALID before the service runs.
func TestAdminEmailChange_InvalidAddress(t *testing.T) {
	for _, addr := range []string{"", "not-an-address", "apikey+x@service.invalid"} {
		h := installEC(t)
		rec := serveAdmin(ecReq(http.MethodPost, `{"newEmail":"`+addr+`"}`))
		if rec.Code != http.StatusBadRequest || errCode(t, rec.Body.String()) != "EMAIL_INVALID" || len(h.requested) != 0 {
			t.Errorf("%q: got %d %s", addr, rec.Code, rec.Body)
		}
	}
}

// TM9: the per-user emailed-token limiter (all purposes) also refuses the request.
func TestAdminEmailChange_TM9_PerUserTokenLimiter(t *testing.T) {
	h := installEC(t)
	h.tokens = 3
	rec := serveAdmin(ecReq(http.MethodPost, `{"newEmail":"new@example.com"}`))
	if rec.Code != http.StatusTooManyRequests || errCode(t, rec.Body.String()) != "EMAIL_CHANGE_RATE_LIMITED" {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	if len(h.requested) != 0 || len(h.mails) != 0 {
		t.Fatal("the request went through")
	}
}

// TM2: the request route takes JSON only.
func TestAdminEmailChange_TM2_RequiresJSON(t *testing.T) {
	h := installEC(t)
	r := adminReq(http.MethodPost, "/users/"+ecTarget.String()+"/email-change", `{"newEmail":"new@example.com"}`)
	r.Header.Set("Content-Type", "text/plain")
	if rec := serveAdmin(r); rec.Code != http.StatusUnsupportedMediaType || len(h.requested) != 0 {
		t.Fatalf("got %d", rec.Code)
	}
}

// Cancel by an admin: 204 (idempotent), via "admin"; 404 for a missing user.
func TestAdminEmailChange_Cancel(t *testing.T) {
	h := installEC(t)
	if rec := serveAdmin(ecReq(http.MethodDelete, "")); rec.Code != http.StatusNoContent {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	if len(h.cancelled) != 1 || h.cancelled[0] != services.EmailChangeViaAdmin {
		t.Fatalf("cancel via %v", h.cancelled)
	}
	h.cancelErr = gorm.ErrRecordNotFound
	if rec := serveAdmin(ecReq(http.MethodDelete, "")); rec.Code != http.StatusNotFound {
		t.Fatalf("missing user: %d", rec.Code)
	}
	h.cancelErr = errors.New("db down")
	if rec := serveAdmin(ecReq(http.MethodDelete, "")); rec.Code != http.StatusInternalServerError {
		t.Fatalf("db error: %d", rec.Code)
	}
}

// GET /users/{id} carries emailVerified and pendingEmailChange.
func TestAdminGetUser_EmailVerifiedAndPending(t *testing.T) {
	h := installEC(t)
	verified := time.Now()
	getUserByID = func(id uuid.UUID) (*models.ApplicationUser, error) {
		return &models.ApplicationUser{ID: id, Email: "current@example.com", EmailVerifiedAt: &verified}, nil
	}
	h.pending[ecTarget] = services.PendingEmailChange{NewEmail: "new@example.com", RequestedAt: verified, ExpiresAt: verified.Add(time.Hour)}
	rec := serveAdmin(adminReq(http.MethodGet, "/users/"+ecTarget.String(), ""))
	var got UserResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil || rec.Code != http.StatusOK {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	if !got.EmailVerified || got.PendingEmailChange == nil || got.PendingEmailChange.NewEmail != "new@example.com" {
		t.Fatalf("response %+v", got)
	}
	delete(h.pending, ecTarget)
	rec = serveAdmin(adminReq(http.MethodGet, "/users/"+ecTarget.String(), ""))
	if !strings.Contains(rec.Body.String(), `"pendingEmailChange":null`) {
		t.Fatalf("no pending: %s", rec.Body)
	}
}

// PUT with a changed email is still refused, and the message names the new action.
func TestAdminUpdateEmail_PointsToEmailChangeAction(t *testing.T) {
	installEC(t)
	getUserByID = func(id uuid.UUID) (*models.ApplicationUser, error) {
		return &models.ApplicationUser{ID: id, Email: "current@example.com"}, nil
	}
	rec := serveAdmin(adminReq(http.MethodPut, "/users/"+ecTarget.String(), `{"email":"other@example.com"}`))
	if rec.Code != http.StatusConflict || errCode(t, rec.Body.String()) != "EMAIL_CHANGE_REQUIRES_USER" ||
		!strings.Contains(rec.Body.String(), "email-change") {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
}

// PUT /users/{id} carries the open request too; a failed lookup answers null, not an error.
func TestAdminUpdateUser_PendingEmailChange(t *testing.T) {
	h := installEC(t)
	getUserByID = func(id uuid.UUID) (*models.ApplicationUser, error) {
		return &models.ApplicationUser{ID: id, Email: "current@example.com"}, nil
	}
	h.pending[ecTarget] = services.PendingEmailChange{NewEmail: "new@example.com", ExpiresAt: time.Now().Add(time.Hour)}
	rec := serveAdmin(adminReq(http.MethodPut, "/users/"+ecTarget.String(), `{}`))
	if rec.Code != http.StatusOK || !strings.Contains(rec.Body.String(), `"newEmail":"new@example.com"`) {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	pendingEmailChanges = func([]uuid.UUID, time.Time) (map[uuid.UUID]services.PendingEmailChange, error) {
		return nil, errors.New("db down")
	}
	rec = serveAdmin(adminReq(http.MethodPut, "/users/"+ecTarget.String(), `{}`))
	if rec.Code != http.StatusOK || !strings.Contains(rec.Body.String(), `"pendingEmailChange":null`) {
		t.Fatalf("lookup error: %d %s", rec.Code, rec.Body)
	}
}
