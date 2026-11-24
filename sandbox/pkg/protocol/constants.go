package protocol

// WebSocket close codes used by this protocol ("Transport and envelope").
//
// 4400-4429 are the protocol's own codes, in the range RFC 6455 reserves for
// application use. A receiver MUST NOT retry on 4400/4401/4403/4409: those all
// mean "this identity may not be here", and retrying is what the nonce cache
// and the revocation check exist to stop.
type CloseCode int

const (
	CloseNormal             CloseCode = 1000 // normal closure
	CloseGoingAway          CloseCode = 1001 // gateway going away (shutdown, deploy)
	CloseMessageTooLarge    CloseCode = 1009 // frame past the read limit
	CloseUnsupportedVersion CloseCode = 4400 // unsupported protocol version or malformed hello
	CloseUnauthenticated    CloseCode = 4401 // unauthenticated, revoked or replayed
	CloseForbidden          CloseCode = 4403 // organisation disabled
	CloseHeartbeatTimeout   CloseCode = 4408 // nothing heard for 45 s
	CloseSuperseded         CloseCode = 4409 // a newer connection from the same device
	CloseRateLimited        CloseCode = 4429 // rate limited

	// CloseInternal is not in the specification's table. It is the standard
	// WebSocket code for an unexpected gateway-side failure — the device registry
	// being unreachable, say — which is not an authentication decision and is
	// worth a retry. The specification's 4401 would tell the runner its identity
	// is bad, which would be a lie.
	CloseInternal CloseCode = 1011
)

// Timing constants from "Connection handshake and authentication", "Job
// lifecycle, leases and fencing" and "Versioning and compatibility". They are
// here rather than in the gateway because the worker library and the desktop app
// need the same numbers.
const (
	// HeartbeatSec is the interval at which a runner sends heartbeat.
	HeartbeatSec = 15
	// DeadAfterSec is how long a connection may be silent before the gateway
	// closes it with 4408. Messages other than heartbeat also count as liveness.
	DeadAfterSec = 45
	// HelloTimeoutSec is how long a runner has to send hello after the upgrade.
	HelloTimeoutSec = 10
	// HelloSkewSec is the accepted |now - hello.auth.ts| window.
	HelloSkewSec = 120
	// NonceTTLSeconds is how long the gateway remembers a (deviceId, nonce) pair.
	// The specification requires at least 240 s.
	NonceTTLSeconds = 240
	// RevocationRecheckSec is how often the gateway re-reads revoked_at for open
	// desktop connections. The specification requires at least every 60 s.
	RevocationRecheckSec = 60
	// LeaseSec is how long an assignment is valid before the reaper takes it
	// back. Renewed by every heartbeat that lists the job. D5b owns the lease.
	LeaseSec = 45
	// AcceptTimeoutSec is how long a runner has to accept or reject an
	// assignment before the gateway treats the silence as a reject. D5b.
	AcceptTimeoutSec = 10
	// CancelGraceSec is the window a runner has to stop a cancelled job. D5b.
	CancelGraceSec = 10
	// MaxAttempts is the default requeue budget of a one-shot job. D5b.
	MaxAttempts = 2
	// OpResultCacheSize is how many op results a worker keeps per session, so a
	// repeated opId after a reconnect returns the cached response rather than
	// re-running the op. D4b.
	OpResultCacheSize = 256
)

// Hello auth kinds.
const (
	AuthKindDevice = "device"
	AuthKindPool   = "pool"
)

// Runner kinds, as they appear in welcome.kind and runner_devices.kind.
const (
	KindDesktop   = "Desktop"
	KindCloudPool = "CloudPool"
)

// Container runtimes a runner may advertise in hello.capabilities.container.
const (
	ContainerNone   = "none"
	ContainerDocker = "docker"
	ContainerPodman = "podman"
)

// Job types. All three are sessions in v1.
const (
	JobRemotionWorkspace = "remotion.workspace-session"
	JobVideoCompile      = "video.compile"
	JobVideoAnalyzeEx    = "video.analyze.extract"
)

