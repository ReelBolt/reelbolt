package protocol_test

import (
	"crypto/ed25519"
	"crypto/rand"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

func decodeRaw(t *testing.T, raw []byte) protocol.Envelope {
	t.Helper()
	env, err := protocol.Decode(raw)
	if err != nil {
		t.Fatalf("Decode: %v", err)
	}
	return env
}

func helloEnvelope(t fb) []byte {
	t.Helper()
	env, err := protocol.New(protocol.TypeHello, protocol.HelloPayload{
		ProtocolVersion: protocol.ProtocolVersion,
		Auth: protocol.HelloAuth{
			Kind: protocol.AuthKindDevice, DeviceID: "c3a1f0de-2b4c-4d8e-9a10-6e5f7b8c9d01",
			TS: "2026-10-05T09:00:00Z", Nonce: "Zml4dHVyZS1ub25jZS0wMDAxMjM0NTY3", Sig: "sig",
		},
		Capabilities: protocol.Capabilities{
			ProtocolVersion: protocol.ProtocolVersion, AgentVersion: "0.1.0",
			OS: "linux", Arch: "amd64", Slots: 2, Container: protocol.ContainerDocker,
			FfmpegVersion: "7.1", FontsetVersion: "1",
			JobTypes: protocol.JobTypes(), MaxOutputBytes: 1 << 32,
		},
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	raw, err := env.Encode()
	if err != nil {
		t.Fatalf("Encode: %v", err)
	}
	return raw
}

func TestDecodeRejectsMalformedEnvelopes(t *testing.T) {
	valid := helloEnvelope(t)

	tooLarge := make([]byte, protocol.ReadLimitBytes+1)
	copy(tooLarge, valid)

	cases := []struct {
		name string
		raw  []byte
		want error
	}{
		{"empty", nil, protocol.ErrMalformed},
		{"not json", []byte("not json"), protocol.ErrMalformed},
		{"array frame", []byte("[]"), protocol.ErrMalformed},
		{"version 2", []byte(mustJSON(t, map[string]any{"v": 2, "type": "hello", "id": "a", "payload": map[string]any{}})), protocol.ErrUnsupportedVersion},
		{"missing id", []byte(mustJSON(t, map[string]any{"v": 1, "type": "hello", "payload": map[string]any{}})), protocol.ErrMalformed},
		{"missing payload", []byte(mustJSON(t, map[string]any{"v": 1, "type": "hello", "id": "a"})), protocol.ErrMalformed},
		{"payload is an array", []byte(mustJSON(t, map[string]any{"v": 1, "type": "hello", "id": "a", "payload": []any{}})), protocol.ErrMalformed},
		{"unknown type", []byte(mustJSON(t, map[string]any{"v": 1, "type": "future.thing", "id": "a", "payload": map[string]any{}})), protocol.ErrUnknownType},
		{"past the read limit", tooLarge, protocol.ErrTooLarge},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if _, err := protocol.Decode(tc.raw); !errors.Is(err, tc.want) {
				t.Fatalf("Decode error = %v, want %v", err, tc.want)
			}
		})
	}

	// The unknown-type case still hands back the envelope, so a receiver can log
	// what it ignored.
	env, err := protocol.Decode([]byte(mustJSON(t, map[string]any{"v": 1, "type": "future.thing", "id": "a", "payload": map[string]any{}})))
	if !errors.Is(err, protocol.ErrUnknownType) {
		t.Fatalf("want ErrUnknownType, got %v", err)
	}
	if env.Type != "future.thing" || env.ID != "a" {
		t.Fatalf("unknown-type envelope was not returned: %+v", env)
	}
}

func TestDecodeIgnoresUnknownFields(t *testing.T) {
	raw := map[string]any{
		"v": 1, "type": "hello", "id": "a",
		"somethingNew": "ignored",
		"payload":      map[string]any{"protocolVersion": "1.0", "futureField": true},
	}
	env := decodeRaw(t, []byte(mustJSON(t, raw)))
	hello, err := protocol.DecodePayload[protocol.HelloPayload](env)
	if err != nil {
		t.Fatalf("DecodePayload: %v", err)
	}
	if hello.ProtocolVersion != "1.0" {
		t.Fatalf("protocolVersion = %q", hello.ProtocolVersion)
	}
}

func TestEnvelopeEmptyPayloadEncodesAsObject(t *testing.T) {
	env := protocol.Envelope{V: protocol.Version, Type: protocol.TypeReady, ID: "a"}
	raw, err := env.Encode()
	if err != nil {
		t.Fatalf("Encode: %v", err)
	}
	if !strings.Contains(string(raw), string('"')+"payload"+string('"')+":{}") {
		t.Fatalf("empty payload must encode as {}, got %s", raw)
	}
}

func TestValidateRequiresScopeFields(t *testing.T) {
	env, err := protocol.New(protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	if err := env.Validate(); err != nil {
		t.Fatalf("connection-scoped envelope should validate: %v", err)
	}

	jobScoped := env
	jobScoped.Type = protocol.TypeAssign
	if err := jobScoped.Validate(); err == nil {
		t.Fatal("assign without jobId must not validate")
	}
	jobScoped.JobID = "7d1c2e0a-5b7e-4a43-9d57-3f0c1a9b2e11"
	if err := jobScoped.Validate(); err != nil {
		t.Fatalf("assign with a jobId should validate: %v", err)
	}

	session, err := protocol.NewJob(protocol.TypeOpRequest, "7d1c2e0a-5b7e-4a43-9d57-3f0c1a9b2e11", "0b6f3c58-91d2-4c7a-8e34-5a2d7f1e9c40", protocol.OpRequestPayload{OpID: "op-1", Op: protocol.OpClose, Args: json.RawMessage("{}"), TimeoutSec: 10})
	if err != nil {
		t.Fatalf("NewJob: %v", err)
	}
	if err := session.Validate(); err != nil {
		t.Fatalf("session-scoped envelope should validate: %v", err)
	}
	session.SessionID = ""
	if err := session.Validate(); err == nil {
		t.Fatal("op.request without sessionId must not validate")
	}

	if _, err := protocol.NewJob(protocol.TypeBye, "j", "", protocol.ByePayload{Reason: "shutdown"}); err == nil {
		t.Fatal("NewJob must refuse a connection-scoped type")
	}
}

func TestMessageCatalogue(t *testing.T) {
	types := protocol.MessageTypes()
	if len(types) != 21 {
		t.Fatalf("the catalogue has %d types, want 21", len(types))
	}
	for _, ty := range types {
		if !ty.Known() {
			t.Errorf("%s is in the catalogue but not Known", ty)
		}
		if _, err := protocol.New(ty, map[string]any{}); err != nil {
			t.Errorf("New(%s): %v", ty, err)
		}
	}
	if protocol.MessageType("nope").Known() {
		t.Error("an unknown type must not be Known")
	}

	scopes := map[protocol.MessageType]protocol.Scope{
		protocol.TypeHello: protocol.ScopeConnection, protocol.TypeWelcome: protocol.ScopeConnection,
		protocol.TypeHeartbeat: protocol.ScopeConnection, protocol.TypeReady: protocol.ScopeConnection,
		protocol.TypeDrain: protocol.ScopeConnection, protocol.TypeBye: protocol.ScopeConnection,
		protocol.TypeAssign: protocol.ScopeJob, protocol.TypeAccept: protocol.ScopeJob,
		protocol.TypeReject: protocol.ScopeJob, protocol.TypeProgress: protocol.ScopeJob,
		protocol.TypeComplete: protocol.ScopeJob, protocol.TypeFail: protocol.ScopeJob,
		protocol.TypeCancel:    protocol.ScopeJob,
		protocol.TypeOpRequest: protocol.ScopeSession, protocol.TypeOpResponse: protocol.ScopeSession,
	}
	for ty, want := range scopes {
		if got := ty.Scope(); got != want {
			t.Errorf("%s scope = %v, want %v", ty, got, want)
		}
	}

	// MessageTypes must hand back a copy: a caller reordering it cannot corrupt
	// the catalogue.
	types[0] = "mutated"
	if protocol.MessageTypes()[0] != protocol.TypeHello {
		t.Fatal("MessageTypes returned the package slice")
	}
}

func TestTypedPayloadRoundTripsEveryType(t *testing.T) {
	cases := []struct {
		env   func(t *testing.T) protocol.Envelope
		check func(t *testing.T, payload any)
	}{
		{
			env: func(t *testing.T) protocol.Envelope {
				return decodeRaw(t, helloEnvelope(t))
			},
			check: func(t *testing.T, payload any) {
				if h, ok := payload.(*protocol.HelloPayload); !ok || h.Auth.Kind != protocol.AuthKindDevice {
					t.Fatalf("hello payload = %#v", payload)
				}
			},
		},
		{
			env: func(t *testing.T) protocol.Envelope {
				org := "5e2b1c90-7a3d-4f68-8b21-0c9d4e6f1a22"
				env, err := protocol.New(protocol.TypeWelcome, protocol.WelcomePayload{
					RunnerID: "r", Kind: protocol.KindDesktop, OrgID: &org,
					ProtocolVersion: protocol.ProtocolVersion, HeartbeatSec: 15, DeadAfterSec: 45,
					MaxInlineBytes: protocol.MaxPayloadBytes, GatewayInstance: "10.0.3.17",
					ServerTime: "2026-10-05T09:00:00Z", Resumed: []protocol.JobLease{},
				})
				if err != nil {
					t.Fatalf("New: %v", err)
				}
				return env
			},
			check: func(t *testing.T, payload any) {
				w, ok := payload.(*protocol.WelcomePayload)
				if !ok || w.Resumed == nil || len(w.Resumed) != 0 {
					t.Fatalf("welcome payload = %#v", payload)
				}
			},
		},
		{
			env: func(t *testing.T) protocol.Envelope {
				env, err := protocol.New(protocol.TypeHeartbeat, protocol.HeartbeatPayload{
					ServerTime: "2026-10-05T09:00:15Z", Leases: []protocol.LeaseStatus{},
				})
				if err != nil {
					t.Fatalf("New: %v", err)
				}
				return env
			},
			check: func(t *testing.T, payload any) {
				if h, ok := payload.(*protocol.HeartbeatPayload); !ok || h.FreeSlots != nil {
					t.Fatalf("gateway heartbeat payload = %#v", payload)
				}
			},
		},
		{
			env: func(t *testing.T) protocol.Envelope {
				zero := 0
				env, err := protocol.New(protocol.TypeHeartbeat, protocol.HeartbeatPayload{FreeSlots: &zero})
				if err != nil {
					t.Fatalf("New: %v", err)
				}
				return env
			},
			check: func(t *testing.T, payload any) {
				h, ok := payload.(*protocol.HeartbeatPayload)
				if !ok || h.FreeSlots == nil || *h.FreeSlots != 0 {
					t.Fatalf("zero free slots must survive the round trip: %#v", payload)
				}
			},
		},
		{
			env: func(t *testing.T) protocol.Envelope {
				env, err := protocol.New(protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1})
				if err != nil {
					t.Fatalf("New: %v", err)
				}
				return env
			},
			check: nil,
		},
		{
			env: func(t *testing.T) protocol.Envelope {
				env, err := protocol.NewJob(protocol.TypeAssign, "j", "s", protocol.AssignPayload{
					JobType: protocol.JobRemotionWorkspace, LeaseEpoch: 3, JobToken: "FIXTURE.job.token",
					Attempt: 1, TimeoutSec: 14400, OrgID: "o",
					Limits:   protocol.AssignLimits{MaxOutputBytes: 1 << 31, IdleTimeoutSec: 1800},
					Requires: protocol.AssignRequires{RuntimeImageDigest: "sha256:aa", Container: protocol.ContainerDocker},
					Spec:     json.RawMessage(mustJSON(t, protocol.RemotionWorkspaceSpec{ExecutionID: "e", Template: protocol.RemotionTemplate{Name: "n", Version: "1"}})),
				})
				if err != nil {
					t.Fatalf("NewJob: %v", err)
				}
				return env
			},
			check: func(t *testing.T, payload any) {
				a, ok := payload.(*protocol.AssignPayload)
				if !ok {
					t.Fatalf("assign payload = %#v", payload)
				}
				var spec protocol.RemotionWorkspaceSpec
				if err := a.SpecInto(&spec); err != nil || spec.ExecutionID != "e" {
					t.Fatalf("SpecInto: %v (%+v)", err, spec)
				}
			},
		},
		{
			env: func(t *testing.T) protocol.Envelope {
				env, err := protocol.NewJob(protocol.TypeOpRequest, "j", "s", protocol.OpRequestPayload{
					OpID: "op-1", Op: protocol.OpWriteFile,
					Args: json.RawMessage(mustJSON(t, map[string]any{"path": "src/Root.tsx", "encoding": "utf8"})), TimeoutSec: 120,
				})
				if err != nil {
					t.Fatalf("NewJob: %v", err)
				}
				return env
			},
			check: func(t *testing.T, payload any) {
				op, ok := payload.(*protocol.OpRequestPayload)
				if !ok {
					t.Fatalf("op.request payload = %#v", payload)
				}
				var args struct {
					Path     string `json:"path"`
					Encoding string `json:"encoding"`
				}
				if err := op.ArgsInto(&args); err != nil || args.Path != "src/Root.tsx" || args.Encoding != "utf8" {
					t.Fatalf("ArgsInto: %v (%+v)", err, args)
				}
			},
		},
		{
			env: func(t *testing.T) protocol.Envelope {
				env, err := protocol.NewJob(protocol.TypeOpResponse, "j", "s", protocol.OpResponsePayload{
					LeaseEpoch: 1, JobToken: "t", OpID: "op-1", OK: false,
					Error: &protocol.OpError{Code: protocol.ErrCodeArgvNotAllowed, Message: "no", Retryable: false},
				})
				if err != nil {
					t.Fatalf("NewJob: %v", err)
				}
				return env
			},
			check: func(t *testing.T, payload any) {
				r, ok := payload.(*protocol.OpResponsePayload)
				if !ok || r.OK || r.Error == nil || r.Error.Code != protocol.ErrCodeArgvNotAllowed {
					t.Fatalf("op.response payload = %#v", payload)
				}
			},
		},
	}
	for _, tc := range cases {
		env := tc.env(t)
		payload, err := env.TypedPayload()
		if err != nil {
			t.Fatalf("%s: TypedPayload: %v", env.Type, err)
		}
		if payload == nil {
			t.Fatalf("%s: TypedPayload returned nil", env.Type)
		}
		if tc.check != nil {
			tc.check(t, payload)
		}
		raw, err := env.Encode()
		if err != nil {
			t.Fatalf("%s: Encode: %v", env.Type, err)
		}
		again, err := protocol.Decode(raw)
		if err != nil {
			t.Fatalf("%s: Decode: %v", env.Type, err)
		}
		if _, err := again.TypedPayload(); err != nil {
			t.Fatalf("%s: TypedPayload after round trip: %v", env.Type, err)
		}
	}

	unknown := protocol.Envelope{V: 1, Type: "future.thing", ID: "a", Payload: json.RawMessage("{}")}
	if _, err := unknown.TypedPayload(); !errors.Is(err, protocol.ErrUnknownType) {
		t.Fatalf("TypedPayload on an unknown type = %v, want ErrUnknownType", err)
	}
}

func TestHelloSignature(t *testing.T) {
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	const (
		deviceID = "c3a1f0de-2b4c-4d8e-9a10-6e5f7b8c9d01"
		ts       = "2026-10-05T09:00:00Z"
		nonce    = "Zml4dHVyZS1ub25jZS0wMDAxMjM0NTY3"
		host     = "runner.reelbolt.ai"
	)
	sig := protocol.SignHello(priv, deviceID, ts, nonce, host)
	key := protocol.EncodeDevicePublicKey(pub)

	if want := "reelbolt-runner-hello-v1|" + deviceID + "|" + ts + "|" + nonce + "|" + host; protocol.HelloSigningString(deviceID, ts, nonce, host) != want {
		t.Fatalf("signing string = %q, want %q", protocol.HelloSigningString(deviceID, ts, nonce, host), want)
	}
	if err := protocol.VerifyHelloSignature(key, deviceID, ts, nonce, host, sig); err != nil {
		t.Fatalf("signature should verify: %v", err)
	}
	// Binding the host is the whole point: a hostile gateway must not be able to
	// replay the hello at another one.
	if err := protocol.VerifyHelloSignature(key, deviceID, ts, nonce, "evil.example.com", sig); err == nil {
		t.Fatal("a signature made for another host must not verify")
	}
	if err := protocol.VerifyHelloSignature(key, deviceID, ts, "b3RoZXI", host, sig); err == nil {
		t.Fatal("a signature must be bound to its nonce")
	}
	if err := protocol.VerifyHelloSignature(key, "00000000-0000-0000-0000-000000000000", ts, nonce, host, sig); err == nil {
		t.Fatal("a signature must be bound to its device id")
	}
	if err := protocol.VerifyHelloSignature(key, deviceID, ts, nonce, host, "not-base64!"); err == nil {
		t.Fatal("a malformed signature must not verify")
	}
	if err := protocol.VerifyHelloSignature("", deviceID, ts, nonce, host, sig); err == nil {
		t.Fatal("an empty public key must not verify")
	}
}

func TestParseDevicePublicKeyAcceptsThePlausibleEncodings(t *testing.T) {
	pub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	der, err := x509.MarshalPKIXPublicKey(pub)
	if err != nil {
		t.Fatalf("MarshalPKIXPublicKey: %v", err)
	}
	pemBlock := pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: der})

	cases := map[string]string{
		"canonical":      protocol.EncodeDevicePublicKey(pub),
		"base64url raw":  base64.RawURLEncoding.EncodeToString(pub),
		"base64 std raw": base64.RawStdEncoding.EncodeToString(pub),
		"pkix pem":       string(pemBlock),
	}
	if protocol.EncodeDevicePublicKey(pub) != base64.StdEncoding.EncodeToString(pub) {
		t.Fatal("the canonical encoding must be standard base64, matching runner_devices.public_key")
	}
	for name, encoded := range cases {
		parsed, err := protocol.ParseDevicePublicKey(encoded)
		if err != nil {
			t.Errorf("%s: %v", name, err)
			continue
		}
		if !parsed.Equal(pub) {
			t.Errorf("%s: parsed key differs", name)
		}
	}
	for name, encoded := range map[string]string{
		"empty":       "",
		"garbage":     "!!!not a key!!!",
		"too short":   base64.StdEncoding.EncodeToString(pub[:16]),
		"pem but bad": "-----BEGIN PUBLIC KEY-----\nnope\n-----END PUBLIC KEY-----",
	} {
		if _, err := protocol.ParseDevicePublicKey(encoded); err == nil {
			t.Errorf("%s: expected an error", name)
		}
	}
}

