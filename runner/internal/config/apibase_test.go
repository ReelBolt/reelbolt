package config

import (
	"path/filepath"
	"testing"
)

func TestCheckAPIBase(t *testing.T) {
	cases := []struct {
		raw     string
		dev     []string
		want    string
		wantErr bool
	}{
		{raw: "https://app.reelbolt.ai", want: "https://app.reelbolt.ai"},
		{raw: "https://app.reelbolt.ai/", want: "https://app.reelbolt.ai"},
		{raw: "https://reelbolt.ai", want: "https://reelbolt.ai"},
		{raw: "http://app.reelbolt.ai", wantErr: true},
		{raw: "https://reelbolt.ai.example.com", wantErr: true},
		{raw: "https://notreelbolt.ai", wantErr: true},
		{raw: "https://app.reelbolt.ai/api", wantErr: true},
		{raw: "https://app.reelbolt.ai?x=1", wantErr: true},
		{raw: "https://user@app.reelbolt.ai", wantErr: true},
		{raw: "ftp://app.reelbolt.ai", wantErr: true},
		{raw: "app.reelbolt.ai", wantErr: true},
		{raw: "https://evil.example.com", wantErr: true},
		// Development hosts: accepted only in a non-release build, which the
		// tests are.
		{raw: "http://localhost:8080", want: "http://localhost:8080"},
		{raw: "http://127.0.0.1:3000/", want: "http://127.0.0.1:3000"},
		{raw: "http://devbox:8080", dev: []string{"devbox:8080"}, want: "http://devbox:8080"},
		{raw: "http://devbox:8080", wantErr: true},
	}
	for _, c := range cases {
		got, err := CheckAPIBase(c.raw, c.dev)
		if c.wantErr {
			if err == nil {
				t.Errorf("CheckAPIBase(%q) = %q, want an error", c.raw, got)
			}
			continue
		}
		if err != nil {
			t.Errorf("CheckAPIBase(%q): %v", c.raw, err)
			continue
		}
		if got != c.want {
			t.Errorf("CheckAPIBase(%q) = %q, want %q", c.raw, got, c.want)
		}
	}
}

func TestValidateChecksTheAPIBase(t *testing.T) {
	c := &Config{}
	c.ApplyDefaults()
	c.APIBaseURL = "https://evil.example.com"
	if err := c.Validate(); err == nil {
		t.Fatal("Validate accepted an API origin outside the allowlist")
	}
	c.APIBaseURL = "https://app.reelbolt.ai"
	if err := c.Validate(); err != nil {
		t.Fatalf("Validate refused the product origin: %v", err)
	}
}

func TestAPIBaseRoundTrips(t *testing.T) {
	path := filepath.Join(t.TempDir(), FileName)
	c := &Config{APIBaseURL: "https://app.reelbolt.ai"}
	c.ApplyDefaults()
	if err := c.SaveTo(path); err != nil {
		t.Fatal(err)
	}
	got, err := LoadFrom(path)
	if err != nil {
		t.Fatal(err)
	}
	if got.APIBaseURL != "https://app.reelbolt.ai" {
		t.Fatalf("apiBaseUrl = %q after a round trip", got.APIBaseURL)
	}
}
