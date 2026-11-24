package main

import (
	"context"
	"crypto/subtle"
	"encoding/base64"
	"errors"
	"fmt"
	"log"
	"strings"
	"time"

	"github.com/coder/websocket"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// handshakeError is a refused hello: the close code, the metric label and the
// short reason the connection is closed with. A non-empty bye is sent as a bye
// message first, which "Versioning and compatibility" requires when the gateway
// refuses a version it does not speak.
type handshakeError struct {
	code   protocol.CloseCode
	metric string
	reason string
	bye    string
}

func (e *handshakeError) Error() string { return e.reason }

// refuse builds a refusal with a stable metric label.
func refuse(code protocol.CloseCode, metric, format string, args ...any) *handshakeError {
	return &handshakeError{code: code, metric: metric, reason: fmt.Sprintf(format, args...)}
}

// minNonceBytes is the smallest hello.auth.nonce the gateway accepts: "at least
// 16 random bytes, base64url".
const minNonceBytes = 16

// handshakeSwitchTimeout bounds the platform_settings read the handshake makes.
// It is the same order as the claim path's own budget, and it exists so that a
// stalled settings read cannot turn into a handshake that never answers: the
// timeout takes the unreadable branch, which accepts the hello and assigns it
// nothing.
const handshakeSwitchTimeout = 10 * time.Second

// handshake authenticates a hello and returns the connection it describes.
//
// It never logs the credential, the signature or the nonce value: "Credentials,
// device signatures and job tokens MUST NOT be logged".
func (g *gateway) handshake(ctx context.Context, env protocol.Envelope, ws *websocket.Conn, hosts []string, remote string, connectedAt time.Time) (*conn, *handshakeError) {
	hello, err := protocol.DecodePayload[protocol.HelloPayload](env)
	if err != nil {
		return nil, refuse(protocol.CloseUnsupportedVersion, "malformed_hello", "hello payload is not valid JSON: %v", err)
	}
	version, ok := protocol.NegotiateVersion(hello.ProtocolVersion)
	if !ok {
		return nil, &handshakeError{
			code:   protocol.CloseUnsupportedVersion,
			metric: "unsupported_version",
			reason: "unsupported protocol version",
			bye:    fmt.Sprintf("unsupported protocol version %q: this gateway speaks %s", hello.ProtocolVersion, protocol.ProtocolVersion),
		}
	}
	if err := hello.Validate(); err != nil {
		return nil, refuse(protocol.CloseUnsupportedVersion, "malformed_hello", "%v", err)
	}

	switch hello.Auth.Kind {
	case protocol.AuthKindDevice:
		return g.handshakeDevice(ctx, hello, version, ws, hosts, remote, connectedAt)
	case protocol.AuthKindPool:
		return g.handshakePool(ctx, hello, version, ws, hosts, remote, connectedAt)
	}
	return nil, refuse(protocol.CloseUnsupportedVersion, "malformed_hello", "auth.kind %q is not one of device, pool", hello.Auth.Kind)
}

// handshakeDevice verifies the Ed25519 hello of a paired desktop runner.
//
// Order matters: the timestamp, the nonce shape and the signature are all
// checked before anything is written down, and the nonce is claimed only after
// the signature verifies, so an attacker cannot burn a victim's nonce or fill
// the replay cache with unsigned hellos.
func (g *gateway) handshakeDevice(ctx context.Context, hello protocol.HelloPayload, version string, ws *websocket.Conn, hosts []string, remote string, connectedAt time.Time) (*conn, *handshakeError) {
	auth := hello.Auth
	ts, err := time.Parse(time.RFC3339, auth.TS)
	if err != nil {
		return nil, refuse(protocol.CloseUnsupportedVersion, "malformed_hello", "hello.auth.ts is not RFC 3339: %v", err)
	}
	now := g.now()
	if skew := now.Sub(ts); skew > g.cfg.ClockSkew || skew < -g.cfg.ClockSkew {
		return nil, refuse(protocol.CloseUnauthenticated, "clock_skew",
			"hello.auth.ts is %s away from this gateway's clock, the window is %s", skew.Round(time.Second), g.cfg.ClockSkew)
	}
	nonce, err := base64.RawURLEncoding.DecodeString(auth.Nonce)
	if err != nil {
		if nonce, err = base64.URLEncoding.DecodeString(auth.Nonce); err != nil {
			return nil, refuse(protocol.CloseUnsupportedVersion, "malformed_hello", "hello.auth.nonce is not base64url")
		}
	}
	if len(nonce) < minNonceBytes {
		return nil, refuse(protocol.CloseUnsupportedVersion, "malformed_hello",
			"hello.auth.nonce is %d bytes, at least %d are required", len(nonce), minNonceBytes)
	}

	device, lookupErr := g.store.LookupDevice(ctx, auth.DeviceID)
	if errors.Is(lookupErr, ErrDeviceNotFound) {
		return nil, refuse(protocol.CloseUnauthenticated, "unknown_device", "no runner is registered with that device id")
	}
	if lookupErr != nil {
		return nil, internalError("reading the device registry: %v", lookupErr)
	}
	if device.Revoked() {
		return nil, refuse(protocol.CloseUnauthenticated, "revoked_device",
			"device was revoked at %s", device.RevokedAt.UTC().Format(time.RFC3339))
	}
	if device.PublicKey == "" {
		return nil, refuse(protocol.CloseUnauthenticated, "no_public_key", "device has no registered public key")
	}
	if device.OrganizationID == "" {
		// A desktop runner always belongs to an organisation: a job for one must
		// never be offered to a device with no tenant to match it against.
		return nil, refuse(protocol.CloseForbidden, "no_organization", "device is not attached to an organisation")
	}
	verified := false
	for _, host := range hosts {
		if err := protocol.VerifyHelloSignature(device.PublicKey, auth.DeviceID, auth.TS, auth.Nonce, host, auth.Sig); err == nil {
			verified = true
			break
		}
	}
	if !verified {
		return nil, refuse(protocol.CloseUnauthenticated, "bad_signature", "hello signature does not verify against the registered device key")
	}
	if ok, saturated := g.nonces.claim(auth.DeviceID, auth.Nonce, now); !ok {
		if saturated {
			g.metrics.nonceSaturated.Inc()
			return nil, refuse(protocol.CloseRateLimited, "nonce_cache_saturated", "the replay cache is full, try again shortly")
		}
		g.metrics.nonceReplays.Inc()
		return nil, refuse(protocol.CloseUnauthenticated, "replayed_nonce", "that hello nonce has already been used")
	}

	// The platform kill switch (F11). It is checked only now, after the hello is
	// authenticated: a refusal must never be an answer to an unauthenticated
	// caller, and 4403 says something true about an identity the gateway has
	// already verified.
	//
	// The unreadable case is the ONE place the platform deliberately does not act
	// on state it cannot see. 4403 is TERMINAL in pkg/agent — retryableClose
	// returns false — so refusing a hello because platform_settings was briefly
	// unreadable would end a runner's reconnect loop for good, which is a worse
	// bug than the pause it would be enforcing. The hello is accepted instead and
	// the runner simply never receives work: with no readable snapshot the claim
	// gate refuses every assignment (see switches.go). When the store IS readable
	// and the row says the organisation is paused, the refusal is real and the
	// metric is the one the protocol table already reserves for it.
	//
	// A CloudPool worker has no organisation to check, so this is the desktop path
	// only; a disabled organisation's pool jobs are skipped per candidate in
	// ClaimJobs, which is where a worker that serves many organisations can be
	// gated at all.
	// The read is bounded. platform_settings is a dependency of the handshake that
	// did not exist before F11, and a database that hangs must cost this
	// connection a bounded pause rather than a handshake that never answers —
	// which would hold a socket, and a goroutine, for as long as the outage lasts.
	// Expiry reads as unreadable, which is the accept-and-assign-nothing direction
	// above rather than a refusal.
	switchCtx, switchCancel := context.WithTimeout(ctx, handshakeSwitchTimeout)
	switches, switchErr := g.switches.Snapshot(switchCtx)
	switchCancel()
	if switchErr != nil {
		log.Printf("%s: platform switches are unreadable (%v); accepting the hello, no work will be assigned",
			connLogTag(protocol.KindDesktop, device.ID, remote), switchErr)
	} else if switches.OrganizationDisabled(device.OrganizationID) {
		return nil, refuse(protocol.CloseForbidden, "organization_disabled",
			"this organisation is disabled on the platform")
	}

	return &conn{
		runnerID:        device.ID,
		kind:            protocol.KindDesktop,
		orgID:           device.OrganizationID,
		name:            device.Name,
		remote:          remote,
		host:            hosts[0],
		caps:            hello.Capabilities,
		ws:              ws,
		logTag:          connLogTag(protocol.KindDesktop, device.ID, remote),
		protocolVersion: version,
		connected:       connectedAt,
		lastSeen:        connectedAt,
		running:         map[string]int64{},
		offers:          map[string]int64{},
		rejected:        map[string]time.Time{},
		sinceHeartbeat:  map[string]struct{}{},
	}, nil
}

// handshakePool verifies a CloudPool worker's shared credential.
//
// The credential is compared in constant time and the worker must still have a
// runner_devices row (kind CloudPool): a worker with no row is refused rather
// than handed a synthetic identity, so a leaked credential alone does not become
// a way to appear as a runner nobody enrolled.
func (g *gateway) handshakePool(ctx context.Context, hello protocol.HelloPayload, version string, ws *websocket.Conn, hosts []string, remote string, connectedAt time.Time) (*conn, *handshakeError) {
	auth := hello.Auth
	if strings.HasPrefix(auth.Credential, nodeTokenPrefix) {
		// A waker-minted node token: signature, expiry and name binding are checked
		// here; the revoked/enrolled row checks below apply to it as to any pool
		// worker. Only the name and the outcome are logged, never the token.
		if !g.nodeTokenAccepted(auth.Credential, auth.WorkerName) {
			log.Printf("pool node %q: node token refused", auth.WorkerName)
			return nil, refuse(protocol.CloseUnauthenticated, "bad_pool_credential", "pool credential does not match")
		}
	} else if !g.poolCredentialAccepted(auth.Credential) {
		return nil, refuse(protocol.CloseUnauthenticated, "bad_pool_credential", "pool credential does not match")
	}
	device, err := g.store.LookupPoolWorker(ctx, auth.WorkerName)
	if errors.Is(err, ErrDeviceNotFound) {
		return nil, refuse(protocol.CloseUnauthenticated, "unknown_pool_worker", "no CloudPool worker is enrolled with that name")
	}
	if err != nil {
		return nil, internalError("reading the device registry: %v", err)
	}
	if device.Kind != protocol.KindCloudPool {
		return nil, refuse(protocol.CloseUnauthenticated, "unknown_pool_worker", "that name is not a CloudPool worker")
	}
	if device.Revoked() {
		return nil, refuse(protocol.CloseUnauthenticated, "revoked_device",
			"worker was revoked at %s", device.RevokedAt.UTC().Format(time.RFC3339))
	}
	return &conn{
		runnerID: device.ID,
		kind:     protocol.KindCloudPool,
		// A pool worker has no organisation (runner_devices.organization_id is
		// null for CloudPool), so welcome.orgId is null for it.
		orgID:           "",
		name:            device.Name,
		remote:          remote,
		host:            hosts[0],
		caps:            hello.Capabilities,
		ws:              ws,
		logTag:          connLogTag(protocol.KindCloudPool, device.ID, remote),
		protocolVersion: version,
		connected:       connectedAt,
		lastSeen:        connectedAt,
		running:         map[string]int64{},
		offers:          map[string]int64{},
		rejected:        map[string]time.Time{},
		sinceHeartbeat:  map[string]struct{}{},
	}, nil
}

// poolCredentialAccepted compares a presented credential against every
// configured one in constant time. No configured credential means no pool worker
// can connect, which is the safe default for a deployment that has not enrolled
// any.
func (g *gateway) poolCredentialAccepted(presented string) bool {
	if len(g.cfg.PoolCredentials) == 0 || presented == "" {
		return false
	}
	ok := false
	for _, expected := range g.cfg.PoolCredentials {
		if subtle.ConstantTimeCompare([]byte(presented), []byte(expected)) == 1 {
			ok = true
		}
	}
	return ok
}

// refuseConnection sends the refusal and closes the socket. The reason on the
// close frame is short and never contains a credential: a desktop runner's
// identity failure is reported to its owner by design, but nothing else is.
func (g *gateway) refuseConnection(ws *websocket.Conn, remote string, refusal *handshakeError) {
	g.metrics.handshakes.WithLabelValues(refusal.metric).Inc()
	g.metrics.countClose(int(refusal.code))
	log.Printf("refused connection from %s: %s (%s)", remote, refusal.reason, refusal.metric)
	if refusal.bye != "" {
		ctx, cancel := context.WithTimeout(context.Background(), g.writeTimeout)
		if env, err := protocol.New(protocol.TypeBye, protocol.ByePayload{Reason: refusal.bye}); err == nil {
			_ = ws.Write(ctx, websocket.MessageText, mustEncode(env))
		}
		cancel()
	}
	_ = ws.Close(websocket.StatusCode(refusal.code), clampReason(refusal.reason))
}

func mustEncode(env protocol.Envelope) []byte {
	raw, err := env.Encode()
	if err != nil {
		return []byte("{}")
	}
	return raw
}

// clampReason keeps a close reason inside the 123-byte limit RFC 6455 puts on
// the close frame.
func clampReason(reason string) string {
	const limit = 123
	if len(reason) <= limit {
		return reason
	}
	return reason[:limit]
}
