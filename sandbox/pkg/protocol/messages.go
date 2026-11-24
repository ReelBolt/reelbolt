package protocol

import (
	"encoding/json"
	"fmt"
	"strings"
)

// JobLease is one (jobId, leaseEpoch) pair: a job a runner still holds
// (hello.running, heartbeat.running) or one the gateway kept for it
// (welcome.resumed).
type JobLease struct {
	JobID      string `json:"jobId"`
	LeaseEpoch int64  `json:"leaseEpoch"`
}

// LeaseStatus is the gateway's answer for one held job in heartbeat.leases.
// ok=false means the gateway no longer honours the lease and the runner MUST
// stop that job.
type LeaseStatus struct {
	JobID string `json:"jobId"`
	OK    bool   `json:"ok"`
}

// HelloAuth is hello.auth: the union of the two authentication shapes, told
// apart by Kind.
//
// It carries a secret on the pool path, so it redacts itself in logs (see
// LogValue) — "Credentials, device signatures and job tokens MUST NOT be
// logged". The device path's Sig is redacted for the same reason.
type HelloAuth struct {
	// Kind is AuthKindDevice or AuthKindPool.
	Kind string `json:"kind"`

	// Desktop device shape.
	DeviceID string `json:"deviceId,omitempty"`
	TS       string `json:"ts,omitempty"`
	Nonce    string `json:"nonce,omitempty"`
	Sig      string `json:"sig,omitempty"`

	// Cloud pool worker shape.
	Credential string `json:"credential,omitempty"`
	WorkerName string `json:"workerName,omitempty"`
}

// LogValue redacts every secret in the auth block so a connection's auth can be
// logged by shape without leaking a credential.
func (a HelloAuth) LogValue() any {
	return map[string]any{
		"kind":       a.Kind,
		"deviceId":   a.DeviceID,
		"workerName": a.WorkerName,
		"credential": redacted(a.Credential),
		"sig":        redacted(a.Sig),
		"nonce":      a.Nonce, // not a secret: it is single-use and bound to the signature
	}
}

func redacted(s string) string {
	if s == "" {
		return ""
	}
	return "[redacted]"
}

// Capabilities is hello.capabilities: what a runner can do. The gateway matches
// jobs on it (D5b) and stores it as runner_devices.capabilities_json.
type Capabilities struct {
	ProtocolVersion    string   `json:"protocolVersion"`
	AgentVersion       string   `json:"agentVersion"`
	OS                 string   `json:"os"`
	Arch               string   `json:"arch"`
	Slots              int      `json:"slots"`
	Container          string   `json:"container"` // none | docker | podman
	ContainerMode      string   `json:"containerMode,omitempty"`
	RuntimeImageDigest *string  `json:"runtimeImageDigest"` // null when Container is none
	FfmpegVersion      string   `json:"ffmpegVersion"`
	FontsetVersion     string   `json:"fontsetVersion"`
	JobTypes           []string `json:"jobTypes"`
	MaxOutputBytes     int64    `json:"maxOutputBytes"`
	// LocalLLM is optional: present only when the user shares a local model.
	LocalLLM *LocalLLMCapabilities `json:"localLlm,omitempty"`
}

// Supports reports whether the runner advertised a job type.
func (c Capabilities) Supports(jobType string) bool {
	for _, jt := range c.JobTypes {
		if jt == jobType {
			return true
		}
	}
	return false
}

// Validate applies the rules the specification states for capabilities.
//
// It deliberately checks only what structural correctness and routing depend on
// — a supported protocol version, at least one slot, a known container runtime,
// the container/digest/jobType consistency rule and a non-negative output cap.
// The descriptive strings (agentVersion, os, arch, ffmpegVersion,
// fontsetVersion) are not required to be non-empty: a runner that cannot probe
// one of them is still allowed to connect and simply receives no matching work,
// which is better than a malformed-hello close. Unknown job types are allowed
// too (enum-like values are open sets, so an unrecognised one is an unknown
// case, never a parse error).
func (c Capabilities) Validate() error {
	if _, ok := NegotiateVersion(c.ProtocolVersion); !ok {
		return fmt.Errorf("capabilities.protocolVersion %q is not supported", c.ProtocolVersion)
	}
	if c.Slots < 1 {
		return fmt.Errorf("capabilities.slots must be at least 1, got %d", c.Slots)
	}
	switch c.Container {
	case ContainerNone, ContainerDocker, ContainerPodman:
	default:
		return fmt.Errorf("capabilities.container %q is not one of none, docker, podman", c.Container)
	}
	if c.Container == ContainerNone {
		if c.RuntimeImageDigest != nil {
			return fmt.Errorf("capabilities.runtimeImageDigest must be null when container is none")
		}
		if c.Supports(JobRemotionWorkspace) {
			return fmt.Errorf("capabilities.jobTypes must not include %s without a container runtime", JobRemotionWorkspace)
		}
	}
	if c.MaxOutputBytes < 0 {
		return fmt.Errorf("capabilities.maxOutputBytes must not be negative")
	}
	if c.LocalLLM != nil {
		if err := c.LocalLLM.Validate(); err != nil {
			return err
		}
	}
	return nil
}

