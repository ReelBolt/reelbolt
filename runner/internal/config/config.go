// Package config owns the runner daemon's on-disk configuration:
// os.UserConfigDir()/ReelBolt/runner.json.
//
// It holds only what a paired runner needs to start again — the gateway it was
// paired to, the device it is, and the local policy knobs (slots, runtime image,
// scratch root). The device's private key is NOT here: it lives in the keystore
// (runner/internal/identity). Nothing in this file is a secret, which is why it
// is a plain 0600 JSON document rather than an encrypted one.
package config

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"time"
)

// Directory and file names under os.UserConfigDir().
const (
	DirName  = "ReelBolt"
	FileName = "runner.json"

	// DefaultProductDomain is the domain the gateway allowlist is rooted at.
	DefaultProductDomain = "reelbolt.ai"

	// DefaultSlots is how many jobs a desktop runner takes at once. One is the
	// safe default for a laptop; the desktop UI raises it deliberately.
	DefaultSlots = 1

	// MaxSlots is the ceiling Validate accepts. A runner advertising more
	// concurrency than a desktop can honour starves jobs inside its own slots.
	MaxSlots = 16

	// DefaultRuntimeImage is the runtime image repository used when nothing is
	// pinned. The tag is only a fallback for a development machine: a released
	// runner pins a digest (see PinnedImage).
	DefaultRuntimeImage = "reelbolt-sandbox-runtime:local"

	// maxTokenBytes bounds every string a config file can carry, so a corrupt or
	// hostile file cannot make the daemon allocate without limit.
	maxTokenBytes = 4096
)

// Config is runner.json.
type Config struct {
	// GatewayURL is the wss:// endpoint the daemon dials. It comes from the
	// pairing poll response and is checked against the allowlist on every load,
	// never only at pairing time: the file is editable.
	GatewayURL string `json:"gatewayUrl,omitempty"`
	// DeviceID is runner_devices.id. Empty means this machine is not paired.
	DeviceID string `json:"deviceId,omitempty"`
	// DeviceName is the human name shown on the approval screen and in the
	// dashboard.
	DeviceName string `json:"deviceName,omitempty"`
	// OrganizationID is the workspace the device was approved into. Recorded for
	// diagnostics only: authority is the gateway's, and nothing in the daemon
	// trusts this value.
	OrganizationID string `json:"organizationId,omitempty"`
	// APIBaseURL is the origin this machine was paired at (e.g.
	// https://reelbolt.ai). Folder sync signs its /api/v1/device requests
	// for this origin and no other, so it is checked against the same allowlist
	// as the gateway on every load. Empty means a device paired before folder
	// sync existed: nothing guesses an origin for it, and folder sync asks the
	// person to pair again (or to run "reelbolt-runner config set-api").
	APIBaseURL string `json:"apiBaseUrl,omitempty"`

	// Slots is how many jobs run concurrently.
	Slots int `json:"slots,omitempty"`
	// RuntimeImage is the container image the Remotion and media executions run
	// in. Empty means the pinned image, then DefaultRuntimeImage.
	RuntimeImage string `json:"runtimeImage,omitempty"`
	// ContainerCLI pins the container CLI ("docker" or "podman"). Empty means the
	// E3 detection order (DOCKER_HOST, docker context, platform list).
	ContainerCLI string `json:"containerCli,omitempty"`
	// ScratchRoot is where job scratch directories are created. Empty means
	// <UserCacheDir>/ReelBolt/jobs.
	ScratchRoot string `json:"scratchRoot,omitempty"`
	// LogFile, when set, receives the log in addition to stderr.
	LogFile string `json:"logFile,omitempty"`
	// DrainSeconds is how long a drain waits for in-flight sessions before the
	// daemon shuts the connection down. Zero means DefaultDrainSeconds.
	DrainSeconds int `json:"drainSeconds,omitempty"`
	// Limits are the per-job resource caps. E5 hard-coded 4g / 2 cpus / 512
	// pids in the daemon and documented them as E5c's starting point; they are
	// configuration now, and no executor holds a literal. Always written to the
	// file (a struct cannot be omitted), so the knobs are discoverable.
	Limits governor.LimitsConfig `json:"limits"`
	// Schedule is when this machine is willing to take work: the idle, power
	// and time-window gates. Always written for the same reason.
	Schedule governor.Schedule `json:"schedule"`

	// DevGateway marks a gateway outside the product domain. It is only accepted
	// when it is in DevGateways AND this is a non-release build.
	DevGateway bool `json:"devGateway,omitempty"`
	// DevGateways are the extra hosts a development build may dial.
	DevGateways []string `json:"devGateways,omitempty"`

	// UpdateChannel is the release channel this runner follows: "stable" or
	// "beta". Empty means stable.
	UpdateChannel string `json:"updateChannel,omitempty"`
	// AutoUpdate turns the background update check off. It is a pointer so that
	// "absent" and "false" are different states: absent means the default,
	// which is on.
	AutoUpdate *bool `json:"autoUpdate,omitempty"`
	// UpdateBaseURL overrides the download host. Empty means the product's
	// (https://downloads.reelbolt.ai/runner). A host outside the product domain
	// is accepted only by a non-release build that was told about it on the
	// command line, exactly like a development gateway.
	UpdateBaseURL string `json:"updateBaseUrl,omitempty"`
	// DevUpdateHosts are extra update hosts a development build may use.
	DevUpdateHosts []string `json:"devUpdateHosts,omitempty"`
	// UpdateIntervalHours is how often the background check runs. Zero means
	// DefaultUpdateIntervalHours.
	UpdateIntervalHours int `json:"updateIntervalHours,omitempty"`

	// Telemetry is the opt-in crash reporting switch (E11). Always written,
	// like the limits and the schedule, so the answer is discoverable in the
	// file the Settings screen names.
	Telemetry Telemetry `json:"telemetry"`

	// MediaCache caps the downloaded-media cache (mediacache.go). Absent means
	// DefaultMediaCacheBytes; maxBytes 0 turns the cache off.
	MediaCache MediaCache `json:"mediaCache"`

	// LocalLLM is the opt-in share-a-local-model setting (localllm.go). The zero
	// value shares nothing.
	LocalLLM LocalLLM `json:"localLlm"`
}