func TestNegotiateVersion(t *testing.T) {
	cases := []struct {
		peer string
		want string
		ok   bool
	}{
		{"1.0", "1.0", true},
		{"1", "1.0", true},
		{"1.5", "1.0", true},
		{"2.0", "", false},
		{"0.9", "", false},
		{"", "", false},
		{"one.zero", "", false},
	}
	for _, tc := range cases {
		got, ok := protocol.NegotiateVersion(tc.peer)
		if ok != tc.ok || got != tc.want {
			t.Errorf("NegotiateVersion(%q) = (%q, %v), want (%q, %v)", tc.peer, got, ok, tc.want, tc.ok)
		}
	}
}

func TestNormalizeGatewayHost(t *testing.T) {
	cases := map[string]string{
		"Runner.Example.com:443": "runner.example.com",
		"runner.example.com":     "runner.example.com",
		"runner.example.com.":    "runner.example.com",
		"[::1]:8080":             "[::1]",
		"[::1]":                  "[::1]",

		"": "",
	}
	for in, want := range cases {
		if got := protocol.NormalizeGatewayHost(in); got != want {
			t.Errorf("NormalizeGatewayHost(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestCapabilitiesValidate(t *testing.T) {
	digest := "sha256:aa"
	valid := protocol.Capabilities{
		ProtocolVersion: "1.0", Slots: 2, Container: protocol.ContainerDocker,
		RuntimeImageDigest: &digest, JobTypes: protocol.JobTypes(), MaxOutputBytes: 1 << 30,
	}
	if err := valid.Validate(); err != nil {
		t.Fatalf("a docker runner should validate: %v", err)
	}
	noRuntime := valid
	noRuntime.Container = protocol.ContainerNone
	noRuntime.RuntimeImageDigest = nil
	noRuntime.JobTypes = protocol.JobTypesWithoutContainer()
	if err := noRuntime.Validate(); err != nil {
		t.Fatalf("a container-less runner should validate: %v", err)
	}

	cases := map[string]func(c *protocol.Capabilities){
		"no slots":          func(c *protocol.Capabilities) { c.Slots = 0 },
		"unknown container": func(c *protocol.Capabilities) { c.Container = "lxc" },
		"bad protocol":      func(c *protocol.Capabilities) { c.ProtocolVersion = "2.0" },
		"negative cap":      func(c *protocol.Capabilities) { c.MaxOutputBytes = -1 },
		"none with digest": func(c *protocol.Capabilities) {
			c.Container = protocol.ContainerNone
			c.JobTypes = protocol.JobTypesWithoutContainer()
		},
		"none with remotion": func(c *protocol.Capabilities) {
			c.Container = protocol.ContainerNone
			c.RuntimeImageDigest = nil
			c.JobTypes = protocol.JobTypes()
		},
	}
	for name, mutate := range cases {
		c := valid
		mutate(&c)
		if err := c.Validate(); err == nil {
			t.Errorf("%s: expected a validation error", name)
		}
	}

	// An unrecognised job type is an unknown case, never a parse error.
	openSet := valid
	openSet.JobTypes = append(append([]string{}, valid.JobTypes...), "future.job")
	if err := openSet.Validate(); err != nil {
		t.Errorf("an unknown job type must be tolerated: %v", err)
	}
	if !openSet.Supports(protocol.JobVideoCompile) {
		t.Error("Supports must report an advertised type")
	}
	if openSet.Supports("never.advertised") {
		t.Error("Supports must not report a type the runner did not advertise")
	}
}

func TestHelloValidate(t *testing.T) {
	hello := protocol.HelloPayload{
		ProtocolVersion: "1.0",
		Auth:            protocol.HelloAuth{Kind: protocol.AuthKindPool, Credential: "c", WorkerName: "w"},
		Capabilities: protocol.Capabilities{
			ProtocolVersion: "1.0", Slots: 1, Container: protocol.ContainerNone,
			JobTypes: protocol.JobTypesWithoutContainer(), MaxOutputBytes: 1,
		},
	}
	if err := hello.Validate(); err != nil {
		t.Fatalf("a pool hello should validate: %v", err)
	}
	if _, ok := hello.PoolAuth(); !ok {
		t.Error("PoolAuth should report the pool shape")
	}
	if _, ok := hello.DeviceAuth(); ok {
		t.Error("DeviceAuth should not report the pool shape")
	}

	bad := hello
	bad.Auth = protocol.HelloAuth{Kind: protocol.AuthKindDevice}
	if err := bad.Validate(); err == nil {
		t.Error("a device auth without its fields must not validate")
	}
	bad = hello
	bad.Auth = protocol.HelloAuth{Kind: "wat"}
	if err := bad.Validate(); err == nil {
		t.Error("an unknown auth kind must not validate")
	}
	bad = hello
	bad.ProtocolVersion = "9.9"
	if err := bad.Validate(); err == nil {
		t.Error("an unsupported protocol version must not validate")
	}
}

func TestAuthRedactsSecrets(t *testing.T) {
	auth := protocol.HelloAuth{Kind: protocol.AuthKindPool, Credential: "credential-value-abc", WorkerName: "w", Sig: "signature-value-xyz"}
	rendered := jsonOf(t, auth.LogValue())
	for _, secret := range []string{"credential-value-abc", "signature-value-xyz"} {
		if strings.Contains(rendered, secret) {
			t.Errorf("LogValue leaked %q: %s", secret, rendered)
		}
	}
	if !strings.Contains(rendered, "[redacted]") {
		t.Errorf("LogValue should mark redacted fields: %s", rendered)
	}
}

func TestHelloSkew(t *testing.T) {
	now := time.Date(2026, 10, 5, 9, 0, 0, 0, time.UTC)
	if d := protocol.HelloSkew(now, now.Add(-119*time.Second)); d <= 0 || d > protocol.HelloSkewSec*time.Second {
		t.Fatalf("skew = %s", d)
	}
}

func FuzzDecodeNeverPanics(f *testing.F) {
	f.Add(helloEnvelope(f))
	f.Add([]byte("{}"))
	f.Add([]byte("[]"))
	f.Add([]byte(""))
	f.Fuzz(func(t *testing.T, raw []byte) {
		env, err := protocol.Decode(raw)
		if err != nil {
			return
		}
		// Anything that decodes must be re-encodable and re-decodable.
		out, err := env.Encode()
		if err != nil {
			t.Fatalf("Encode after a successful Decode: %v", err)
		}
		if _, err := protocol.Decode(out); err != nil {
			t.Fatalf("Decode of our own output: %v", err)
		}
	})
}

// fb is the slice of testing.TB helloEnvelope and the helpers need, so the fuzz
// target can build the same seed fixture as the table tests.
type fb interface {
	Helper()
	Fatalf(string, ...any)
}

func mustJSON(t fb, v any) string {
	t.Helper()
	raw, err := json.Marshal(v)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	return string(raw)
}

func jsonOf(t *testing.T, v any) string {
	t.Helper()
	raw, err := json.Marshal(v)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	return string(raw)
}

func TestLocalLLMCapabilitiesValidate(t *testing.T) {
	ok := protocol.Capabilities{ProtocolVersion: protocol.ProtocolVersion, Slots: 1, Container: protocol.ContainerNone}
	ok.LocalLLM = &protocol.LocalLLMCapabilities{Enabled: true, ServerKind: "ollama", Models: []string{"a"}, MaxConcurrent: 2}
	if err := ok.Validate(); err != nil {
		t.Fatalf("valid localLlm rejected: %v", err)
	}
	many := make([]string, protocol.LLMMaxModels+1)
	for i := range many {
		many[i] = "m"
	}
	bad := []protocol.LocalLLMCapabilities{
		{Enabled: true, Models: many},
		{Enabled: true, Models: []string{strings.Repeat("x", protocol.LLMMaxModelNameLen+1)}},
		{Enabled: true, ServerKind: strings.Repeat("x", protocol.LLMMaxServerKindLen+1)},
		{Enabled: true, MaxConcurrent: -1},
		{Enabled: true, MaxConcurrent: protocol.LLMMaxConcurrent + 1},
	}
	for i, l := range bad {
		c := ok
		c.LocalLLM = &l
		if err := c.Validate(); err == nil {
			t.Errorf("case %d: oversized localLlm accepted", i)
		}
	}
}

func TestLLMRequestValidateAndAllowlist(t *testing.T) {
	good := protocol.LLMRequestPayload{RequestID: "r", Method: "POST", Path: protocol.LLMPathChatCompletions, TimeoutSec: 30}
	if err := good.Validate(); err != nil {
		t.Fatal(err)
	}
	for _, p := range []protocol.LLMRequestPayload{
		{RequestID: "", Method: "POST"},
		{RequestID: "r", Method: "DELETE"},
		{RequestID: "r", Method: "POST", Body: strings.Repeat("x", protocol.LLMMaxRequestBodyBytes+1)},
		{RequestID: "r", Method: "POST", TimeoutSec: protocol.LLMMaxTimeoutSec + 1},
	} {
		if p.Validate() == nil {
			t.Errorf("accepted %+v", p.Method)
		}
	}
	for _, c := range []struct {
		m, p string
		ok   bool
	}{
		{"POST", "/v1/chat/completions", true}, {"POST", "/v1/embeddings", true}, {"GET", "/v1/embeddings", false}, {"POST", "/v1/embeddings/", false}, {"GET", "/v1/models", true},
		{"GET", "/v1/chat/completions", false}, {"POST", "/v1/models", false},
		{"POST", "/v1/chat/completions/", false}, {"POST", "/v1/../etc", false},
		{"POST", "http://evil/v1/chat/completions", false}, {"POST", "/v1/chat/completions?x=1", false},
	} {
		if protocol.LLMPathAllowed(c.m, c.p) != c.ok {
			t.Errorf("LLMPathAllowed(%s %s) != %v", c.m, c.p, c.ok)
		}
	}
}

func TestLLMChunkValidate(t *testing.T) {
	if (protocol.LLMResponseChunkPayload{RequestID: "r", Data: strings.Repeat("a", protocol.LLMMaxChunkBytes+1)}).Validate() == nil {
		t.Error("oversized chunk accepted")
	}
	if (protocol.LLMResponseChunkPayload{RequestID: "r", Data: "\xff"}).Validate() == nil {
		t.Error("invalid UTF-8 accepted")
	}
}