// JobTypes returns the three v1 job types.
func JobTypes() []string {
	return []string{JobRemotionWorkspace, JobVideoCompile, JobVideoAnalyzeEx}
}

// JobTypesWithoutContainer are the job types a runner with container "none" may
// advertise. remotion.workspace-session requires a verified container runtime.
func JobTypesWithoutContainer() []string {
	return []string{JobVideoCompile, JobVideoAnalyzeEx}
}

// VideoCompileSpec.Mode values.
const (
	CompileModeReencode   = "Reencode"
	CompileModeStreamCopy = "StreamCopy"
)

// complete.outcome values.
const (
	OutcomeSucceeded = "succeeded"
	OutcomeCancelled = "cancelled"
)

// reject.reason values.
const (
	RejectBusy               = "busy"
	RejectCapabilityMismatch = "capability_mismatch"
	RejectShuttingDown       = "shutting_down"
	RejectPausedByUser       = "paused_by_user"
	RejectNoContainerRuntime = "no_container_runtime"
)

// cancel.reason values.
const (
	CancelUser           = "user"
	CancelExecutionEnded = "execution_ended"
	CancelLeaseLost      = "lease_lost"
	CancelRevoked        = "revoked"
	CancelShutdown       = "shutdown"
)

// fail.class values. The class decides whether credits are refunded and whether
// the engine retries; a class a runner claims is recorded as a claim only.
const (
	FailPlatform = "platform"
	FailRunner   = "runner"
	FailInput    = "input"
)

// Op names. The first group is valid only in a remotion.workspace-session, the
// second only in a media session (video.compile, video.analyze.extract).
const (
	OpWriteFile  = "writeFile"
	OpReadFile   = "readFile"
	OpListFiles  = "listFiles"
	OpDeletePath = "deletePath"
	OpExec       = "exec"
	OpStatus     = "status"
	OpStage      = "stage"
	OpPublish    = "publish"
	OpRender     = "render"
	OpHydrate    = "hydrate"
	OpClose      = "close"

	OpFfmpeg  = "ffmpeg"
	OpFfprobe = "ffprobe"
	OpRead    = "read"
	OpStat    = "stat"
)

// WorkspaceOps and MediaOps list the ops of each session kind.
func WorkspaceOps() []string {
	return []string{
		OpWriteFile, OpReadFile, OpListFiles, OpDeletePath, OpExec, OpStatus,
		OpStage, OpPublish, OpRender, OpHydrate, OpClose,
	}
}

// MediaOps lists the ops valid in video.compile and video.analyze.extract.
func MediaOps() []string {
	return []string{OpStage, OpFfmpeg, OpFfprobe, OpRead, OpPublish, OpStat, OpClose}
}

// op.response.error.code values from "Error and failure classes".
const (
	ErrCodeInvalidPath          = "invalid_path"
	ErrCodeArgvNotAllowed       = "argv_not_allowed"
	ErrCodeExecNotAllowed       = "exec_not_allowed"
	ErrCodeFileTooLarge         = "file_too_large"
	ErrCodeOutputTooLarge       = "output_too_large"
	ErrCodeUnsupportedOp        = "unsupported_op"
	ErrCodeOpExpired            = "op_expired"
	ErrCodeOpInFlight           = "op_in_flight"
	ErrCodeStageFailed          = "stage_failed"
	ErrCodePublishFailed        = "publish_failed"
	ErrCodeTimeout              = "timeout"
	ErrCodeContainerUnavailable = "container_unavailable"
	ErrCodeDiskFull             = "disk_full"
	ErrCodeCancelled            = "cancelled"
)

// ExecAllowlistScripts and ExecAllowlistRemotionSubcommands are the exec
// allowlist from "Remotion workspace ops". The worker re-validates against it;
// it is input hygiene, never isolation.
var (
	ExecAllowlistScripts             = []string{"build", "render", "typecheck", "compositions"}
	ExecAllowlistRemotionSubcommands = []string{"render", "still", "compositions"}
)
