// Package pairing is the device's side of the pairing flow (RFC 8628's device
// authorization grant, which E4b implemented the server half of).
//
// The machine proves possession of a key it generates locally, the user types a
// short code into the dashboard and approves the device there, and the poll then
// yields the device id, the organization and the gateway URL. The private key
// never leaves this machine and is never sent anywhere; only the 32-byte public
// key is uploaded, and the approval screen shows the name, OS and version the
// user is deciding about.
//
// Everything here is the daemon's own traffic: it talks to the API, not to the
// gateway, and it holds no bearer token of any kind. The gateway handshake later
// authenticates with an Ed25519 signature over the hello, so there is nothing to
// keep from this flow except the key and the three values the poll returned.
package pairing

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/url"
	"os/exec"
	"runtime"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// RFC 8628 error codes the server can answer with. Anything else is reported
// verbatim rather than mapped, because guessing at an unknown code would hide a
// server change.
const (
	codePending  = "authorization_pending"
	codeSlowDown = "slow_down"
	codeDenied   = "access_denied"
	codeExpired  = "expired_token"
)

// Sentinel errors a caller maps to an exit message.
var (
	// ErrAccessDenied means the user denied the request in the dashboard.
	ErrAccessDenied = errors.New("pairing: the request was denied")
	// ErrExpired means the code's ten-minute window closed, or was already used.
	ErrExpired = errors.New("pairing: the pairing code expired")
	// ErrNoGateway means the server approved the device but has no gateway address to
	// give it (RUNNER_GATEWAY_URL is unset on the server). Nothing is wrong with this
	// machine; the operator of the service has to finish setting it up.
	ErrNoGateway = errors.New("pairing: approved, but the service has not published an address for runners to connect to yet; this is a setup problem on the ReelBolt side, not on this computer")
	// ErrAlreadyPaired means this key is already a device on the account.
	ErrAlreadyPaired = errors.New("pairing: this machine is already paired")
)

// Client is the pairing client. A zero Client is not usable: APIBaseURL is
// required.
type Client struct {
	// APIBaseURL is the Inference API (or the dashboard's origin), e.g.
	// https://api.reelbolt.ai. The pairing routes live under /api/v1/runners.
	APIBaseURL string
	// DeviceName is what the approval screen shows. Required.
	DeviceName string
	// Version is the runner's version string, shown to the approver.
	Version string
	// HTTPClient performs the requests. Nil means http.DefaultClient with a
	// 30-second timeout.
	HTTPClient *http.Client
	// OpenBrowser opens the verification URI. Nil means the platform opener;
	// a test substitutes a recorder.
	OpenBrowser func(u string) error
	// Logger receives progress lines. Nil means log.Default().
	Logger Logger
	// Now and Sleep make the poll loop's clock injectable.
	Now   func() time.Time
	Sleep func(context.Context, time.Duration) error
}

// Logger is the one method pairing needs.
type Logger interface {
	Printf(format string, args ...any)
}

// Request is one pairing attempt.
type Request struct {
	// PublicKey is the device's Ed25519 public key, encoded canonically.
	PublicKey ed25519.PublicKey
	// OS and Arch default to this build's GOOS/GOARCH.
	OS, Arch string
}

// Pending is an open pairing request: what the user needs to see, and what the
// poll needs to present.
type Pending struct {
	DeviceCode      string
	UserCode        string
	VerificationURI string
	ExpiresIn       int
	Interval        int
	// CodeURL is the verification URI with the user code as a query parameter.
	// A browser opened at this address shows the code pre-filled.
	CodeURL string
}

// Result is an approved pairing.
type Result struct {
	DeviceID       string
	OrganizationID string
	GatewayURL     string
}

// httpError is a non-2xx answer carrying the RFC 8628 error envelope.
type httpError struct {
	Status int
	Code   string
	Body   string
}

func (e *httpError) Error() string {
	if e.Code != "" {
		return fmt.Sprintf("pairing: HTTP %d: %s", e.Status, e.Code)
	}
	return fmt.Sprintf("pairing: HTTP %d", e.Status)
}

