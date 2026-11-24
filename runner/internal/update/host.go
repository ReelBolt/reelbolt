package update

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path"
	"path/filepath"
	"strings"
	"time"
)

// DefaultHostBase is where manifests and artifacts are published: an object
// store bucket behind the product's download domain.
const DefaultHostBase = "https://downloads.reelbolt.ai/runner"

// Channels a runner may follow.
const (
	ChannelStable = "stable"
	ChannelBeta   = "beta"
)

// ParseChannel validates a channel name. The channel is a path element in the
// manifest URL, so it is checked rather than interpolated: a value with a slash
// or a ".." in it would address a different document than the user chose.
func ParseChannel(s string) (string, error) {
	s = strings.ToLower(strings.TrimSpace(s))
	switch s {
	case "":
		return ChannelStable, nil
	case ChannelStable, ChannelBeta:
		return s, nil
	default:
		return "", fmt.Errorf("update: unknown channel %q (stable or beta)", s)
	}
}

// ErrHostUnreachable means the update host could not be reached, or answered
// something that is not a manifest. It is explicitly NOT fatal: an unreachable
// update host leaves the runner running the build it already has.
var ErrHostUnreachable = errors.New("update: the update host is unreachable")

// ErrHostNotAllowed means the configured update host is not one this build will
// talk to.
var ErrHostNotAllowed = errors.New("update: the update host is not an allowed host")

// Host is the update host client.
//
// The base URL is attacker-influenceable in the same way the gateway URL is: it
// arrives from the configuration file a user can edit, and it decides which
// server gets to serve code that will be executed. So it is allowlisted exactly
// like the gateway is, with the same escape hatch: a development host is
// accepted only by a non-release build that was told about it.
type Host struct {
	// BaseURL is the manifest root, without the channel. Default DefaultHostBase.
	BaseURL string
	// Channel is stable or beta.
	Channel string
	// DevHosts are extra hosts a non-release build may use.
	DevHosts []string
	// HTTPClient is the transport. Nil means a client with a 5-minute timeout.
	HTTPClient *http.Client
	// Now is the clock, for tests.
	Now func() time.Time
}

// AllowedHost is the product's download host.
const AllowedHost = "downloads.reelbolt.ai"

// ManifestURL is the document this host serves for the configured channel.
func (h *Host) ManifestURL() (string, error) {
	base := strings.TrimSpace(h.BaseURL)
	if base == "" {
		base = DefaultHostBase
	}
	channel, err := ParseChannel(h.Channel)
	if err != nil {
		return "", err
	}
	u, err := url.Parse(base)
	if err != nil {
		return "", fmt.Errorf("update: base url %q: %w", base, err)
	}
	if err := h.checkURL(u); err != nil {
		return "", err
	}
	u.Path = path.Join(u.Path, channel, "manifest.json")
	return u.String(), nil
}

// checkURL enforces the scheme and host rules on both the base and every
// artifact the manifest names.
func (h *Host) checkURL(u *url.URL) error {
	switch u.Scheme {
	case "https":
	case "http":
		// Plaintext is only for a host on this machine or a development host a
		// non-release build was told about: a manifest is what authorises code
		// to run, and TLS is what stops a network attacker replacing it.
		if !isLoopbackHost(u.Hostname()) && !h.isDevHost(u.Host) {
			return fmt.Errorf("%w: %q is not https", ErrHostNotAllowed, u.String())
		}
	default:
		return fmt.Errorf("%w: %q must use https", ErrHostNotAllowed, u.String())
	}
	host := u.Hostname()
	if host == "" {
		return fmt.Errorf("%w: %q has no host", ErrHostNotAllowed, u.String())
	}
	if host == AllowedHost || strings.HasSuffix(host, "."+AllowedHost) {
		return nil
	}
	if isLoopbackHost(host) || h.isDevHost(u.Host) {
		if !devHostAllowed {
			return fmt.Errorf("%w: %q is a development host, which a release build refuses",
				ErrHostNotAllowed, u.String())
		}
		return nil
	}
	return fmt.Errorf("%w: %q is not %s", ErrHostNotAllowed, u.String(), AllowedHost)
}

func (h *Host) isDevHost(host string) bool {
	for _, d := range h.DevHosts {
		if strings.EqualFold(strings.TrimSpace(d), host) {
			return true
		}
	}
	return false
}

func isLoopbackHost(host string) bool {
	switch strings.ToLower(host) {
	case "localhost", "127.0.0.1", "::1", "[::1]":
		return true
	}
	return false
}

func (h *Host) client() *http.Client {
	if h.HTTPClient != nil {
		return h.HTTPClient
	}
	return &http.Client{Timeout: 5 * time.Minute}
}