// Telemetry is this machine's crash-reporting consent.
//
// The switch is OFF unless a user turns it on, and the zero value of this
// struct - which is what a runner.json written before E11, or no file at all,
// produces - is off. "Never asked", "asked and declined" and "turned off after
// being on" are all off; they differ only in AskedAt, which exists so the
// first-run wizard asks once and does not nag a user who already answered.
type Telemetry struct {
	// Enabled is the user's answer. A pointer so that an explicit false is
	// written rather than omitted: a file that says "off" and a file that says
	// nothing must not be distinguishable in behaviour, but the record of a
	// person's decision is worth keeping.
	Enabled *bool `json:"enabled,omitempty"`
	// AskedAt is when the wizard asked, RFC3339 UTC. Empty means it has not.
	AskedAt string `json:"askedAt,omitempty"`
	// ConsentVersion is the disclosure that was accepted, so a later and wider
	// one can be told apart from the one a user actually read.
	ConsentVersion string `json:"consentVersion,omitempty"`
}

// On reports whether the user has turned telemetry on. Everything that decides
// whether anything leaves this machine goes through this one method.
func (t Telemetry) On() bool { return t.Enabled != nil && *t.Enabled }

// Asked reports whether the first-run wizard has already asked.
func (t Telemetry) Asked() bool { return t.AskedAt != "" }

// Set records an answer, and marks the question as asked if it was not.
func (t *Telemetry) Set(on bool, at time.Time, consentVersion string) {
	v := on
	t.Enabled = &v
	if t.AskedAt == "" {
		t.AskedAt = at.UTC().Format(time.RFC3339)
	}
	if on {
		t.ConsentVersion = consentVersion
		return
	}
	// Declining withdraws consent: the version is cleared so a later build
	// cannot read an old "yes" out of a "no".
	t.ConsentVersion = ""
}

// MarkAsked records that the wizard asked without changing the answer.
func (t *Telemetry) MarkAsked(at time.Time) {
	if t.AskedAt == "" {
		t.AskedAt = at.UTC().Format(time.RFC3339)
	}
}

// Validate refuses a telemetry block a runner must not act on: a timestamp it
// cannot read would make "already asked" unanswerable.
func (t Telemetry) Validate() error {
	for name, v := range map[string]string{"askedAt": t.AskedAt} {
		if v == "" {
			continue
		}
		if _, err := time.Parse(time.RFC3339, v); err != nil {
			return fmt.Errorf("%s is not an RFC3339 timestamp: %q", name, v)
		}
	}
	if len(t.ConsentVersion) > maxTokenBytes {
		return fmt.Errorf("consentVersion is longer than %d bytes", maxTokenBytes)
	}
	return nil
}