// Start opens a pairing request and returns what the user must be shown.
func (c *Client) Start(ctx context.Context, req Request) (*Pending, error) {
	if strings.TrimSpace(c.APIBaseURL) == "" {
		return nil, errors.New("pairing: APIBaseURL is required")
	}
	if strings.TrimSpace(c.DeviceName) == "" {
		return nil, errors.New("pairing: a device name is required; the approver sees it")
	}
	if len(req.PublicKey) != ed25519.PublicKeySize {
		return nil, fmt.Errorf("pairing: expected a %d-byte Ed25519 public key, got %d",
			ed25519.PublicKeySize, len(req.PublicKey))
	}
	osName, arch := req.OS, req.Arch
	if osName == "" {
		osName = runtime.GOOS
	}
	if arch == "" {
		arch = runtime.GOARCH
	}
	body := map[string]string{
		"publicKey": protocol.EncodeDevicePublicKey(req.PublicKey),
		"name":      c.DeviceName,
		"os":        osName,
		"arch":      arch,
		"version":   c.Version,
	}
	var out struct {
		DeviceCode      string `json:"deviceCode"`
		UserCode        string `json:"userCode"`
		VerificationURI string `json:"verificationUri"`
		ExpiresIn       int    `json:"expiresIn"`
		Interval        int    `json:"interval"`
	}
	if err := c.do(ctx, http.MethodPost, "/api/v1/runners/pair/start", body, &out); err != nil {
		var he *httpError
		if errors.As(err, &he) && he.Code == "already_paired" {
			return nil, ErrAlreadyPaired
		}
		return nil, err
	}
	if out.DeviceCode == "" || out.UserCode == "" || out.VerificationURI == "" {
		return nil, errors.New("pairing: the server's pairing response was incomplete")
	}
	p := &Pending{
		DeviceCode:      out.DeviceCode,
		UserCode:        out.UserCode,
		VerificationURI: out.VerificationURI,
		ExpiresIn:       out.ExpiresIn,
		Interval:        out.Interval,
	}
	p.CodeURL = verificationURL(out.VerificationURI, out.UserCode)
	return p, nil
}

// verificationURL appends the user code as a query parameter, preserving any
// query the server already put in the URI.
func verificationURL(uri, userCode string) string {
	if userCode == "" {
		return uri
	}
	sep := "?"
	if strings.Contains(uri, "?") {
		sep = "&"
	}
	return uri + sep + "code=" + url.QueryEscape(userCode)
}

// Poll waits for the user to approve the request, following RFC 8628: it polls
// no faster than the advertised interval, backs off on slow_down, and stops at
// the expiry the server gave.
func (c *Client) Poll(ctx context.Context, p *Pending) (*Result, error) {
	if p == nil || p.DeviceCode == "" {
		return nil, errors.New("pairing: Poll needs an open request")
	}
	interval := time.Duration(p.Interval) * time.Second
	if interval <= 0 {
		interval = 5 * time.Second
	}
	deadline := c.now().Add(time.Duration(p.ExpiresIn) * time.Second)
	if p.ExpiresIn <= 0 {
		deadline = c.now().Add(10 * time.Minute)
	}
	for {
		if err := c.sleep(ctx, interval); err != nil {
			return nil, err
		}
		if !c.now().Before(deadline) {
			return nil, ErrExpired
		}
		var out struct {
			DeviceID       string `json:"deviceId"`
			OrganizationID string `json:"organizationId"`
			GatewayURL     string `json:"gatewayUrl"`
		}
		err := c.do(ctx, http.MethodPost, "/api/v1/runners/pair/poll",
			map[string]string{"deviceCode": p.DeviceCode}, &out)
		if err == nil {
			if out.DeviceID == "" {
				return nil, errors.New("pairing: the approval response was incomplete")
			}
			if out.GatewayURL == "" {
				// A server that has no gateway address configured (RUNNER_GATEWAY_URL)
				// approves the device but has nowhere to send it. Say so, because
				// "incomplete" reads like a bug in this program.
				return nil, ErrNoGateway
			}
			return &Result{
				DeviceID:       out.DeviceID,
				OrganizationID: out.OrganizationID,
				GatewayURL:     out.GatewayURL,
			}, nil
		}
		var he *httpError
		if !errors.As(err, &he) {
			// A transport failure is not the server's verdict, so it is retried:
			// the request is still open, and the code is good for ten minutes.
			c.logf("pairing poll failed (%v); retrying", err)
			continue
		}
		switch he.Code {
		case codePending:
			continue
		case codeSlowDown:
			interval += 5 * time.Second
			c.logf("the server asked the poll to slow down; interval is now %s", interval)
			continue
		case codeDenied:
			return nil, ErrAccessDenied
		case codeExpired:
			return nil, ErrExpired
		default:
			if he.Status >= 500 {
				// A gateway or proxy 5xx is not a verdict on the request: the code is
				// still open, so the loop keeps polling until the advertised expiry.
				c.logf("the server answered HTTP %d; retrying", he.Status)
				continue
			}
			return nil, err
		}
	}
}

