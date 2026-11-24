package services

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestNormalizeLocale(t *testing.T) {
	cases := map[string]struct {
		out string
		ok  bool
	}{
		"it": {"it", true}, "IT": {"it", true}, "it-IT": {"it", true}, "it_it": {"it", true}, " en-US ": {"en", true},
		"": {"", false}, "xx": {"", false}, "../etc": {"", false}, "it/../en": {"", false}, "fr-CA": {"", false},
		strings.Repeat("a", 500): {"", false},
	}
	for in, want := range cases {
		out, ok := NormalizeLocale(in)
		if out != want.out || ok != want.ok {
			t.Errorf("NormalizeLocale(%q) = %q,%v want %q,%v", in, out, ok, want.out, want.ok)
		}
	}
	if !IsSupportedLocale("it") || IsSupportedLocale("IT") || IsSupportedLocale("it-IT") {
		t.Error("IsSupportedLocale must be exact")
	}
}

func TestLocaleFromRequest(t *testing.T) {
	mk := func(cookie, al string) *http.Request {
		r := httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/", nil)
		if cookie != "" {
			r.AddCookie(&http.Cookie{Name: "rb_locale", Value: cookie})
		}
		if al != "" {
			r.Header.Set("Accept-Language", al)
		}
		return r
	}
	tests := []struct {
		name     string
		r        *http.Request
		explicit []string
		want     string
	}{
		{"nothing", mk("", ""), nil, "en"},
		{"explicit wins", mk("en", "en"), []string{"it"}, "it"},
		{"bad explicit falls to cookie", mk("it", ""), []string{"zz"}, "it"},
		{"cookie over header", mk("it", "en"), nil, "it"},
		{"bad cookie falls to header", mk("../etc", "it"), nil, "it"},
		{"region", mk("", "it-IT"), nil, "it"},
		{"q-values pick highest supported", mk("", "fr;q=0.9, en;q=0.5, it;q=0.8"), nil, "it"},
		{"q ordering en", mk("", "it;q=0.3, en;q=0.7"), nil, "en"},
		{"q=0 excluded", mk("", "it;q=0, en;q=0.1"), nil, "en"},
		{"garbage", mk("", ";;;,,,q=,it;q=abc, ???"), nil, "en"},
		{"unsupported only", mk("", "fr-FR,de;q=0.8"), nil, "en"},
		{"oversized header capped", mk("", strings.Repeat("x", 100000)+",it"), nil, "en"},
		{"traversal in header", mk("", "../../etc/passwd"), nil, "en"},
		{"nil request", nil, nil, "en"},
	}
	for _, tc := range tests {
		if got := LocaleFromRequest(tc.r, tc.explicit...); got != tc.want {
			t.Errorf("%s: got %q want %q", tc.name, got, tc.want)
		}
	}
}

func TestLocaleSignalFromRequest(t *testing.T) {
	mk := func(cookie, al string) *http.Request {
		r := httptest.NewRequest("GET", "/", nil)
		if cookie != "" {
			r.AddCookie(&http.Cookie{Name: "rb_locale", Value: cookie})
		}
		if al != "" {
			r.Header.Set("Accept-Language", al)
		}
		return r
	}
	cases := []struct {
		name   string
		r      *http.Request
		expl   []string
		want   string
		wantOK bool
	}{
		{"nothing", mk("", ""), nil, "", false},
		{"nil request", nil, nil, "", false},
		{"unsupported language only", mk("", "fr-FR,fr;q=0.9"), nil, "", false},
		{"garbage cookie", mk("../etc", ""), nil, "", false},
		{"explicit wins", mk("en", "en"), []string{"it"}, "it", true},
		{"cookie", mk("it", "en"), nil, "it", true},
		{"accept-language", mk("", "it-IT,it;q=0.9,en;q=0.5"), nil, "it", true},
		{"english header counts as a signal", mk("", "en-US"), nil, "en", true},
	}
	for _, c := range cases {
		got, ok := LocaleSignalFromRequest(c.r, c.expl...)
		if got != c.want || ok != c.wantOK {
			t.Errorf("%s: got (%q,%v) want (%q,%v)", c.name, got, ok, c.want, c.wantOK)
		}
		if def := LocaleFromRequest(c.r, c.expl...); !c.wantOK && def != DefaultLocale {
			t.Errorf("%s: LocaleFromRequest without a signal = %q", c.name, def)
		}
	}
}
