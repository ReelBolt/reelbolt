package main

import (
	"errors"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// appConfig is the gateway's configuration. Every value has a default except
// DatabaseURL: without the device registry there is nothing to authenticate a
// desktop runner against, so an unset DATABASE_URL is fatal at startup.
type appConfig struct {
	Port        string
	DatabaseURL string

	// DatabaseMaxConns caps each of the gateway's three pgx pools
	// (DATABASE_MAX_CONNS). Zero means unset: pool_max_conns in DATABASE_URL
	// decides, else defaultDatabaseMaxConns (pool.go). A set value below 2 or
	// that is not an integer fails startup.
	DatabaseMaxConns int32

	// GatewayInstance identifies this replica in welcome.gatewayInstance and, for
	// D5c, in runner_sessions.gateway_instance: the pod IP from the downward API
	// (GATEWAY_INSTANCE, which the deployment sets from POD_IP), falling back to
	// the hostname.
	GatewayInstance string

	// PublicHost is the hostname runners dial, which the hello signature binds.
	// Empty means "take it from the request" (X-Forwarded-Host, then Host), which
	// is what happens behind nginx — proxy_set_header Host $host forwards the
	// name without a port.
	PublicHost string

	// PoolCredentials are the accepted hello.auth.credential values for CloudPool
	// workers. Empty means no pool worker can authenticate, which is the honest
	// default: an unconfigured pool credential must never be accepted.
	PoolCredentials []string

	// PoolTokenKey signs on-demand pool node tokens (rbn1.). Empty disables them;
	// when set it must be at least 32 bytes.
	PoolTokenKey string

	// MinAgentVersion is advertised in welcome.minAgentVersion when set; a runner
	// older than it should tell its user to update and must not take jobs.
	MinAgentVersion string

	// InternalToken authenticates the engine-facing internal API
	// (/internal/v1/*). It is REQUIRED for that surface: with no token
	// configured every internal request is refused, because those routes create
	// and cancel work and report which organisations have runners connected.
	InternalToken string

	// JobTokenKey is the HS256 key of the per-job lease tokens. Empty means "mint
	// one for this process": correct for a single replica, and logged as a
	// warning, because with several replicas a token minted by one is not
	// accepted by another.
	JobTokenKey string

	// RabbitMQURL is the broker the RunnerDeviceRevoked fast path consumes from.
	// Empty means the fast path is off and revocation relies on the 60-second
	// revoked_at re-check, which is the protocol's own authoritative backstop.
	RabbitMQURL string

	// Liveness and handshake timings. The defaults are the protocol constants;
	// they are configurable so a test can exercise the timeouts in milliseconds.
	HeartbeatSec       int
	DeadAfterSec       int
	HelloTimeout       time.Duration
	ClockSkew          time.Duration
	NonceTTL           time.Duration
	RevocationInterval time.Duration
	LastSeenInterval   time.Duration

	// Lease and sweep timings (D5b). Zero means "the protocol constant", except
	// for the two intervals: a zero interval turns that loop off, which is what a
	// test that does not want a background sweep relies on.
	LeaseSec         int
	AcceptTimeoutSec int
	CancelGraceSec   int
	ReaperInterval   time.Duration
	AssignInterval   time.Duration

	// PoolOrgSlotCap is the per-organisation cap on concurrent CloudPool slots
	// ("Fair share" in "Routing and targets"). Zero means unlimited: the
	// entitlement numbers come from F11 and do not exist yet, and a gateway that
	// invented its own would silently throttle paying customers.
	PoolOrgSlotCap int

	// InternalPort is the port a PEER replica listens on, used only to reach the
	// owner of a session over the headless service. It defaults to Port; a
	// gateway_instance that already carries a port (a test) overrides it.
	InternalPort string

	// PeersHost (RUNNER_GATEWAY_PEERS_HOST) is a DNS name, normally the headless
	// service, that resolves to every gateway replica. When set, a replica that
	// gets a local-LLM relay request for a runner it does not hold asks the other
	// replicas (at InternalPort, with the internal token, max 8, one at a time)
	// and relays the first answer that is not 503 runner_offline. Empty = off:
	// such a request answers 503 runner_offline. The name must be a
	// cluster-internal headless service: the internal bearer token travels over
	// plain HTTP, so only loopback, private (RFC 1918 / ULA) and link-local
	// answers are used; any other address is skipped and counted under the
	// result label peer_address_skipped.
	PeersHost string

	// InternalTLSAddr (RUNNER_GATEWAY_INTERNAL_TLS_ADDR, e.g. ":8443") turns on a
	// second listener that serves ONLY /internal/** (plus a bare /healthz) over
	// TLS 1.3. Empty = off and the gateway behaves exactly as without it. When
	// set, InternalTLSCertFile and InternalTLSKeyFile
	// (RUNNER_GATEWAY_INTERNAL_TLS_CERT_FILE / _KEY_FILE, PEM) are both
	// required or startup fails. They are re-read when the files change (checked
	// at most every 30 s), so rotation needs no restart.
	InternalTLSAddr     string
	InternalTLSCertFile string
	InternalTLSKeyFile  string

	// InternalTLSClientCAFile (RUNNER_GATEWAY_INTERNAL_TLS_CLIENT_CA_FILE) is an
	// optional PEM bundle. When set the TLS listener requires and verifies a
	// client certificate against it (mutual TLS).
	InternalTLSClientCAFile string

	// InternalTLSAllowNoClientCert
	// (RUNNER_GATEWAY_INTERNAL_TLS_ALLOW_NO_CLIENT_CERT, default false) is the
	// explicit opt-out that lets the TLS listener start without a client CA, in
	// which case any holder of the internal token can connect (WARNING logged).
	// The client CA bundle is re-read when the file changes (at most every 30 s).
	InternalTLSAllowNoClientCert bool

	// InternalPlainLocalOnly (RUNNER_GATEWAY_INTERNAL_PLAIN_LOCAL_ONLY, default
	// false; true/false/1/0/yes/no, case-insensitive, anything else is a fatal
	// startup error) restricts /internal/** on the plain listener to TCP peers
	// that are loopback or inside a subnet of one of the gateway's OWN network
	// interfaces, including the Docker bridge subnet and its gateway address
	// (the host). Any other peer gets 403 internal_tls_required. It relies on
	// default rootful Docker with iptables enabled preserving the caller's real
	// source address; with docker-proxy, rootless Docker or iptables=false every
	// remote caller may appear to come from the bridge gateway and so look local.
	// Peer replicas on ANOTHER node are refused: use the TLS listener for them
	// or turn the flag off. The internal token stays the real control.
	// Everything else on the plain listener is unaffected.
	InternalPlainLocalOnly bool

	// OpTimeoutSec optionally caps every session op below the job's own timeout.
	// Zero means the protocol rule: an op may run as long as its job may, which is
	// what exec and a render need.
	OpTimeoutSec int

	// ProxyTimeout is the slack added to an op's deadline for the extra hop to the
	// owning replica, so the owner is the one that reports a timeout.
	ProxyTimeout time.Duration

	// PoolBufferSlots is the spare capacity GET /internal/v1/scaling asks for on
	// top of the work that exists, so the pool can absorb the next arrival without
	// the job waiting for a cold start.
	PoolBufferSlots int
}

// leaseDuration is how long an assignment is valid before the reaper takes it
// back.
func (c appConfig) leaseDuration() time.Duration {
	sec := c.LeaseSec
	if sec <= 0 {
		sec = protocol.LeaseSec
	}
	return time.Duration(sec) * time.Second
}

// acceptTimeout is how long a runner has to answer an assignment.
func (c appConfig) acceptTimeout() time.Duration {
	sec := c.AcceptTimeoutSec
	if sec <= 0 {
		sec = protocol.AcceptTimeoutSec
	}
	return time.Duration(sec) * time.Second
}

// cancelGrace is the window a runner has to stop a cancelled job.
func (c appConfig) cancelGrace() time.Duration {
	sec := c.CancelGraceSec
	if sec <= 0 {
		sec = protocol.CancelGraceSec
	}
	return time.Duration(sec) * time.Second
}

// deadAfter is how long a connection may be silent before it is closed with 4408.
func (c appConfig) deadAfter() time.Duration {
	return time.Duration(c.DeadAfterSec) * time.Second
}

// loadConfig reads the environment. Most values fall back to their default
// when unparsable; DATABASE_MAX_CONNS is the exception and returns an error,
// because a mistyped pool cap must stop startup rather than quietly become a
// number nobody chose.
func loadConfig() (appConfig, error) {
	maxConns, err := parseDatabaseMaxConns(os.Getenv("DATABASE_MAX_CONNS"))
	if err != nil {
		return appConfig{}, err
	}
	plainLocal, err := parseStrictBool("RUNNER_GATEWAY_INTERNAL_PLAIN_LOCAL_ONLY", os.Getenv("RUNNER_GATEWAY_INTERNAL_PLAIN_LOCAL_ONLY"))
	if err != nil {
		return appConfig{}, err
	}
	allowNoCert, err := parseStrictBool("RUNNER_GATEWAY_INTERNAL_TLS_ALLOW_NO_CLIENT_CERT", os.Getenv("RUNNER_GATEWAY_INTERNAL_TLS_ALLOW_NO_CLIENT_CERT"))
	if err != nil {
		return appConfig{}, err
	}
	cfg := appConfig{
		DatabaseMaxConns:   maxConns,
		Port:               getEnv("PORT", "8090"),
		DatabaseURL:        os.Getenv("DATABASE_URL"),
		GatewayInstance:    getEnv("GATEWAY_INSTANCE", getEnv("POD_IP", hostname())),
		PublicHost:         getEnv("RUNNER_PUBLIC_HOST", ""),
		PoolCredentials:    getListEnv("RUNNER_POOL_CREDENTIALS"),
		PoolTokenKey:       os.Getenv("RUNNER_POOL_TOKEN_KEY"),
		MinAgentVersion:    getEnv("RUNNER_MIN_AGENT_VERSION", ""),
		HeartbeatSec:       getIntEnv("RUNNER_HEARTBEAT_SEC", protocol.HeartbeatSec),
		DeadAfterSec:       getIntEnv("RUNNER_DEAD_AFTER_SEC", protocol.DeadAfterSec),
		HelloTimeout:       getDurationEnv("RUNNER_HELLO_TIMEOUT", protocol.HelloTimeoutSec*time.Second),
		ClockSkew:          getDurationEnv("RUNNER_CLOCK_SKEW", protocol.HelloSkewSec*time.Second),
		NonceTTL:           getDurationEnv("RUNNER_NONCE_TTL", protocol.NonceTTLSeconds*time.Second),
		RevocationInterval: getDurationEnv("RUNNER_REVOCATION_INTERVAL", protocol.RevocationRecheckSec*time.Second),
		LastSeenInterval:   getDurationEnv("RUNNER_LAST_SEEN_INTERVAL", time.Minute),
		InternalToken:      os.Getenv("RUNNER_INTERNAL_TOKEN"),
		JobTokenKey:        os.Getenv("RUNNER_JOB_TOKEN_KEY"),
		RabbitMQURL:        os.Getenv("RABBITMQ_URL"),
		LeaseSec:           getIntEnv("RUNNER_LEASE_SEC", protocol.LeaseSec),
		AcceptTimeoutSec:   getIntEnv("RUNNER_ACCEPT_TIMEOUT_SEC", protocol.AcceptTimeoutSec),
		CancelGraceSec:     getIntEnv("RUNNER_CANCEL_GRACE_SEC", protocol.CancelGraceSec),
		ReaperInterval:     getDurationEnv("RUNNER_REAPER_INTERVAL", 10*time.Second),
		AssignInterval:     getDurationEnv("RUNNER_ASSIGN_INTERVAL", 5*time.Second),
		PoolOrgSlotCap:     getIntEnv("RUNNER_POOL_ORG_SLOT_CAP", 0),
		InternalPort:       os.Getenv("RUNNER_GATEWAY_INTERNAL_PORT"),
		PeersHost:          strings.TrimSpace(os.Getenv("RUNNER_GATEWAY_PEERS_HOST")),

		InternalTLSAddr:              strings.TrimSpace(os.Getenv("RUNNER_GATEWAY_INTERNAL_TLS_ADDR")),
		InternalTLSCertFile:          strings.TrimSpace(os.Getenv("RUNNER_GATEWAY_INTERNAL_TLS_CERT_FILE")),
		InternalTLSKeyFile:           strings.TrimSpace(os.Getenv("RUNNER_GATEWAY_INTERNAL_TLS_KEY_FILE")),
		InternalTLSClientCAFile:      strings.TrimSpace(os.Getenv("RUNNER_GATEWAY_INTERNAL_TLS_CLIENT_CA_FILE")),
		InternalPlainLocalOnly:       plainLocal,
		InternalTLSAllowNoClientCert: allowNoCert,
		// (resolved below: a peer replica listens on the same port this one does)
		OpTimeoutSec:    getIntEnv("RUNNER_OP_TIMEOUT_SEC", 0),
		ProxyTimeout:    getDurationEnv("RUNNER_PROXY_TIMEOUT", 30*time.Second),
		PoolBufferSlots: getIntEnv("RUNNER_POOL_BUFFER_SLOTS", 1),
	}
	if cfg.PoolTokenKey != "" && len(cfg.PoolTokenKey) < minPoolTokenKeyBytes {
		return appConfig{}, fmt.Errorf("RUNNER_POOL_TOKEN_KEY must be at least %d bytes", minPoolTokenKeyBytes)
	}
	if cfg.InternalPort == "" {
		cfg.InternalPort = cfg.Port
	}
	if err := validateInternalTLSConfig(cfg); err != nil {
		return appConfig{}, err
	}
	return cfg, nil
}

func hostname() string {
	h, err := os.Hostname()
	if err != nil || h == "" {
		return "runner-gateway"
	}
	return h
}

func getEnv(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func getListEnv(key string) []string {
	var out []string
	for _, part := range strings.Split(os.Getenv(key), ",") {
		if v := strings.TrimSpace(part); v != "" {
			out = append(out, v)
		}
	}
	return out
}

func getIntEnv(key string, fallback int) int {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	n, err := strconv.Atoi(v)
	if err != nil || n <= 0 {
		return fallback
	}
	return n
}

func getDurationEnv(key string, fallback time.Duration) time.Duration {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	d, err := time.ParseDuration(v)
	if err != nil || d <= 0 {
		return fallback
	}
	return d
}

// parseStrictBool parses true/false/1/0/yes/no (case-insensitive); empty is
// false. Anything else is an error rather than a silent "off".
func parseStrictBool(name, v string) (bool, error) {
	switch strings.ToLower(strings.TrimSpace(v)) {
	case "", "false", "0", "no":
		return false, nil
	case "true", "1", "yes":
		return true, nil
	}
	return false, fmt.Errorf("%s=%q is not a boolean (use true/false/1/0/yes/no)", name, v)
}

// validateInternalTLSConfig is the pure startup check for the internal TLS
// variables (no file access; file readability is checked when the listener is
// built, still before anything listens).
func validateInternalTLSConfig(c appConfig) error {
	if c.InternalTLSAddr == "" {
		var stray []string
		if c.InternalTLSCertFile != "" {
			stray = append(stray, "RUNNER_GATEWAY_INTERNAL_TLS_CERT_FILE")
		}
		if c.InternalTLSKeyFile != "" {
			stray = append(stray, "RUNNER_GATEWAY_INTERNAL_TLS_KEY_FILE")
		}
		if c.InternalTLSClientCAFile != "" {
			stray = append(stray, "RUNNER_GATEWAY_INTERNAL_TLS_CLIENT_CA_FILE")
		}
		if c.InternalTLSAllowNoClientCert {
			stray = append(stray, "RUNNER_GATEWAY_INTERNAL_TLS_ALLOW_NO_CLIENT_CERT")
		}
		if len(stray) > 0 {
			return fmt.Errorf("%s set but RUNNER_GATEWAY_INTERNAL_TLS_ADDR is empty: the TLS listener would silently not start",
				strings.Join(stray, ", "))
		}
		return nil
	}
	if c.InternalTLSCertFile == "" || c.InternalTLSKeyFile == "" {
		return errors.New("RUNNER_GATEWAY_INTERNAL_TLS_ADDR is set, so RUNNER_GATEWAY_INTERNAL_TLS_CERT_FILE and " +
			"RUNNER_GATEWAY_INTERNAL_TLS_KEY_FILE are both required")
	}
	if c.InternalTLSClientCAFile != "" && c.InternalTLSAllowNoClientCert {
		return errors.New("RUNNER_GATEWAY_INTERNAL_TLS_ALLOW_NO_CLIENT_CERT=true contradicts RUNNER_GATEWAY_INTERNAL_TLS_CLIENT_CA_FILE: " +
			"remove one of them (the client CA makes a client certificate required)")
	}
	if c.InternalTLSClientCAFile == "" && !c.InternalTLSAllowNoClientCert {
		return errors.New("RUNNER_GATEWAY_INTERNAL_TLS_ADDR is set without RUNNER_GATEWAY_INTERNAL_TLS_CLIENT_CA_FILE: " +
			"set the client CA, or set RUNNER_GATEWAY_INTERNAL_TLS_ALLOW_NO_CLIENT_CERT=true to accept any TLS client")
	}
	return nil
}