// HelloPayload is the runner's first message. Running lists the jobs the runner
// believes it still holds after a reconnect; it is optional.
type HelloPayload struct {
	ProtocolVersion string       `json:"protocolVersion"`
	Auth            HelloAuth    `json:"auth"`
	Capabilities    Capabilities `json:"capabilities"`
	Running         []JobLease   `json:"running,omitempty"`
}

// DeviceAuth reports the device-shaped auth block and whether it is one.
func (h HelloPayload) DeviceAuth() (HelloAuth, bool) {
	return h.Auth, h.Auth.Kind == AuthKindDevice
}

// PoolAuth reports the pool-shaped auth block and whether it is one.
func (h HelloPayload) PoolAuth() (HelloAuth, bool) {
	return h.Auth, h.Auth.Kind == AuthKindPool
}

// WelcomePayload accepts the connection and returns the negotiated limits plus
// the runner's resolved identity.
type WelcomePayload struct {
	RunnerID        string     `json:"runnerId"`
	Kind            string     `json:"kind"`  // Desktop | CloudPool
	OrgID           *string    `json:"orgId"` // null for the pool
	ProtocolVersion string     `json:"protocolVersion"`
	HeartbeatSec    int        `json:"heartbeatSec"`
	DeadAfterSec    int        `json:"deadAfterSec"`
	MaxInlineBytes  int        `json:"maxInlineBytes"`
	GatewayInstance string     `json:"gatewayInstance"`
	ServerTime      string     `json:"serverTime"`
	Resumed         []JobLease `json:"resumed"`
	// MinAgentVersion is optional: a runner older than it should tell its user to
	// update and must not take jobs.
	MinAgentVersion string `json:"minAgentVersion,omitempty"`
}

// HeartbeatPayload is the union of the two heartbeat shapes, told apart by
// direction. From a runner: FreeSlots and Running. From the gateway: ServerTime
// and Leases.
//
// FreeSlots is a pointer because 0 free slots is a meaningful, common state and
// must not be confused with "absent". MarshalJSON writes the shape the populated
// fields imply, so each direction's required array is present even when empty.
type HeartbeatPayload struct {
	FreeSlots  *int          `json:"freeSlots,omitempty"`
	Running    []JobLease    `json:"running,omitempty"`
	ServerTime string        `json:"serverTime,omitempty"`
	Leases     []LeaseStatus `json:"leases,omitempty"`
}

// MarshalJSON writes a heartbeat that carries serverTime or leases as the
// gateway's shape and anything else as the runner's. Both shapes carry their
// array as a required field ("running" from a runner, "leases" from the
// gateway), so an empty one is written as [] rather than dropped — which plain
// omitempty cannot express, and a receiver that cannot tell "no jobs" from "no
// field" would be guessing.
func (h HeartbeatPayload) MarshalJSON() ([]byte, error) {
	if h.ServerTime != "" || h.Leases != nil {
		leases := h.Leases
		if leases == nil {
			leases = []LeaseStatus{}
		}
		return json.Marshal(struct {
			ServerTime string        `json:"serverTime"`
			Leases     []LeaseStatus `json:"leases"`
		}{h.ServerTime, leases})
	}
	running := h.Running
	if running == nil {
		running = []JobLease{}
	}
	return json.Marshal(struct {
		FreeSlots *int       `json:"freeSlots"`
		Running   []JobLease `json:"running"`
	}{h.FreeSlots, running})
}

// ReadyPayload declares free slots; the gateway assigns work only against them.
type ReadyPayload struct {
	FreeSlots int `json:"freeSlots"`
}

// AssignLimits are the caps a runner must honour for one job.
type AssignLimits struct {
	MaxOutputBytes int64 `json:"maxOutputBytes"`
	IdleTimeoutSec int   `json:"idleTimeoutSec"`
}

// AssignRequires are the pins a runner must meet before it may accept a job.
// Zero values mean "not required by this job".
type AssignRequires struct {
	RuntimeImageDigest string `json:"runtimeImageDigest,omitempty"`
	Container          string `json:"container,omitempty"`
	FontsetVersion     string `json:"fontsetVersion,omitempty"`
	FfmpegMajor        int    `json:"ffmpegMajor,omitempty"`
}