// FetchManifest downloads manifest.json and its detached signature and verifies
// the signature against keys. The manifest is returned only if the signature
// verified; there is no path that returns an unverified manifest to a caller.
//
// Every failure — a refused connection, a 404, a truncated body, a bad
// signature — is an error. None of them is fatal to the runner (see the package
// comment): the caller logs it and keeps running the current build.
func (h *Host) FetchManifest(ctx context.Context, keys KeySet) (*Manifest, []byte, error) {
	if err := keys.Validate(); err != nil {
		return nil, nil, err
	}
	base, err := h.ManifestURL()
	if err != nil {
		return nil, nil, err
	}
	raw, err := h.get(ctx, base, MaxManifestBytes)
	if err != nil {
		return nil, nil, err
	}
	sig, err := h.get(ctx, base+".sig", MaxSignatureBytes)
	if err != nil {
		return nil, nil, err
	}
	m, err := VerifyManifest(raw, sig, keys)
	if err != nil {
		return nil, nil, err
	}
	// The channel in the document must agree with the channel in the URL. A
	// mismatch means the host served the wrong channel's document (or an
	// attacker with the host's cooperation replayed one); either way the runner
	// does not apply it.
	if m.Channel != "" {
		want, _ := ParseChannel(h.Channel)
		if m.Channel != want {
			return nil, nil, fmt.Errorf("update: the manifest declares channel %q but was fetched from %q",
				m.Channel, want)
		}
	}
	return m, raw, nil
}

func (h *Host) get(ctx context.Context, rawURL string, limit int64) ([]byte, error) {
	u, err := url.Parse(rawURL)
	if err != nil {
		return nil, fmt.Errorf("update: %q: %w", rawURL, err)
	}
	if err := h.checkURL(u); err != nil {
		return nil, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u.String(), nil)
	if err != nil {
		return nil, fmt.Errorf("%w: %w", ErrHostUnreachable, err)
	}
	req.Header.Set("Accept", "application/json, application/octet-stream")
	resp, err := h.client().Do(req)
	if err != nil {
		return nil, fmt.Errorf("%w: %w", ErrHostUnreachable, err)
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("%w: %s answered %s", ErrHostUnreachable, u.Redacted(), resp.Status)
	}
	// One byte over the limit is read so an oversized body is detected rather
	// than silently truncated into something that might parse.
	body, err := io.ReadAll(io.LimitReader(resp.Body, limit+1))
	if err != nil {
		return nil, fmt.Errorf("%w: %w", ErrHostUnreachable, err)
	}
	if int64(len(body)) > limit {
		return nil, fmt.Errorf("update: %s is larger than the %d-byte limit", u.Redacted(), limit)
	}
	return body, nil
}

// Download fetches an artifact into dest, hashing as it goes, and only then
// renames it into place. A download that fails, stops early, or produces the
// wrong bytes leaves dest absent and a .part file removed — never a partial file
// where a caller might pick it up.
//
// The size and SHA-256 are checked against the manifest entry, so the bytes that
// reach Apply are the bytes the release key signed.
func (h *Host) Download(ctx context.Context, a Artifact, dest string) error {
	if err := a.validate(); err != nil {
		return fmt.Errorf("update: artifact %s: %w", a.Key(), err)
	}
	u, err := url.Parse(a.URL)
	if err != nil {
		return fmt.Errorf("update: artifact url %q: %w", a.URL, err)
	}
	if err := h.checkURL(u); err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(dest), 0o700); err != nil {
		return fmt.Errorf("update: create %s: %w", filepath.Dir(dest), err)
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u.String(), nil)
	if err != nil {
		return fmt.Errorf("%w: %w", ErrHostUnreachable, err)
	}
	resp, err := h.client().Do(req)
	if err != nil {
		return fmt.Errorf("%w: %w", ErrHostUnreachable, err)
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("%w: %s answered %s", ErrHostUnreachable, u.Redacted(), resp.Status)
	}
	if resp.ContentLength > a.Size {
		return fmt.Errorf("%w: %s is longer than the manifest's %d bytes",
			ErrDigest, u.Redacted(), a.Size)
	}

	part := dest + partSuffix
	_ = os.Remove(part)
	f, err := os.OpenFile(part, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
	if err != nil {
		return fmt.Errorf("update: create %s: %w", part, err)
	}
	// A .part file is scaffolding, not an artifact: any failure after this point
	// removes it, so a partial download is never left where it could be mistaken
	// for a complete one.
	cleanup := func() { _ = f.Close(); _ = os.Remove(part) }

	hasher := sha256.New()
	written, err := io.Copy(io.MultiWriter(f, hasher), io.LimitReader(resp.Body, a.Size+1))
	if err != nil {
		cleanup()
		return fmt.Errorf("%w: %w", ErrHostUnreachable, err)
	}
	if written != a.Size {
		cleanup()
		return fmt.Errorf("%w: read %d bytes, the manifest says %d", ErrDigest, written, a.Size)
	}
	if err := f.Sync(); err != nil {
		cleanup()
		return fmt.Errorf("update: flush %s: %w", part, err)
	}
	if err := f.Close(); err != nil {
		_ = os.Remove(part)
		return fmt.Errorf("update: close %s: %w", part, err)
	}
	got := hex.EncodeToString(hasher.Sum(nil))
	if !equalHex(got, a.SHA256) {
		_ = os.Remove(part)
		return fmt.Errorf("%w: the download hashes to %s, the manifest says %s",
			ErrDigest, got, strings.ToLower(a.SHA256))
	}
	if err := os.Rename(part, dest); err != nil {
		_ = os.Remove(part)
		return fmt.Errorf("update: %s: %w", dest, err)
	}
	return nil
}

// partSuffix marks an in-progress download. Nothing outside this package reads
// a file with this suffix, and the staging sweeper removes them.
const partSuffix = ".part"
