// Package agent is the runner side of the runner protocol
// (docs/runner-protocol.md): it dials the gateway, authenticates with the
// device's Ed25519 key, advertises capabilities, renews its leases, claims
// jobs, proxies each job's ops to a session executor, and closes cleanly with
// the codes the specification defines.
//
// It is the library both `reelbolt-worker` (D13) and the desktop app (Area E)
// embed, so it carries no UI of any kind: no Wails, no tray, no windowing and
// no HTTP listener. Area E adds those on top, never through this package. It
// contains no provider credential and no field that could hold one — job
// execution is credential-free by construction (see the package's
// TestNoProviderCredentialCanBeCarried).
//
// The wire format is not re-declared here. Every envelope, payload, constant
// and close code comes from sandbox/pkg/protocol, which is the one source of
// the protocol's shape for the gateway (D5a) and the worker alike.
package agent

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// Logger is the subset of *log.Logger the agent uses. Config.Logger injects it
// so a test can assert on what the library writes: the specification forbids
// logging credentials, device signatures, job tokens and presigned URLs, and
// that is checked rather than promised.
type Logger interface {
	Printf(format string, args ...any)
}

// Identity is how the runner proves who it is in hello.auth. Exactly one of the
// two shapes is used, chosen by Kind.
//
// A device's private key never leaves the device: it is held in memory here and
// is used for nothing but the hello signature.
type Identity struct {
	// DeviceID is runner_devices.id for a paired desktop runner.
	DeviceID string
	// PrivateKey is the device's Ed25519 key, created at pairing.
	PrivateKey ed25519.PrivateKey
	// Credential is the pool secret of a CloudPool worker.
	Credential string
	// WorkerName is the enrolled CloudPool worker's name.
	WorkerName string
}

// Kind reports which auth shape this identity produces.
func (id Identity) Kind() string {
	if id.DeviceID != "" && len(id.PrivateKey) > 0 {
		return protocol.AuthKindDevice
	}
	return protocol.AuthKindPool
}

// Valid reports whether the identity has everything its shape needs.
func (id Identity) Valid() error {
	switch id.Kind() {
	case protocol.AuthKindDevice:
		if id.DeviceID == "" {
			return errors.New("agent: device identity needs a device id")
		}
		if len(id.PrivateKey) != ed25519.PrivateKeySize {
			return fmt.Errorf("agent: device identity needs an Ed25519 private key, got %d bytes", len(id.PrivateKey))
		}
	case protocol.AuthKindPool:
		if id.Credential == "" || id.WorkerName == "" {
			return errors.New("agent: pool identity needs a credential and a worker name")
		}
	}
	return nil
}