// Update defaults.
const (
	// DefaultUpdateChannel is the channel a runner follows unless it is set.
	DefaultUpdateChannel = "stable"
	// DefaultUpdateIntervalHours is how often the daemon looks for a release.
	// An update is checked a few times a day rather than continuously: the
	// manifest is small, but there is nothing to gain from polling it.
	DefaultUpdateIntervalHours = 6
	// MaxUpdateIntervalHours bounds the configured interval.
	MaxUpdateIntervalHours = 168
)

// UpdatesEnabled reports whether the daemon should check for releases in the
// background. The explicit "update check" and "update apply" commands work
// whatever this says.
func (c *Config) UpdatesEnabled() bool { return c.AutoUpdate == nil || *c.AutoUpdate }

// UpdateInterval is how long the daemon waits between background checks.
func (c *Config) UpdateInterval() time.Duration {
	h := c.UpdateIntervalHours
	if h <= 0 {
		h = DefaultUpdateIntervalHours
	}
	return time.Duration(h) * time.Hour
}

// UpdateChannelOr returns the configured channel, or the default.
func (c *Config) UpdateChannelOr() string {
	if c.UpdateChannel == "" {
		return DefaultUpdateChannel
	}
	return c.UpdateChannel
}

// DefaultDrainSeconds bounds the drain that a SIGTERM starts.
const DefaultDrainSeconds = 5

// Dir returns os.UserConfigDir()/ReelBolt, the directory runner.json lives in.
func Dir() (string, error) {
	base, err := os.UserConfigDir()
	if err != nil {
		return "", fmt.Errorf("config: user config directory: %w", err)
	}
	return filepath.Join(base, DirName), nil
}

// Path returns the full path of runner.json.
func Path() (string, error) {
	dir, err := Dir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, FileName), nil
}

// CacheDir returns <UserCacheDir>/ReelBolt: the daemon's scratch and log root.
func CacheDir() (string, error) {
	base, err := os.UserCacheDir()
	if err != nil {
		return "", fmt.Errorf("config: user cache directory: %w", err)
	}
	return filepath.Join(base, DirName), nil
}

// Load reads runner.json. A missing file is not an error: it yields a default,
// unpaired configuration, which is what a first run needs. Every other failure
// is reported, because a config file that exists but cannot be read would
// otherwise silently un-pair a working runner.
func Load() (*Config, error) {
	path, err := Path()
	if err != nil {
		return nil, err
	}
	return LoadFrom(path)
}

// LoadFrom reads one configuration file.
func LoadFrom(path string) (*Config, error) {
	raw, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		c := &Config{}
		c.ApplyDefaults()
		return c, nil
	}
	if err != nil {
		return nil, fmt.Errorf("config: read %s: %w", path, err)
	}
	// A config file is written by this daemon with 0600, but it is a text file a
	// user can edit, so it is validated rather than trusted.
	if err := os.Chmod(path, 0o600); err != nil && !errors.Is(err, os.ErrPermission) {
		return nil, fmt.Errorf("config: tighten permissions on %s: %w", path, err)
	}
	var c Config
	if err := json.Unmarshal(raw, &c); err != nil {
		return nil, fmt.Errorf("config: parse %s: %w", path, err)
	}
	c.ApplyDefaults()
	if err := c.Validate(); err != nil {
		return nil, fmt.Errorf("config: %s: %w", path, err)
	}
	return &c, nil
}

// Save writes the configuration to the standard path, creating the directory.
func (c *Config) Save() error {
	path, err := Path()
	if err != nil {
		return err
	}
	return c.SaveTo(path)
}

// SaveTo writes the configuration to one path. The write is atomic (a temporary
// file in the same directory, then a rename) so a crash mid-write cannot leave a
// truncated config that would refuse to load, and it is 0600 from creation.
func (c *Config) SaveTo(path string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return fmt.Errorf("config: create %s: %w", filepath.Dir(path), err)
	}
	raw, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return fmt.Errorf("config: marshal: %w", err)
	}
	raw = append(raw, '\n')
	tmp, err := os.CreateTemp(filepath.Dir(path), FileName+".tmp*")
	if err != nil {
		return fmt.Errorf("config: temporary file: %w", err)
	}
	tmpName := tmp.Name()
	defer func() { _ = os.Remove(tmpName) }()
	if err := tmp.Chmod(0o600); err != nil {
		_ = tmp.Close()
		return fmt.Errorf("config: chmod temporary file: %w", err)
	}
	if _, err := tmp.Write(raw); err != nil {
		_ = tmp.Close()
		return fmt.Errorf("config: write: %w", err)
	}
	if err := tmp.Close(); err != nil {
		return fmt.Errorf("config: close: %w", err)
	}
	if err := os.Rename(tmpName, path); err != nil {
		return fmt.Errorf("config: replace %s: %w", path, err)
	}
	return nil
}

