package auth

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
)

func withLocaleFakes(t *testing.T, h *settingsHarness) {
	t.Helper()
	withDisplayNameFakes(t, h)
	settings.setLocale = func(id uuid.UUID, loc *string) error {
		h.writes[id] = true
		h.user.Locale = loc
		return nil
	}
}

func userCookieLocale(t *testing.T, rec *httptest.ResponseRecorder) (string, bool) {
	t.Helper()
	for _, c := range rec.Result().Cookies() {
		if c.Name == "reelbolt_user" {
			raw, _ := url.QueryUnescape(c.Value)
			var ui map[string]any
			if err := json.Unmarshal([]byte(raw), &ui); err != nil {
				t.Fatalf("cookie json: %v", err)
			}
			v, ok := ui["locale"].(string)
			return v, ok
		}
	}
	t.Fatal("no reelbolt_user cookie")
	return "", false
}

func TestPatchLocaleValidSavesAndRefreshesCookie(t *testing.T) {
	for body, want := range map[string]string{`{"locale":"it"}`: "it", `{"locale":" IT-it "}`: "it", `{"locale":"en"}`: "en"} {
		h := newSettingsHarness(t, false)
		withLocaleFakes(t, h)
		rec := httptest.NewRecorder()
		handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, body))
		if rec.Code != http.StatusOK {
			t.Fatalf("%s: status %d %s", body, rec.Code, rec.Body.String())
		}
		got := decodeSettings(t, rec.Body.String())
		if got.Locale == nil || *got.Locale != want {
			t.Fatalf("%s: locale = %v", body, got.Locale)
		}
		if loc, ok := userCookieLocale(t, rec); !ok || loc != want {
			t.Fatalf("%s: cookie locale = %q %v", body, loc, ok)
		}
	}
}

func TestPatchLocaleInvalidIs400(t *testing.T) {
	for _, body := range []string{`{"locale":"xx"}`, `{"locale":"../etc"}`, `{"locale":"fr"}`, `{"locale":123}`} {
		h := newSettingsHarness(t, false)
		withLocaleFakes(t, h)
		rec := httptest.NewRecorder()
		handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, body))
		if rec.Code != http.StatusBadRequest {
			t.Fatalf("%s: status = %d", body, rec.Code)
		}
		if h.user.Locale != nil || len(h.writes) != 0 {
			t.Fatalf("%s: something was written", body)
		}
	}
}

func TestPatchLocaleClears(t *testing.T) {
	for _, body := range []string{`{"locale":null}`, `{"locale":""}`} {
		h := newSettingsHarness(t, false)
		withLocaleFakes(t, h)
		it := "it"
		h.user.Locale = &it
		rec := httptest.NewRecorder()
		handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, body))
		if rec.Code != http.StatusOK {
			t.Fatalf("%s: status %d", body, rec.Code)
		}
		if h.user.Locale != nil {
			t.Fatalf("%s: not cleared", body)
		}
		if got := decodeSettings(t, rec.Body.String()); got.Locale != nil {
			t.Fatalf("%s: response locale = %v", body, *got.Locale)
		}
		if _, ok := userCookieLocale(t, rec); ok {
			t.Fatalf("%s: cookie still carries a locale", body)
		}
	}
}

func TestGetSettingsReturnsLocaleAndNullWhenUnset(t *testing.T) {
	h := newSettingsHarness(t, false)
	rec := httptest.NewRecorder()
	handleGetSettings(rec, h.sessionRequest(http.MethodGet, ""))
	if !strings.Contains(rec.Body.String(), `"locale":null`) {
		t.Fatalf("body = %s", rec.Body.String())
	}
	it := "it"
	h.user.Locale = &it
	rec = httptest.NewRecorder()
	handleGetSettings(rec, h.sessionRequest(http.MethodGet, ""))
	if !strings.Contains(rec.Body.String(), `"locale":"it"`) {
		t.Fatalf("body = %s", rec.Body.String())
	}
}

func TestMailLocalePrefersSavedThenRequest(t *testing.T) {
	it := "it"
	if got := mailLocale(&models.ApplicationUser{Locale: &it}, "en"); got != "it" {
		t.Fatalf("saved: %q", got)
	}
	if got := mailLocale(&models.ApplicationUser{}, "it"); got != "it" {
		t.Fatalf("request: %q", got)
	}
	if got := mailLocale(&models.ApplicationUser{}, "../etc"); got != "en" {
		t.Fatalf("garbage: %q", got)
	}
	r := httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/", nil)
	r.Header.Set("Accept-Language", "it-IT,it;q=0.9")
	if p := requestLocalePtr(r); p == nil || *p != "it" {
		t.Fatalf("captured locale = %v", p)
	}
}

func TestRequestLocalePtrIsNilWithoutALanguageSignal(t *testing.T) {
	r := httptest.NewRequest("POST", "/api/v1/auth/signup", nil)
	if p := requestLocalePtr(r); p != nil {
		t.Fatalf("no signal must store nothing, got %q", *p)
	}
	r.Header.Set("Accept-Language", "fr-FR")
	if p := requestLocalePtr(r); p != nil {
		t.Fatalf("an unsupported language must store nothing, got %q", *p)
	}
	r.Header.Set("Accept-Language", "it-IT,it;q=0.9")
	if p := requestLocalePtr(r); p == nil || *p != "it" {
		t.Fatalf("Accept-Language it -> it, got %v", p)
	}
	if p := requestLocalePtr(r, "en"); p == nil || *p != "en" {
		t.Fatalf("explicit body value wins, got %v", p)
	}
}
