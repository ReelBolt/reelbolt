package auth

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/middleware"
)

// countingReader counts what a handler pulled from the request body.
type countingReader struct {
	r io.Reader
	n int
}

func (c *countingReader) Read(p []byte) (int, error) {
	n, err := c.r.Read(p)
	c.n += n
	return n, err
}

// API-GO-03: login, switch-org, change-password and account delete stop reading at 4 KB, like
// their siblings, instead of buffering whatever is sent (nginx allows 100 MB). The oversized
// field is the first one, so a handler that decoded it whole would go on to use it.
func TestAuthBodies_AreCapped(t *testing.T) {
	pad := strings.Repeat("a", 1<<20)
	for _, tc := range []struct {
		name   string
		handle http.HandlerFunc
		body   string
	}{
		{"token", handleToken, `{"email":"` + pad + `","password":"x"}`},
		{"switch-org", handleSwitchOrg, `{"organizationId":"` + pad + `"}`},
		{"change-password", handleChangePassword, `{"current_password":"` + pad + `","new_password":"x"}`},
		{"account delete", handleAccountDelete, `{"password":"` + pad + `"}`},
	} {
		body := &countingReader{r: strings.NewReader(tc.body)}
		req := httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/x", body)
		req = req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey,
			middleware.UserContext{UserID: uuid.NewString(), Email: "a@b.c"}))
		rec := httptest.NewRecorder()
		tc.handle(rec, req)

		if rec.Code != http.StatusBadRequest || !strings.Contains(rec.Body.String(), "invalid request body") {
			t.Errorf("%s: got %d %s, want 400 invalid request body", tc.name, rec.Code, rec.Body.String())
		}
		if body.n > 8<<10 {
			t.Errorf("%s: read %d bytes of an oversized body, cap is 4 KB", tc.name, body.n)
		}
	}
}

// A body of ordinary size is still decoded: the cap must not refuse a real request. Each
// handler answers with its own validation error, which proves it got past the decoder.
func TestAuthBodies_OrdinaryBodiesStillDecode(t *testing.T) {
	for _, tc := range []struct {
		name   string
		handle http.HandlerFunc
		body   string
		want   string
	}{
		{"token", handleToken, `{"email":"","password":""}`, "email and password are required"},
		{"switch-org", handleSwitchOrg, `{"organizationId":"nope"}`, "organizationId must be a uuid"},
		{"change-password", handleChangePassword, `{"current_password":"","new_password":""}`, "current_password and new_password are required"},
	} {
		req := httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/x", strings.NewReader(tc.body))
		req = req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey,
			middleware.UserContext{UserID: uuid.NewString(), Email: "a@b.c"}))
		rec := httptest.NewRecorder()
		tc.handle(rec, req)
		if !strings.Contains(rec.Body.String(), tc.want) {
			t.Errorf("%s: got %d %s, want %q", tc.name, rec.Code, rec.Body.String(), tc.want)
		}
	}
}