// ApplyDefaults fills every unset field with its default. It never invents a
// device identity: an unpaired runner stays unpaired.
func (c *Config) ApplyDefaults() {
	if c.Slots <= 0 {
		c.Slots = DefaultSlots
	}
	if c.DeviceName == "" {
		if h, err := os.Hostname(); err == nil {
			c.DeviceName = sanitizeName(h)
		}
	}
	if c.DrainSeconds <= 0 {
		c.DrainSeconds = DefaultDrainSeconds
	}
	c.Limits.ApplyDefaults()
	c.Schedule.ApplyDefaults()
}

// Validate rejects a configuration the daemon must not act on.
func (c *Config) Validate() error {
	if c.Slots < 1 || c.Slots > MaxSlots {
		return fmt.Errorf("slots must be between 1 and %d, got %d", MaxSlots, c.Slots)
	}
	for name, v := range map[string]string{
		"gatewayUrl":    c.GatewayURL,
		"deviceId":      c.DeviceID,
		"deviceName":    c.DeviceName,
		"runtimeImage":  c.RuntimeImage,
		"containerCli":  c.ContainerCLI,
		"scratchRoot":   c.ScratchRoot,
		"logFile":       c.LogFile,
		"updateBaseUrl": c.UpdateBaseURL,
		"updateChannel": c.UpdateChannel,
		"apiBaseUrl":    c.APIBaseURL,
	} {
		if len(v) > maxTokenBytes {
			return fmt.Errorf("%s is longer than %d bytes", name, maxTokenBytes)
		}
		if strings.ContainsRune(v, 0) {
			return fmt.Errorf("%s contains a NUL byte", name)
		}
	}
	switch c.UpdateChannel {
	case "", "stable", "beta":
	default:
		return fmt.Errorf("updateChannel must be stable or beta, got %q", c.UpdateChannel)
	}
	if c.UpdateIntervalHours < 0 || c.UpdateIntervalHours > MaxUpdateIntervalHours {
		return fmt.Errorf("updateIntervalHours must be between 0 and %d, got %d",
			MaxUpdateIntervalHours, c.UpdateIntervalHours)
	}
	for _, host := range c.DevUpdateHosts {
		if len(host) > maxTokenBytes {
			return fmt.Errorf("devUpdateHosts entry is longer than %d bytes", maxTokenBytes)
		}
	}
	if c.ScratchRoot != "" && !filepath.IsAbs(c.ScratchRoot) {
		return fmt.Errorf("scratchRoot must be an absolute path, got %q", c.ScratchRoot)
	}
	switch c.ContainerCLI {
	case "", "docker", "podman":
	default:
		return fmt.Errorf("containerCli must be docker or podman, got %q", c.ContainerCLI)
	}
	if c.GatewayURL != "" {
		if err := CheckGateway(c.GatewayURL, c.DevGateways); err != nil {
			return err
		}
	}
	if c.APIBaseURL != "" {
		if _, err := CheckAPIBase(c.APIBaseURL, c.DevGateways); err != nil {
			return err
		}
	}
	if c.DeviceID != "" && c.GatewayURL == "" {
		return errors.New("a paired device needs the gatewayUrl it was paired to")
	}
	if err := c.Limits.Validate(); err != nil {
		return fmt.Errorf("limits: %w", err)
	}
	if err := c.Schedule.Validate(); err != nil {
		return fmt.Errorf("schedule: %w", err)
	}
	if err := c.Telemetry.Validate(); err != nil {
		return fmt.Errorf("telemetry: %w", err)
	}
	if err := c.MediaCache.Validate(); err != nil {
		return fmt.Errorf("mediaCache: %w", err)
	}
	if err := c.LocalLLM.Validate(); err != nil {
		return fmt.Errorf("localLlm: %w", err)
	}
	return nil
}

// IsPaired reports whether this machine has a device identity to present.
func (c *Config) IsPaired() bool { return c.DeviceID != "" && c.GatewayURL != "" }