// Config is everything the agent needs to run. There is deliberately no field
// that can hold a provider key, endpoint secret or `env:` reference: the runner
// never calls a model provider.
type Config struct {
	// GatewayURL is the address dialled, e.g. wss://runner.example/v1/connect.
	GatewayURL string
	// Identity is the device key or the pool credential.
	Identity Identity
	// Capabilities is the hello.capabilities document. Slots bounds how many
	// jobs run concurrently.
	Capabilities protocol.Capabilities
	// Sessions builds the executor for an assigned job. Required.
	Sessions Factory
	// CapabilitiesFunc, when set, is asked for the capability document at every
	// hello (so it can carry fresh optional fields such as localLlm). Capabilities
	// stays the fallback and still bounds Slots.
	CapabilitiesFunc func() protocol.Capabilities
	// LLM serves the gateway's llm.* relay messages. Nil means they are ignored.
	LLM LLMHandler

	// GatewayHost overrides the host the hello signature binds. Empty means the
	// host of GatewayURL, which is what a runner dials and therefore what the
	// gateway was reached at.
	GatewayHost string

	// ScratchRoot is the directory the agent creates one sub-directory per job
	// in and deletes on job end. Empty means os.TempDir()/reelbolt-runner.
	ScratchRoot string
	// DiskQuotaBytes caps one session's scratch directory. 0 means 8 GiB.
	DiskQuotaBytes int64

	// HTTPClient performs the presigned GET/PUT transfers of stage, publish,
	// hydrate and render. Nil means a client with a 30-minute timeout and no
	// redirect following.
	HTTPClient *http.Client
	// Dialer opens the socket. Nil means a real WebSocket dial.
	Dialer Dialer
	// Logger receives operational lines. Nil means log.Default().
	Logger Logger
	// Now is the clock. Nil means time.Now.
	Now func() time.Time
	// Rand is the entropy source for hello nonces and backoff jitter. Nil means
	// crypto/rand.Reader.
	Rand io.Reader

	// Heartbeat is the interval between heartbeats. 0 means
	// protocol.HeartbeatSec.
	Heartbeat time.Duration
	// HelloTimeout bounds the wait for welcome after hello. 0 means
	// protocol.HelloTimeoutSec.
	HelloTimeout time.Duration
	// ByeWait bounds the wait for the close frame that follows a gateway bye.
	// The spec calls bye an "orderly goodbye immediately before closing", and
	// the close code is what decides whether reconnecting is allowed, so the
	// runner reads on briefly rather than acting on the goodbye alone. 0 means
	// 5s.
	ByeWait time.Duration
	// DialTimeout bounds one dial attempt. 0 means 30s.
	DialTimeout time.Duration
	// OpTimeout bounds an op that carries no timeoutSec of its own. 0 means 10m.
	OpTimeout time.Duration
	// CancelGrace is how long a cancelled session has to stop. 0 means
	// protocol.CancelGraceSec.
	CancelGrace time.Duration
	// LeaseLostAfter is how long a job may keep running while the runner is
	// disconnected. The gateway stops renewing a lease once the socket is gone,
	// so past this window the job is lost whatever the runner does. 0 means
	// protocol.LeaseSec.
	LeaseLostAfter time.Duration

	// Backoff is the reconnect schedule after a connection ends for a reason
	// that may be temporary.
	Backoff Backoff
	// StopOnSuperseded makes a 4409 (a newer connection from the same device)
	// terminal instead of a reconnect loop. A superseded runner that reconnected
	// would fight the new one for the socket forever, so this defaults to true
	// and only an explicit false turns it off.
	StopOnSuperseded *bool
}

// Defaults applied to a zero value in newAgent.
const (
	defaultScratchBytes int64 = 8 << 30 // 8 GiB
	defaultDialTimeout        = 30 * time.Second
	defaultOpTimeout          = 10 * time.Minute
	defaultHelloTimeout       = protocol.HelloTimeoutSec * time.Second
	defaultHeartbeat          = protocol.HeartbeatSec * time.Second
	defaultCancelGrace        = protocol.CancelGraceSec * time.Second
	defaultByeWait            = 5 * time.Second
	defaultLeaseLost          = protocol.LeaseSec * time.Second
)

// Agent is one runner's connection. Create it with New and drive it with Run.
//
// An Agent is safe for concurrent use, but a runner holds exactly one: a second
// connection with the same identity supersedes the first (close code 4409).
type Agent struct {
	cfg Config
	log Logger

	// life is the parent of every job context. Jobs outlive connections — a
	// gateway restart must not kill a running render — but not the agent.
	life       context.Context
	lifeCancel context.CancelFunc

	mu        sync.Mutex
	conn      *connection
	jobs      map[string]*jobState
	draining  bool
	stopped   bool
	stopErr   error
	attempt   int
	connected time.Time
}

