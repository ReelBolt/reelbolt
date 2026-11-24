package ui

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/runner/internal/identity"
)

func newAPITest(t *testing.T) (*App, http.Handler, func()) {
	t.Helper()
	s, cleanup := newTestShell(t, func(o *Options) {
		o.PairingBaseURL = "http://127.0.0.1:1"
	})
	a := NewApp(s, AppOptions{
		OpenURL:   func(string) error { return nil },
		Autostart: &fakeAutostart{},
	})
	return a, Handler(a), cleanup
}

func TestAPIViewIsTheSameDocumentTheTrayRenders(t *testing.T) {
	a, h, cleanup := newAPITest(t)
	defer cleanup()
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/view", nil))
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d", rec.Code)
	}
	if ct := rec.Header().Get("Content-Type"); !strings.Contains(ct, "application/json") {
		t.Errorf("content type = %q", ct)
	}
	var got View
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("decode: %v", err)
	}
	want := a.View()
	if got.State != want.State || got.Headline != want.Headline {
		t.Errorf("the API view %q/%q differs from the shell view %q/%q",
			got.State, got.Headline, want.State, want.Headline)
	}
}

func TestAPIActionRunsThroughTheSameSwitchAsTheTray(t *testing.T) {
	_, h, cleanup := newAPITest(t)
	defer cleanup()
	body := strings.NewReader(`{"action":"pause","arg":"from the window"}`)
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/do", body))
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d", rec.Code)
	}
	var res ActionResult
	if err := json.Unmarshal(rec.Body.Bytes(), &res); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if !res.OK {
		t.Fatalf("result = %+v", res)
	}
	if res.View.State != StatePaused {
		t.Errorf("state = %q, want paused", res.View.State)
	}
}

// TestAPIActionFailureIsASentenceNotATransportError: the window must be able to
// show "no runner is running" rather than a failed request.
func TestAPIActionFailureIsASentenceNotATransportError(t *testing.T) {
	dir := t.TempDir()
	s, err := New(Options{
		MediaCacheDir: filepath.Join(dir, "media"),
		Config:        testConfig(t),
		Keys:          identity.Open(dir, quiet()),
		Logger:        quiet(),
		ControlDir:    dir,
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	a := NewApp(s, AppOptions{OpenURL: func(string) error { return nil }, Autostart: &fakeAutostart{}})
	rec := httptest.NewRecorder()
	Handler(a).ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/do",
		strings.NewReader(`{"action":"resume"}`)))
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200 with a failed result", rec.Code)
	}
	var res ActionResult
	if err := json.Unmarshal(rec.Body.Bytes(), &res); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if res.OK || res.Message == "" {
		t.Fatalf("result = %+v", res)
	}
}

func TestAPIRejectsBadRequests(t *testing.T) {
	_, h, cleanup := newAPITest(t)
	defer cleanup()
	cases := []struct {
		method, path, body string
		want               int
	}{
		{http.MethodPost, "/view", "", http.StatusMethodNotAllowed},
		{http.MethodGet, "/do", "", http.StatusMethodNotAllowed},
		{http.MethodPost, "/do", "not json", http.StatusBadRequest},
	}
	for _, tc := range cases {
		rec := httptest.NewRecorder()
		req := httptest.NewRequestWithContext(context.Background(), tc.method, tc.path, strings.NewReader(tc.body))
		h.ServeHTTP(rec, req)
		if rec.Code != tc.want {
			t.Errorf("%s %s = %d, want %d", tc.method, tc.path, rec.Code, tc.want)
		}
	}
}

func TestAPIPairingCancelIsIdempotent(t *testing.T) {
	_, h, cleanup := newAPITest(t)
	defer cleanup()
	for i := 0; i < 2; i++ {
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), http.MethodDelete, "/pairing", nil))
		if rec.Code != http.StatusOK {
			t.Fatalf("delete %d = %d", i, rec.Code)
		}
		var p PairingView
		if err := json.Unmarshal(rec.Body.Bytes(), &p); err != nil {
			t.Fatalf("decode: %v", err)
		}
		if p.Active {
			t.Fatalf("a cancelled pairing is still active: %+v", p)
		}
	}
}

func TestAPIMenuMatchesTheTray(t *testing.T) {
	a, h, cleanup := newAPITest(t)
	defer cleanup()
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/menu", nil))
	var got []MenuItem
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if len(got) != len(Menu(a.View())) {
		t.Fatalf("the API menu has %d rows, the tray has %d", len(got), len(Menu(a.View())))
	}
}

var _ = io.Discard