// AssignPayload offers a job and carries the per-job token. Spec stays raw
// because its shape depends on JobType — see SpecInto and the three spec structs.
type AssignPayload struct {
	JobType    string          `json:"jobType"`
	LeaseEpoch int64           `json:"leaseEpoch"`
	JobToken   string          `json:"jobToken"`
	Attempt    int             `json:"attempt"`
	TimeoutSec int             `json:"timeoutSec"`
	OrgID      string          `json:"orgId"`
	Limits     AssignLimits    `json:"limits"`
	Requires   AssignRequires  `json:"requires"`
	Spec       json.RawMessage `json:"spec"`
	// Display is optional and for people only: the names a runner's own UI
	// shows next to the job. It is absent when the gateway could not read them
	// in time, and an older gateway never sends it.
	Display *AssignDisplay `json:"display,omitempty"`
}

// AssignDisplay is assign.display: the human names of what a job belongs to,
// so a desktop runner can say "Launch video · Promo cut" instead of a job id.
//
// It is DISPLAY-ONLY. Nothing on either side may authorize, route, scope or
// bill by it: the gateway reads it best effort after the lease is already
// granted, a runner cannot verify it, and the ids in it are the ones the
// dashboard links by, not credentials. Every field is optional, and an
// unrecognised field a later gateway adds is ignored like any other.
type AssignDisplay struct {
	// WorkspaceName is the organization's name.
	WorkspaceName string `json:"workspaceName,omitempty"`
	ProjectName   string `json:"projectName,omitempty"`
	WorkflowName  string `json:"workflowName,omitempty"`
	// ProjectID, WorkflowID and ExecutionID let a runner build a link into the
	// dashboard; opening it still requires the person's own session.
	ProjectID   string `json:"projectId,omitempty"`
	WorkflowID  string `json:"workflowId,omitempty"`
	ExecutionID string `json:"executionId,omitempty"`
}

// Empty reports whether the display carries nothing at all.
func (d *AssignDisplay) Empty() bool {
	return d == nil || *d == AssignDisplay{}
}

// SpecInto decodes assign.spec into v.
func (p AssignPayload) SpecInto(v any) error {
	if err := json.Unmarshal(p.Spec, v); err != nil {
		return fmt.Errorf("assign.spec for %s: %w", p.JobType, err)
	}
	return nil
}

// RemotionWorkspaceSpec is assign.spec for remotion.workspace-session.
type RemotionWorkspaceSpec struct {
	ExecutionID string           `json:"executionId"`
	Template    RemotionTemplate `json:"template"`
}

// RemotionTemplate names the Remotion template the runner copies into its
// workspace.
type RemotionTemplate struct {
	Name    string `json:"name"`
	Version string `json:"version"`
}

// VideoCompileSpec is assign.spec for video.compile.
type VideoCompileSpec struct {
	ExecutionID string `json:"executionId"`
	StepID      string `json:"stepId"`
	Mode        string `json:"mode"` // Reencode | StreamCopy
}

// VideoAnalyzeExtractSpec is assign.spec for video.analyze.extract.
type VideoAnalyzeExtractSpec struct {
	ExecutionID string `json:"executionId"`
	StepID      string `json:"stepId"`
}

// AcceptPayload takes the job.
type AcceptPayload struct {
	LeaseEpoch int64  `json:"leaseEpoch"`
	JobToken   string `json:"jobToken"`
}

// RejectPayload declines the job. The job returns to Queued without consuming an
// attempt. Message is optional and for the user's benefit only.
type RejectPayload struct {
	LeaseEpoch int64  `json:"leaseEpoch"`
	JobToken   string `json:"jobToken"`
	Reason     string `json:"reason"`
	Message    string `json:"message,omitempty"`
}

// ProgressPayload is non-authoritative progress: nothing in it changes what the
// customer is charged.
type ProgressPayload struct {
	LeaseEpoch int64  `json:"leaseEpoch"`
	JobToken   string `json:"jobToken"`
	OpID       string `json:"opId,omitempty"`
	Stage      string `json:"stage"`
	// Percent is 0..100 and optional; a pointer so 0 survives the JSON round trip.
	Percent *int   `json:"percent,omitempty"`
	Detail  string `json:"detail,omitempty"`
}

// OpRequestPayload is one operation for a session. Args stays raw because its
// shape depends on Op; use ArgsInto.
type OpRequestPayload struct {
	OpID       string          `json:"opId"`
	Op         string          `json:"op"`
	Args       json.RawMessage `json:"args"`
	TimeoutSec int             `json:"timeoutSec"`
}

// ArgsInto decodes op.request.args into v.
func (p OpRequestPayload) ArgsInto(v any) error {
	if len(p.Args) == 0 {
		return nil
	}
	if err := json.Unmarshal(p.Args, v); err != nil {
		return fmt.Errorf("op.request.args for %s: %w", p.Op, err)
	}
	return nil
}