// Pair runs the whole flow: open, open the browser, wait, return the result.
func (c *Client) Pair(ctx context.Context, req Request) (*Pending, *Result, error) {
	p, err := c.Start(ctx, req)
	if err != nil {
		return nil, nil, err
	}
	c.instructions(p)
	if err := c.open(p.CodeURL); err != nil {
		c.logf("could not open a browser (%v); open this address manually: %s", err, p.CodeURL)
	}
	res, err := c.Poll(ctx, p)
	if err != nil {
		return p, nil, err
	}
	return p, res, nil
}

func (c *Client) instructions(p *Pending) {
	c.logf("")
	c.logf("To pair this machine, open %s", p.CodeURL)
	c.logf("and enter this code: %s", p.UserCode)
	c.logf("")
	c.logf("Only approve a code that is displayed on a device you own.")
	c.logf("This request expires in %d minutes.", p.ExpiresIn/60)
}

func (c *Client) open(u string) error {
	if c.OpenBrowser != nil {
		return c.OpenBrowser(u)
	}
	return OpenBrowser(u)
}

// OpenBrowser hands a URL to the platform's opener. It never waits for the
// browser: the opener exits immediately and the poll continues.
func OpenBrowser(u string) error {
	var cmd *exec.Cmd
	switch runtime.GOOS {
	case "windows":
		cmd = exec.CommandContext(context.Background(), "rundll32", "url.dll,FileProtocolHandler", u)
	case "darwin":
		cmd = exec.CommandContext(context.Background(), "open", u)
	default:
		cmd = exec.CommandContext(context.Background(), "xdg-open", u)
	}
	if err := cmd.Start(); err != nil {
		return err
	}
	go func() { _ = cmd.Wait() }()
	return nil
}

func (c *Client) do(ctx context.Context, method, path string, in, out any) error {
	body, err := json.Marshal(in)
	if err != nil {
		return fmt.Errorf("pairing: encode request: %w", err)
	}
	base := strings.TrimRight(c.APIBaseURL, "/")
	req, err := http.NewRequestWithContext(ctx, method, base+path, bytes.NewReader(body))
	if err != nil {
		return fmt.Errorf("pairing: build request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")
	resp, err := c.client().Do(req)
	if err != nil {
		return fmt.Errorf("pairing: %s %s: %w", method, path, err)
	}
	defer func() { _ = resp.Body.Close() }()
	// A pairing response is a few hundred bytes; anything larger is not one.
	raw, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return fmt.Errorf("pairing: read response: %w", err)
	}
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		var env struct {
			Error string `json:"error"`
		}
		_ = json.Unmarshal(raw, &env)
		return &httpError{Status: resp.StatusCode, Code: env.Error, Body: strings.TrimSpace(string(raw))}
	}
	if out == nil {
		return nil
	}
	if err := json.Unmarshal(raw, out); err != nil {
		return fmt.Errorf("pairing: parse response: %w", err)
	}
	return nil
}

func (c *Client) client() *http.Client {
	if c.HTTPClient != nil {
		return c.HTTPClient
	}
	return &http.Client{Timeout: 30 * time.Second}
}

func (c *Client) now() time.Time {
	if c.Now != nil {
		return c.Now()
	}
	return time.Now()
}

func (c *Client) sleep(ctx context.Context, d time.Duration) error {
	if c.Sleep != nil {
		return c.Sleep(ctx, d)
	}
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-timer.C:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (c *Client) logf(format string, args ...any) {
	l := c.Logger
	if l == nil {
		l = log.Default()
	}
	l.Printf(format, args...)
}
