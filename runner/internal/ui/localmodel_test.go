package ui

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/llmrelay"
)

type fakeLocalModel struct {
	set      config.LocalLLM
	status   llmrelay.Status
	found    []llmrelay.Detected
	saveErr  error
	refresh  error
	saved    int
	refreshN int
}

func (f *fakeLocalModel) Status() llmrelay.Status            { return f.status }
func (f *fakeLocalModel) Settings() (config.LocalLLM, error) { return f.set, nil }
func (f *fakeLocalModel) Detect(context.Context) []llmrelay.Detected {
	return f.found
}
func (f *fakeLocalModel) Save(s config.LocalLLM) error {
	if f.saveErr != nil {
		return f.saveErr
	}
	f.set = s
	f.saved++
	return nil
}
func (f *fakeLocalModel) Refresh(context.Context) error {
	f.refreshN++
	if f.refresh == nil {
		f.status.Models = []string{"llama3"}
		f.status.Reachable = true
	}
	return f.refresh
}

func localApp(t *testing.T, f *fakeLocalModel) (*App, func()) {
	t.Helper()
	s, cleanup := newTestShell(t, nil)
	return NewApp(s, AppOptions{OpenURL: func(string) error { return nil }, Autostart: &fakeAutostart{}, LocalModel: f}), cleanup
}

func TestLocalModelTurningOnNeedsConsent(t *testing.T) {
	f := &fakeLocalModel{}
	app, cleanup := localApp(t, f)
	defer cleanup()
	res := app.SaveLocalModel(LocalModelPatch{Enabled: true})
	if res.OK || f.saved != 0 || !strings.Contains(res.Message, "confirm") {
		t.Fatalf("saved without consent: %+v saved=%d", res, f.saved)
	}
	res = app.SaveLocalModel(LocalModelPatch{Enabled: true, Consent: true})
	if !res.OK || !f.set.Enabled || f.set.BaseURL != config.DefaultLocalLLMBaseURL {
		t.Fatalf("consented save failed: %+v %+v", res, f.set)
	}
	// Turning it off never needs consent.
	if res := app.SaveLocalModel(LocalModelPatch{Enabled: false}); !res.OK || f.set.Enabled {
		t.Fatalf("off: %+v", res)
	}
}

func TestLocalModelRefusesRemoteHostWithoutOptIn(t *testing.T) {
	f := &fakeLocalModel{}
	app, cleanup := localApp(t, f)
	defer cleanup()
	res := app.SaveLocalModel(LocalModelPatch{Enabled: true, Consent: true, BaseURL: "http://192.168.1.20:11434"})
	if res.OK || f.saved != 0 || !strings.Contains(res.Message, "another computer") {
		t.Fatalf("remote accepted: %+v", res)
	}
	res = app.SaveLocalModel(LocalModelPatch{Enabled: true, Consent: true, BaseURL: "http://192.168.1.20:11434", AllowRemoteHost: true})
	if !res.OK || !f.set.AllowRemoteHost {
		t.Fatalf("opted-in remote refused: %+v", res)
	}
	res = app.SaveLocalModel(LocalModelPatch{BaseURL: "ftp://127.0.0.1"})
	if res.OK || strings.Contains(res.Message, "baseUrl") {
		t.Fatalf("bad scheme: %+v", res)
	}
}

func TestLocalModelSaveFailureIsASentence(t *testing.T) {
	f := &fakeLocalModel{saveErr: errors.New("disk exploded: /x/y")}
	app, cleanup := localApp(t, f)
	defer cleanup()
	res := app.SaveLocalModel(LocalModelPatch{Enabled: true, Consent: true})
	if res.OK || strings.Contains(res.Message, "disk") {
		t.Fatalf("raw error leaked: %+v", res)
	}
}

func TestLocalModelDetectAndCheck(t *testing.T) {
	f := &fakeLocalModel{found: []llmrelay.Detected{{Kind: "ollama", BaseURL: "http://127.0.0.1:11434", Models: []string{"llama3"}}}}
	app, cleanup := localApp(t, f)
	defer cleanup()
	if res := app.DetectLocalModels(context.Background()); len(res.Servers) != 1 || res.Message != "" {
		t.Fatalf("detect: %+v", res)
	}
	f.found = nil
	if res := app.DetectLocalModels(context.Background()); res.Servers == nil || len(res.Servers) != 0 || res.Message == "" {
		t.Fatalf("empty detect: %+v", res)
	}
	if res := app.CheckLocalModel(context.Background()); res.OK || f.refreshN != 0 {
		t.Fatalf("check while off should not probe: %+v", res)
	}
	f.set.Enabled = true
	if res := app.CheckLocalModel(context.Background()); !res.OK || len(res.Doc.Status.Models) != 1 {
		t.Fatalf("check: %+v", res)
	}
	f.refresh = errors.New("dial tcp: refused")
	if res := app.CheckLocalModel(context.Background()); res.OK || strings.Contains(res.Message, "dial") {
		t.Fatalf("unreachable: %+v", res)
	}
}

func TestLocalModelHTTPRoutes(t *testing.T) {
	f := &fakeLocalModel{}
	app, cleanup := localApp(t, f)
	defer cleanup()
	h := Handler(app)
	do := func(method, path, body string) *httptest.ResponseRecorder {
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), method, path, strings.NewReader(body)))
		return rec
	}
	if rec := do(http.MethodGet, "/local-model", ""); rec.Code != 200 || !strings.Contains(rec.Body.String(), `"available":true`) {
		t.Fatalf("get: %d %s", rec.Code, rec.Body)
	}
	if rec := do(http.MethodPost, "/local-model", `{"enabled":true,"consent":true}`); !strings.Contains(rec.Body.String(), `"ok":true`) {
		t.Fatalf("post: %s", rec.Body)
	}
	if rec := do(http.MethodPost, "/local-model", `nope`); !strings.Contains(rec.Body.String(), `"ok":false`) {
		t.Fatalf("bad json: %s", rec.Body)
	}
	if rec := do(http.MethodGet, "/local-model/detect", ""); rec.Code != http.StatusMethodNotAllowed {
		t.Fatalf("detect GET = %d", rec.Code)
	}
	if rec := do(http.MethodPost, "/local-model/check", ""); rec.Code != 200 {
		t.Fatalf("check = %d", rec.Code)
	}
}
