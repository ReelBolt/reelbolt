package config

import (
	"fmt"
	"net"
	"net/url"
	"strings"
)

// Local-model sharing. Off unless the user turns it on: the zero value of
// LocalLLM, which is what a runner.json written before this feature produces,
// shares nothing.
const (
	// DefaultLocalLLMBaseURL is where Ollama listens by default.
	DefaultLocalLLMBaseURL = "http://127.0.0.1:11434"
	// DefaultLocalLLMConcurrent is how many relayed requests run at once.
	DefaultLocalLLMConcurrent = 2
	// MaxLocalLLMConcurrent is the ceiling Validate accepts.
	MaxLocalLLMConcurrent = 8
	maxLocalLLMURLBytes   = 300
)

// LocalLLM is the "share a local model with my workspace" setting.
//
// The server it points at is the user's own; the runner only relays requests
// to it. The target is decided here and nowhere else: no message from the
// gateway can supply a host, URL or header.
type LocalLLM struct {
	// Enabled turns sharing on. Default off.
	Enabled bool `json:"enabled"`
	// BaseURL is the user's OpenAI-compatible server, scheme://host[:port]
	// (a path prefix is allowed). Empty means DefaultLocalLLMBaseURL.
	BaseURL string `json:"baseUrl,omitempty"`
	// MaxConcurrent caps simultaneous relayed requests. Zero means the default.
	MaxConcurrent int `json:"maxConcurrent,omitempty"`
	// AllowRemoteHost lets BaseURL name a host that is not this machine. It is
	// an explicit opt-in: without it only loopback is accepted.
	AllowRemoteHost bool `json:"allowRemoteHost,omitempty"`
}

// BaseURLOr returns the configured base URL, or the default.
func (l LocalLLM) BaseURLOr() string {
	if strings.TrimSpace(l.BaseURL) == "" {
		return DefaultLocalLLMBaseURL
	}
	return strings.TrimSpace(l.BaseURL)
}

// Concurrency returns the effective concurrency cap.
func (l LocalLLM) Concurrency() int {
	if l.MaxConcurrent <= 0 {
		return DefaultLocalLLMConcurrent
	}
	return l.MaxConcurrent
}

// IsLoopbackHost reports whether host (no port) names this machine.
func IsLoopbackHost(host string) bool {
	host = strings.Trim(strings.ToLower(host), "[]")
	if host == "localhost" || strings.HasSuffix(host, ".localhost") {
		return true
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}

// ParseLocalLLMBase parses and checks a base URL. It is the one place the rules
// live: http or https, a host, no credentials, query or fragment, and a
// loopback host unless allowRemote is set.
func ParseLocalLLMBase(raw string, allowRemote bool) (*url.URL, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		raw = DefaultLocalLLMBaseURL
	}
	if len(raw) > maxLocalLLMURLBytes {
		return nil, fmt.Errorf("baseUrl is longer than %d bytes", maxLocalLLMURLBytes)
	}
	u, err := url.Parse(raw)
	if err != nil {
		return nil, fmt.Errorf("baseUrl is not a valid URL: %w", err)
	}
	if u.Scheme != "http" && u.Scheme != "https" {
		return nil, fmt.Errorf("baseUrl must start with http:// or https://")
	}
	if u.Hostname() == "" {
		return nil, fmt.Errorf("baseUrl needs a host")
	}
	if u.User != nil {
		return nil, fmt.Errorf("baseUrl must not contain credentials")
	}
	if u.RawQuery != "" || u.Fragment != "" || strings.Contains(raw, "?") || strings.Contains(raw, "#") {
		return nil, fmt.Errorf("baseUrl must not contain a query or fragment")
	}
	for _, seg := range strings.Split(u.Path, "/") {
		if seg == ".." || seg == "." {
			return nil, fmt.Errorf("baseUrl path must not contain dot segments")
		}
	}
	if !allowRemote && !IsLoopbackHost(u.Hostname()) {
		return nil, fmt.Errorf("baseUrl host %q is not this machine; allowRemoteHost must be set explicitly", u.Hostname())
	}
	return u, nil
}

// Validate rejects a setting the relay must not act on.
func (l LocalLLM) Validate() error {
	if l.MaxConcurrent < 0 || l.MaxConcurrent > MaxLocalLLMConcurrent {
		return fmt.Errorf("maxConcurrent must be between 0 and %d, got %d", MaxLocalLLMConcurrent, l.MaxConcurrent)
	}
	if _, err := ParseLocalLLMBase(l.BaseURL, l.AllowRemoteHost); err != nil {
		return err
	}
	return nil
}

// LocalLLMSettings returns the current setting.
func (c *Config) LocalLLMSettings() LocalLLM { return c.LocalLLM }

// SetLocalLLM validates and records the setting. The caller saves the config.
func (c *Config) SetLocalLLM(l LocalLLM) error {
	if err := l.Validate(); err != nil {
		return fmt.Errorf("localLlm: %w", err)
	}
	c.LocalLLM = l
	return nil
}