// New builds an agent. It validates the configuration eagerly so a
// misconfigured runner fails at startup rather than on every reconnect.
func New(cfg Config) (*Agent, error) {
	if err := cfg.Identity.Valid(); err != nil {
		return nil, err
	}
	if cfg.GatewayURL == "" {
		return nil, errors.New("agent: GatewayURL is required")
	}
	u, err := url.Parse(cfg.GatewayURL)
	if err != nil {
		return nil, fmt.Errorf("agent: GatewayURL: %w", err)
	}
	if u.Host == "" {
		return nil, fmt.Errorf("agent: GatewayURL %q has no host", cfg.GatewayURL)
	}
	if cfg.Sessions == nil {
		return nil, errors.New("agent: Sessions is required")
	}
	if err := cfg.Capabilities.Validate(); err != nil {
		return nil, fmt.Errorf("agent: capabilities: %w", err)
	}
	a := &Agent{cfg: cfg, jobs: map[string]*jobState{}}
	a.life, a.lifeCancel = context.WithCancel(context.Background())
	if a.cfg.Logger != nil {
		a.log = a.cfg.Logger
	} else {
		a.log = log.Default()
	}
	if a.cfg.Now == nil {
		a.cfg.Now = time.Now
	}
	if a.cfg.Rand == nil {
		a.cfg.Rand = rand.Reader
	}
	if a.cfg.Dialer == nil {
		a.cfg.Dialer = &WebSocketDialer{}
	}
	if a.cfg.HTTPClient == nil {
		a.cfg.HTTPClient = DefaultHTTPClient()
	}
	if a.cfg.ScratchRoot == "" {
		a.cfg.ScratchRoot = filepath.Join(os.TempDir(), "reelbolt-runner")
	}
	if a.cfg.DiskQuotaBytes == 0 {
		a.cfg.DiskQuotaBytes = defaultScratchBytes
	}
	if a.cfg.GatewayHost == "" {
		a.cfg.GatewayHost = protocol.NormalizeGatewayHost(u.Host)
	}
	if a.cfg.Heartbeat == 0 {
		a.cfg.Heartbeat = defaultHeartbeat
	}
	if a.cfg.HelloTimeout == 0 {
		a.cfg.HelloTimeout = defaultHelloTimeout
	}
	if a.cfg.ByeWait == 0 {
		a.cfg.ByeWait = defaultByeWait
	}
	if a.cfg.DialTimeout == 0 {
		a.cfg.DialTimeout = defaultDialTimeout
	}
	if a.cfg.OpTimeout == 0 {
		a.cfg.OpTimeout = defaultOpTimeout
	}
	if a.cfg.CancelGrace == 0 {
		a.cfg.CancelGrace = defaultCancelGrace
	}
	if a.cfg.LeaseLostAfter == 0 {
		a.cfg.LeaseLostAfter = defaultLeaseLost
	}
	if a.cfg.Backoff.isZero() {
		a.cfg.Backoff = DefaultBackoff()
	}
	return a, nil
}

// Logger returns the logger the agent writes to, for embedders that want to
// share one.
func (a *Agent) Logger() Logger { return a.log }

// GatewayURL returns the address dialled, for status output.
func (a *Agent) GatewayURL() string { return a.cfg.GatewayURL }

// Run dials, handshakes and serves until ctx ends, the runner is shut down, or
// the gateway answers something that retrying cannot fix.
//
// A connection that ends for a temporary reason — a gateway deploy, a lost
// network, a heartbeat timeout — is retried on the configured backoff, and jobs
// still held are listed in the next hello so the gateway can resume them. The
// returned error wraps ErrTerminal when the refusal was final: an unsupported
// protocol version, a bad or revoked identity, or a superseded connection.
func (a *Agent) Run(ctx context.Context) error {
	for {
		if err := ctx.Err(); err != nil {
			a.teardownAll("context cancelled")
			return err
		}
		if err := a.stopReason(); err != nil {
			a.teardownAll("stopped")
			return err
		}
		started := a.cfg.Now()
		err := a.runOnce(ctx)
		a.logf("connection ended: %v (terminal=%v)", err, IsTerminal(err))
		if err := ctx.Err(); err != nil {
			a.teardownAll("context cancelled")
			return err
		}
		if stop := a.stopReason(); stop != nil {
			a.teardownAll("stopped")
			return stop
		}
		var terminal *terminalError
		if errors.As(err, &terminal) {
			a.teardownAll("terminal: " + terminal.Error())
			return err
		}
		if a.cfg.Now().Sub(started) >= a.cfg.Backoff.ResetAfter {
			a.mu.Lock()
			a.attempt = 0
			a.mu.Unlock()
		}
		delay := a.nextBackoff()
		a.logf("connection ended (%v); reconnecting in %s", err, delay.Round(time.Millisecond))
		timer := time.NewTimer(delay)
		select {
		case <-timer.C:
		case <-ctx.Done():
			timer.Stop()
			a.teardownAll("context cancelled")
			return ctx.Err()
		}
	}
}

