package config

import (
	"path/filepath"
	"testing"
)

func TestLocalLLMDefaultsOffAndLoopback(t *testing.T) {
	var c Config
	c.ApplyDefaults()
	if c.LocalLLM.Enabled {
		t.Fatal("sharing must default to off")
	}
	if got := c.LocalLLM.BaseURLOr(); got != "http://127.0.0.1:11434" {
		t.Errorf("default base = %q", got)
	}
	if c.LocalLLM.Concurrency() != 2 {
		t.Errorf("default concurrency = %d", c.LocalLLM.Concurrency())
	}
	if err := c.Validate(); err != nil {
		t.Fatalf("default config invalid: %v", err)
	}
}

func TestLocalLLMValidate(t *testing.T) {
	cases := []struct {
		name string
		l    LocalLLM
		ok   bool
	}{
		{"default", LocalLLM{}, true},
		{"localhost", LocalLLM{BaseURL: "http://localhost:1234"}, true},
		{"ipv6 loopback", LocalLLM{BaseURL: "http://[::1]:8080"}, true},
		{"path prefix", LocalLLM{BaseURL: "http://127.0.0.1:8000/ollama"}, true},
		{"lan host refused", LocalLLM{BaseURL: "http://192.168.1.20:11434"}, false},
		{"lan host explicit", LocalLLM{BaseURL: "http://192.168.1.20:11434", AllowRemoteHost: true}, true},
		{"credentials", LocalLLM{BaseURL: "http://u:p@127.0.0.1"}, false},
		{"query", LocalLLM{BaseURL: "http://127.0.0.1:1?x=1"}, false},
		{"fragment", LocalLLM{BaseURL: "http://127.0.0.1:1#x"}, false},
		{"dot segments", LocalLLM{BaseURL: "http://127.0.0.1/a/../b"}, false},
		{"scheme", LocalLLM{BaseURL: "file:///etc/passwd"}, false},
		{"no host", LocalLLM{BaseURL: "http://"}, false},
		{"concurrency", LocalLLM{MaxConcurrent: 99}, false},
	}
	for _, c := range cases {
		if err := c.l.Validate(); (err == nil) != c.ok {
			t.Errorf("%s: err = %v, want ok=%v", c.name, err, c.ok)
		}
	}
}

func TestLocalLLMPersists(t *testing.T) {
	path := filepath.Join(t.TempDir(), "runner.json")
	c, err := LoadFrom(path)
	if err != nil {
		t.Fatal(err)
	}
	if err := c.SetLocalLLM(LocalLLM{Enabled: true, BaseURL: "http://127.0.0.1:1234", MaxConcurrent: 3}); err != nil {
		t.Fatal(err)
	}
	if err := c.SaveTo(path); err != nil {
		t.Fatal(err)
	}
	back, err := LoadFrom(path)
	if err != nil {
		t.Fatal(err)
	}
	if got := back.LocalLLMSettings(); !got.Enabled || got.BaseURL != "http://127.0.0.1:1234" || got.MaxConcurrent != 3 {
		t.Fatalf("round trip = %+v", got)
	}
	if err := c.SetLocalLLM(LocalLLM{BaseURL: "http://evil.example"}); err == nil {
		t.Fatal("a non-loopback host was accepted without allowRemoteHost")
	}
}
