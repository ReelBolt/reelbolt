package middleware

// Tests for the JSON-only content-type layer (C11 F-6). They pin the property the
// finding says did not exist: a cross-site request with a body and a
// non-preflighted content type never reaches a state-changing handler.

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

const jsonBody = `{"password":"hunter2hunter2"}`

func TestRequireJSON_RefusesABodyThatIsNotJSON(t *testing.T) {
	for _, tc := range []struct {
		name        string
		contentType string
		setHeader   bool
		body        string
		length      int64
		wantStatus  int
		wantCalled  bool
	}{
		{name: "application/json", contentType: "application/json", setHeader: true, body: jsonBody, wantStatus: 200, wantCalled: true},
		{name: "application/json with charset", contentType: "application/json; charset=utf-8", setHeader: true, body: jsonBody, wantStatus: 200, wantCalled: true},
		{name: "a +json media type", contentType: "application/vnd.reelbolt+json", setHeader: true, body: jsonBody, wantStatus: 200, wantCalled: true},
		{name: "an upper-case media type", contentType: "APPLICATION/JSON", setHeader: true, body: jsonBody, wantStatus: 200, wantCalled: true},
		// The cross-site shapes the finding is about: a fetch with text/plain
		// needs no preflight, so only this middleware stops it.
		{name: "text/plain carrying JSON", contentType: "text/plain", setHeader: true, body: jsonBody, wantStatus: 415},
		{name: "a form post", contentType: "application/x-www-form-urlencoded", setHeader: true, body: "a=b", wantStatus: 415},
		{name: "a multipart post", contentType: "multipart/form-data; boundary=X", setHeader: true, body: "--X--", wantStatus: 415},
		{name: "no content type at all", body: jsonBody, wantStatus: 415},
		{name: "a type that only looks like JSON", contentType: "application/jsonx", setHeader: true, body: jsonBody, wantStatus: 415},
		// No body: nothing to smuggle, so the handler keeps its own answer.
		{name: "an empty body without a content type", wantStatus: 200, wantCalled: true},
		{name: "an empty body with a non-JSON content type", contentType: "text/plain", setHeader: true, wantStatus: 200, wantCalled: true},
		{name: "a chunked JSON body", contentType: "application/json", setHeader: true, body: jsonBody, length: -1, wantStatus: 200, wantCalled: true},
		{name: "a chunked body that is not JSON", contentType: "text/plain", setHeader: true, body: jsonBody, length: -1, wantStatus: 415},
	} {
		t.Run(tc.name, func(t *testing.T) {
			called := false
			next := func(w http.ResponseWriter, _ *http.Request) {
				called = true
				w.WriteHeader(http.StatusOK)
			}

			var req *http.Request
			if tc.body == "" {
				req = httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/api/v1/auth/account/delete", nil)
			} else {
				req = httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/api/v1/auth/account/delete", strings.NewReader(tc.body))
			}
			if tc.length != 0 {
				req.ContentLength = tc.length
			}
			if tc.setHeader {
				req.Header.Set("Content-Type", tc.contentType)
			}
			rec := httptest.NewRecorder()
			RequireJSON(next)(rec, req)

			if rec.Code != tc.wantStatus {
				t.Fatalf("status %d, want %d (%s)", rec.Code, tc.wantStatus, rec.Body.String())
			}
			if called != tc.wantCalled {
				t.Fatalf("handler called = %v, want %v", called, tc.wantCalled)
			}
			if tc.wantStatus == http.StatusUnsupportedMediaType {
				// The refusal must be machine-readable and say what is accepted.
				var body map[string]string
				if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
					t.Fatalf("the refusal is not JSON: %q (%v)", rec.Body.String(), err)
				}
				if body["error"] != "unsupported media type" || !strings.Contains(body["message"], "application/json") {
					t.Fatalf("refusal body %v", body)
				}
				if got := rec.Header().Get("Content-Type"); got != "application/json" {
					t.Fatalf("refusal content type %q", got)
				}
			}
		})
	}
}

// A GET carries no body, so the middleware is transparent to it — the SSE
// streams and every read route keep working wherever it is applied.
func TestRequireJSON_LeavesAReadAlone(t *testing.T) {
	called := false
	rec := httptest.NewRecorder()
	req := httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/api/v1/workflows/events", nil)
	RequireJSON(func(w http.ResponseWriter, _ *http.Request) {
		called = true
		w.WriteHeader(http.StatusOK)
	})(rec, req)
	if !called || rec.Code != http.StatusOK {
		t.Fatalf("called=%v status=%d", called, rec.Code)
	}
}
