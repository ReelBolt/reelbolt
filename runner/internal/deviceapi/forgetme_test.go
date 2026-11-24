package deviceapi

import (
	"context"
	"errors"
	"net/http"
	"testing"
)

// ForgetMe is a signed DELETE of /api/v1/device/me with no body; the verifying
// server checks the signature exactly as the Inference API does.
func TestForgetMeIsASignedDeleteOfMe(t *testing.T) {
	var method, path string
	var body []byte
	_, c := verifyingServer(t, func(w http.ResponseWriter, r *http.Request, b []byte) {
		method, path, body = r.Method, r.URL.Path, b
		w.WriteHeader(http.StatusNoContent)
	})
	if err := c.ForgetMe(context.Background()); err != nil {
		t.Fatalf("ForgetMe: %v", err)
	}
	if method != http.MethodDelete || path != "/api/v1/device/me" || len(body) != 0 {
		t.Fatalf("sent %s %s with %d body bytes", method, path, len(body))
	}
}

// The two answers a caller tells apart: no longer accepted (already removed),
// and a server that has no such route (not removed).
func TestForgetMeReportsTheAnswersACallerActsOn(t *testing.T) {
	cases := []struct {
		status int
		body   string
		want   error
	}{
		{http.StatusUnauthorized, `{"error":"device_unauthorized"}`, ErrNotConnected},
		{http.StatusNotFound, ``, ErrNotFound},
	}
	for _, tc := range cases {
		_, c := verifyingServer(t, func(w http.ResponseWriter, _ *http.Request, _ []byte) {
			w.WriteHeader(tc.status)
			_, _ = w.Write([]byte(tc.body))
		})
		if err := c.ForgetMe(context.Background()); !errors.Is(err, tc.want) {
			t.Errorf("HTTP %d: got %v, want %v", tc.status, err, tc.want)
		}
	}
	_, c := verifyingServer(t, func(w http.ResponseWriter, _ *http.Request, _ []byte) {
		w.WriteHeader(http.StatusInternalServerError)
	})
	err := c.ForgetMe(context.Background())
	if err == nil || errors.Is(err, ErrNotConnected) || errors.Is(err, ErrNotFound) {
		t.Fatalf("a 500 = %v, want an error that claims neither", err)
	}
}