// CheckGateway enforces the compiled-in gateway allowlist.
//
// The gateway URL is not compiled in: it arrives in the pairing poll response
// and lives in a text file afterwards, so it is attacker-influenceable in both
// directions. A runner that dialed an arbitrary host would hand a device's
// Ed25519 signature — and therefore its identity — to that host, so only the
// product domain (or a gateway a NON-RELEASE build was explicitly told about)
// is accepted.
func CheckGateway(raw string, devGateways []string) error {
	u, err := url.Parse(raw)
	if err != nil {
		return fmt.Errorf("gatewayUrl %q is not a URL: %w", raw, err)
	}
	switch u.Scheme {
	case "wss":
	case "ws":
		// Plaintext is only ever acceptable for a gateway on this machine or a
		// development gateway; the signature is a bearer credential in transit.
		if !isLoopback(u.Hostname()) && !hostIn(u.Host, devGateways) {
			return fmt.Errorf("gatewayUrl %q is not secure: use wss", raw)
		}
	default:
		return fmt.Errorf("gatewayUrl %q must use wss", raw)
	}
	if u.Host == "" {
		return fmt.Errorf("gatewayUrl %q has no host", raw)
	}
	host := u.Hostname()
	if host == DefaultProductDomain || strings.HasSuffix(host, "."+DefaultProductDomain) {
		return nil
	}
	// Every gateway outside the product domain is a development gateway,
	// including one on this machine: the rule that a release build refuses them
	// is what stops a shipped runner being pointed at a third party.
	if hostIn(u.Host, devGateways) || hostIn(host, devGateways) || isLoopback(host) {
		if !DevGatewayAllowed() {
			return fmt.Errorf("gatewayUrl %q is a development gateway, which a release build refuses", raw)
		}
		return nil
	}
	return fmt.Errorf("gatewayUrl %q is not in the allowed gateway list (%s)", raw, DefaultProductDomain)
}

// CheckAPIBase enforces the gateway's allowlist on the API origin folder sync
// signs requests for, and returns the origin in canonical form
// (scheme://host[:port], no trailing slash).
//
// The rule is the gateway's, for the gateway's reason: a signed device request
// is good for two minutes, so a host that received one could replay it to the
// real API. Only https on the product domain is accepted, plus — in a
// NON-RELEASE build only — a loopback or development host (plain http allowed
// only there).
func CheckAPIBase(raw string, devHosts []string) (string, error) {
	u, err := url.Parse(strings.TrimSpace(raw))
	if err != nil {
		return "", fmt.Errorf("apiBaseUrl %q is not a URL: %w", raw, err)
	}
	if u.Host == "" {
		return "", fmt.Errorf("apiBaseUrl %q has no host", raw)
	}
	if u.User != nil || u.RawQuery != "" || u.Fragment != "" || (u.Path != "" && u.Path != "/") {
		return "", fmt.Errorf("apiBaseUrl %q must be an origin like https://app.%s, with no path or query", raw, DefaultProductDomain)
	}
	host := u.Hostname()
	dev := isLoopback(host) || hostIn(u.Host, devHosts) || hostIn(host, devHosts)
	switch u.Scheme {
	case "https":
	case "http":
		if !dev {
			return "", fmt.Errorf("apiBaseUrl %q is not secure: use https", raw)
		}
	default:
		return "", fmt.Errorf("apiBaseUrl %q must use https", raw)
	}
	origin := u.Scheme + "://" + u.Host
	if host == DefaultProductDomain || strings.HasSuffix(host, "."+DefaultProductDomain) {
		return origin, nil
	}
	if dev {
		if !DevGatewayAllowed() {
			return "", fmt.Errorf("apiBaseUrl %q is a development host, which a release build refuses", raw)
		}
		return origin, nil
	}
	return "", fmt.Errorf("apiBaseUrl %q is not in the allowed list (%s)", raw, DefaultProductDomain)
}

// hostIn matches a host (optionally host:port) against a list of allowed hosts.
// Matching is exact — never a substring — so "reelbolt.ai.example.com" and
// "notreelbolt.ai" are both refused.
func hostIn(host string, allowed []string) bool {
	for _, a := range allowed {
		a = strings.TrimSpace(a)
		if a == "" {
			continue
		}
		if strings.EqualFold(a, host) {
			return true
		}
		// A bare host entry also matches the same host on its default port.
		if strings.EqualFold(strings.TrimSuffix(a, "/"), host) {
			return true
		}
	}
	return false
}

func isLoopback(host string) bool {
	switch strings.ToLower(host) {
	case "localhost", "127.0.0.1", "::1", "[::1]":
		return true
	}
	return false
}

// sanitizeName keeps a device name printable and short enough for the approval
// screen. It is display-only — the gateway owns the stored name.
func sanitizeName(name string) string {
	name = strings.TrimSpace(name)
	if len(name) > 64 {
		name = name[:64]
	}
	return name
}