// Drain stops taking new work and says so, then waits for the caller to finish
// or shut down. A session job is not waited for: the specification says drain
// rehydrates sessions elsewhere, so held sessions are torn down here.
func (a *Agent) Drain(reason string, deadline time.Duration) error {
	a.mu.Lock()
	already := a.draining
	a.draining = true
	c := a.conn
	a.mu.Unlock()
	if already {
		return nil
	}
	a.logf("draining (%s): no new jobs will be accepted", reason)
	a.teardownSessions("drained")
	if c == nil {
		return nil
	}
	secs := int(deadline / time.Second)
	if err := c.send(protocol.TypeDrain, protocol.DrainPayload{Reason: reason, DeadlineSec: secs}); err != nil {
		return err
	}
	return nil
}

// Shutdown ends the connection the way the specification wants it ended: a
// bye, then a close. Held sessions are torn down first; they are rehydrated by
// the engine (D7b) rather than waited for.
func (a *Agent) Shutdown(ctx context.Context) error {
	a.mu.Lock()
	a.stopped = true
	if a.stopErr == nil {
		a.stopErr = ErrStopped
	}
	c := a.conn
	a.mu.Unlock()

	a.teardownAll("shutdown")
	// The job contexts are children of the agent's lifetime, not of the
	// connection's: cancelling it here releases anything a session left behind
	// however the job ended.
	a.lifeCancel()
	if c == nil {
		return nil
	}
	_ = c.send(protocol.TypeBye, protocol.ByePayload{Reason: protocol.CancelShutdown})
	c.close(protocol.CloseGoingAway, "runner shutting down")
	_ = ctx
	return nil
}

// stopReason reports the terminal error once Shutdown or a terminal refusal has
// been recorded.
func (a *Agent) stopReason() error {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.stopped {
		if a.stopErr != nil {
			return a.stopErr
		}
		return ErrStopped
	}
	return nil
}

func (a *Agent) nextBackoff() time.Duration {
	a.mu.Lock()
	attempt := a.attempt
	a.attempt++
	a.mu.Unlock()
	return a.cfg.Backoff.Delay(attempt, a.cfg.Rand)
}

func (a *Agent) logf(format string, args ...any) {
	if a.log == nil {
		return
	}
	a.log.Printf(format, args...)
}

// DefaultHTTPClient builds the client used for presigned transfers. Redirects
// are not followed: a presigned URL grants one object and one method, and
// following a redirect would leave that grant.
func DefaultHTTPClient() *http.Client {
	return &http.Client{
		Timeout: 30 * time.Minute,
		// A node that cannot reach the storage host must fail the op at once: without these a stage
		// blocked in connect or waiting for headers until the whole-request Timeout (30 min).
		Transport: guardedTransport(),
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return errors.New("agent: presigned URL redirects are not followed")
		},
	}
}

// RedactForLog strips the query string from a URL for logging: a presigned URL
// is a bearer secret for one object and MUST be logged with the query removed.
func RedactForLog(raw string) string {
	u, err := url.Parse(raw)
	if err != nil {
		return "[invalid url]"
	}
	u.RawQuery = ""
	u.Fragment = ""
	u.User = nil
	return u.String()
}

func newNonce(r io.Reader) (string, error) {
	// 32 bytes, comfortably past the 16 the gateway requires, so the encoding
	// choice is never what makes a hello fail.
	buf := make([]byte, 32)
	if _, err := io.ReadFull(r, buf); err != nil {
		return "", fmt.Errorf("agent: read nonce: %w", err)
	}
	return base64.RawURLEncoding.EncodeToString(buf), nil
}

func guardedTransport() *http.Transport {
	tr := http.DefaultTransport.(*http.Transport).Clone()
	tr.DialContext = (&net.Dialer{Timeout: 15 * time.Second, KeepAlive: 30 * time.Second}).DialContext
	tr.TLSHandshakeTimeout = 15 * time.Second
	tr.ResponseHeaderTimeout = 60 * time.Second
	tr.IdleConnTimeout = 90 * time.Second
	tr.MaxIdleConns = 10
	return tr
}