// OpResponsePayload is the result of one op. Exactly one of Result and Error is
// set: Ok true carries Result, Ok false carries Error.
type OpResponsePayload struct {
	LeaseEpoch int64           `json:"leaseEpoch"`
	JobToken   string          `json:"jobToken"`
	OpID       string          `json:"opId"`
	OK         bool            `json:"ok"`
	Result     json.RawMessage `json:"result,omitempty"`
	Error      *OpError        `json:"error,omitempty"`
}

// OpError is a failed op. Code is one of the ErrCode* constants; an unrecognised
// code is an unknown case, never a parse error.
type OpError struct {
	Code      string `json:"code"`
	Message   string `json:"message"`
	Retryable bool   `json:"retryable"`
}

// CompletePayload ends a job: outcome succeeded, or an acknowledged
// cancellation. Result and Metrics are informational only — billing is priced
// from the platform-side job spec, never from anything the runner reports.
type CompletePayload struct {
	LeaseEpoch int64           `json:"leaseEpoch"`
	JobToken   string          `json:"jobToken"`
	Outcome    string          `json:"outcome"` // succeeded | cancelled
	Result     json.RawMessage `json:"result,omitempty"`
	Metrics    json.RawMessage `json:"metrics,omitempty"`
}

// FailPayload ends a job as a failure. Class is the runner's claim; the class
// that counts for billing is the one the platform derives itself.
type FailPayload struct {
	LeaseEpoch int64  `json:"leaseEpoch"`
	JobToken   string `json:"jobToken"`
	Class      string `json:"class"` // platform | runner | input
	Code       string `json:"code"`
	Message    string `json:"message"`
	Retryable  bool   `json:"retryable"`
}

// CancelPayload tells a runner to stop a job now.
type CancelPayload struct {
	Reason   string `json:"reason"`
	GraceSec int    `json:"graceSec"`
}

// DrainPayload tells a runner to take no new work (or announces that it will
// not). DeadlineSec is optional.
type DrainPayload struct {
	Reason      string `json:"reason"`
	DeadlineSec int    `json:"deadlineSec,omitempty"`
}

// ByePayload is the orderly goodbye sent immediately before closing. Reason is
// an open string; the gateway's values are the cancel reasons plus "revoked"
// and "shutdown".
type ByePayload struct {
	Reason string `json:"reason"`
}

// JobTokenClaims is the claim set of the per-job HS256 token minted at assign
// and echoed on every job-scoped runner message. Minting and verification arrive
// with the lease (D5b); the shape lives here so the gateway and the worker
// cannot drift on it.
type JobTokenClaims struct {
	JobID     string `json:"jobId"`
	SessionID string `json:"sessionId,omitempty"`
	Epoch     int64  `json:"epoch"`
	OrgID     string `json:"orgId"`
	// Exp is the Unix expiry: no later than the job timeout plus 5 minutes.
	Exp int64 `json:"exp"`
}

// String is the log-safe rendering of a message type plus its scope, used to tag
// connection logs without echoing a payload.
func (t MessageType) String() string { return string(t) }

// ValidateHello checks a hello for the structural rules the gateway applies
// before it looks at credentials or the database: a supported protocol version,
// a known auth shape and valid capabilities.
func (p HelloPayload) Validate() error {
	if _, ok := NegotiateVersion(p.ProtocolVersion); !ok {
		return fmt.Errorf("protocolVersion %q is not supported", p.ProtocolVersion)
	}
	switch p.Auth.Kind {
	case AuthKindDevice:
		if p.Auth.DeviceID == "" || p.Auth.TS == "" || p.Auth.Nonce == "" || p.Auth.Sig == "" {
			return fmt.Errorf("device auth requires deviceId, ts, nonce and sig")
		}
	case AuthKindPool:
		if p.Auth.Credential == "" || p.Auth.WorkerName == "" {
			return fmt.Errorf("pool auth requires credential and workerName")
		}
	default:
		return fmt.Errorf("auth.kind %q is not one of device, pool", p.Auth.Kind)
	}
	return p.Capabilities.Validate()
}

// NormalizeGatewayHost puts a dialled host into the canonical form the hello
// signature binds: lower case, no port, no trailing dot. Both ends compute it
// from the URL they dialled (the runner) or were reached at (the gateway), so a
// mismatch here would reject every honest connection.
func NormalizeGatewayHost(host string) string {
	host = strings.TrimSpace(strings.ToLower(host))
	if i := strings.LastIndex(host, ":"); i >= 0 && !strings.Contains(host[i:], "]") {
		host = host[:i]
	}
	return strings.TrimSuffix(host, ".")
}
