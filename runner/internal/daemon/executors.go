package daemon

import (
	"bytes"
	"context"
	"errors"
	"os"
	"os/exec"
	"sync"
	"sync/atomic"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/media"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/remotion"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
)

// Container posture. Every resource number now comes from the governor
// (runner.json's [limits] block, resolved against this host); what is left here
// is the timing and the byte cap, which are not resource limits a user sets.
const (
	// defaultContainerTTL is how long a workspace container may live without
	// being touched. It is a backstop for a container the session failed to
	// delete, not a job timeout (the gateway owns that).
	defaultContainerTTL = 2 * time.Hour
	// defaultExecTimeout bounds one command inside a workspace container.
	defaultExecTimeout = 10 * time.Minute
)

// sessions dispatches a job to the executor for its type.
//
// The agent takes ONE factory for the whole connection, and the two executors
// each refuse the other's job types with a JobError — so the dispatch table
// lives here rather than inside either of them.
//
// It also counts the jobs in flight, because the auto-update refuses to replace
// the binary while one is running (E9): the count is taken when a session is
// created and released when the agent closes it, which the library guarantees
// happens exactly once however the job ends.
type sessions struct {
	remotion agent.Factory
	media    agent.Factory
	act      *activity
	// observe, when set, receives the job notifications the desktop shell's
	// job list is built from (see jobevents.go). Nil is every headless run.
	observe JobObserver
	// failures, when set, receives a job this runner could not start (see
	// jobfailures.go). Nil is every headless run, and every run of a build
	// whose user has not turned E11's crash reporting on.
	failures JobFailureObserver
}

// New implements agent.Factory.
func (s sessions) New(ctx context.Context, job *agent.Job) (agent.Session, error) {
	switch job.JobType {
	case protocol.JobRemotionWorkspace:
		if s.remotion == nil {
			// The gateway should never route this to a runner that did not
			// advertise it; if it does, failing loudly beats half-running it.
			return nil, s.fail(job, &agent.JobError{
				Class:     protocol.FailRunner,
				Code:      protocol.ErrCodeContainerUnavailable,
				Message:   "this runner has no container runtime, so it cannot run " + job.JobType,
				Retryable: true,
			})
		}
		return s.count(s.remotion, ctx, job)
	case protocol.JobVideoCompile, protocol.JobVideoAnalyzeEx:
		if s.media == nil {
			return nil, s.fail(job, &agent.JobError{
				Class:   protocol.FailRunner,
				Code:    protocol.ErrCodeUnsupportedOp,
				Message: "this runner has no usable ffmpeg",
			})
		}
		return s.count(s.media, ctx, job)
	default:
		return nil, s.fail(job, &agent.JobError{
			Class:   protocol.FailInput,
			Code:    protocol.ErrCodeUnsupportedOp,
			Message: "this runner does not run " + job.JobType,
		})
	}
}

// fail records a job this runner could not start, and returns the error the
// caller must pass on.
//
// It exists so the reason has ONE definition: the error the agent library
// forwards to the gateway and the class and code the desktop shell's opt-in
// telemetry may report are read from the same JobError. A failure message is
// never handed to the observer: see JobFailure's own note, and the reason the
// two streams are separate.
func (s sessions) fail(job *agent.Job, err error) error {
	class, code := protocol.FailRunner, "job_setup_failed"
	var jobErr *agent.JobError
	if errors.As(err, &jobErr) {
		if jobErr.Class != "" {
			class = jobErr.Class
		}
		if jobErr.Code != "" {
			code = jobErr.Code
		}
	}
	s.refuse(job, class, code)
	return err
}

// count wraps a session so the in-flight count follows it.
func (s sessions) count(f agent.Factory, ctx context.Context, job *agent.Job) (agent.Session, error) {
	// The observer is installed on the job BEFORE the factory sees it, so a
	// session that reports progress immediately is already teed. Replacing the
	// sink is safe: the library sets it once, at assignment, and reads it only
	// through the job.
	var inner agent.ProgressSink
	if s.observe != nil {
		inner = job.Sink
		job.Sink = teeProgress{inner: inner, sess: s, job: job}
	}
	session, err := f.New(ctx, job)
	if err != nil {
		// A factory that refused the job never ran it, so the original sink is
		// put back rather than left wrapped: the observer must not later see
		// progress for a session that does not exist.
		job.Sink = inner
		return nil, s.fail(job, err)
	}
	// The count starts only once a session exists: a job that was refused never
	// entered the machine, so it must not hold an update back.
	s.act.begin()
	s.notify(jobEvent(JobStarted, job))
	return &countedSession{Session: session, act: s.act, sess: s, job: job}, nil
}

// activity is the number of jobs in flight, safe for concurrent use because
// jobs are dispatched from the agent's read loop and ended from their own
// goroutines.
type activity struct{ n atomic.Int64 }

func (a *activity) begin() {
	if a != nil {
		a.n.Add(1)
	}
}

func (a *activity) end() {
	if a != nil {
		a.n.Add(-1)
	}
}

func (a *activity) busy() bool { return a != nil && a.n.Load() > 0 }

// countedSession releases the in-flight count exactly once, when the agent
// closes the session. Close is documented as "always called, exactly once", and
// the once-guard makes that true even if a future caller is less careful.
//
// It is also how the finished event learns its outcome: the agent calls SetEnd
// immediately before Close (agent.EndAware), and Close reports it.
type countedSession struct {
	agent.Session
	act  *activity
	once sync.Once
	sess sessions
	job  *agent.Job

	endMu  sync.Mutex
	end    agent.JobEnd
	endSet bool
}

// SetEnd implements agent.EndAware, and passes the end on to an executor's
// session that wants it too.
func (s *countedSession) SetEnd(end agent.JobEnd) {
	s.endMu.Lock()
	s.end, s.endSet = end, true
	s.endMu.Unlock()
	if inner, ok := s.Session.(agent.EndAware); ok {
		inner.SetEnd(end)
	}
}

// Close implements agent.Session.
func (s *countedSession) Close(ctx context.Context) {
	s.once.Do(func() {
		s.act.end()
		s.Session.Close(ctx)
		if s.job != nil {
			ev := jobEvent(JobFinished, s.job)
			s.endMu.Lock()
			end, known := s.end, s.endSet
			s.endMu.Unlock()
			if !known {
				// An agent that predates EndAware, or a caller that closed the
				// session without one: the runner let go of the job and
				// cannot say more.
				end = agent.JobEnd{}
			}
			ev.Outcome, ev.OutcomeDetail = outcomeOf(end)
			s.sess.notify(ev)
		}
	})
}

// workspaceConfig is the container-driver configuration for Remotion sessions.
type workspaceConfig struct {
	Image   string
	Root    string
	Slots   int
	Network string
	// Limits are the resolved per-job caps. They are required: a workspace
	// container is never created without a memory and CPU cap, because an empty
	// --memory argument is rejected by the runtime and an uncapped render is
	// what this WP exists to stop.
	Limits governor.Limits
	// Cache is the downloaded-media cache stages go through. Nil means none.
	Cache agent.SourceCache
}

// newRemotionExecutor builds the workspace executor. It is built only when a
// container runtime was verified (runtime.Probe ran the pinned image under the
// full flag set and confirmed there is no route out), so a session that exists
// at all can start a container.
func newRemotionExecutor(cfg workspaceConfig, c runtime.Candidate) (*remotion.Executor, error) {
	if cfg.Image == "" {
		return nil, errors.New("daemon: a runtime image is required for the workspace executor")
	}
	if cfg.Root == "" {
		return nil, errors.New("daemon: a workspace root is required for the workspace executor")
	}
	if cfg.Limits.MemoryBytes <= 0 || cfg.Limits.PIDs <= 0 {
		return nil, errors.New("daemon: the workspace executor needs resolved resource limits")
	}
	max := cfg.Slots
	if max < 1 {
		max = 1
	}
	driver := container.NewWithRunner(container.Config{
		SandboxImage:   cfg.Image,
		SandboxRoot:    cfg.Root,
		SandboxNetwork: cfg.Network,
		// Egress stays off. The sandbox network is named "none" by default, and
		// the flag the driver passes to `docker run` is the network name, so a
		// container started here has no route anywhere — not to the internet and
		// not to the host's published ports (Postgres, RabbitMQ, the gateway).
		NetworkEgress:  false,
		MaxSandboxes:   max,
		MaxRenderBytes: DefaultMaxOutputBytes,
		SandboxTTL:     defaultContainerTTL,
		ExecTimeout:    defaultExecTimeout,
		MemoryLimit:    cfg.Limits.MemoryArg(),
		CPULimit:       cfg.Limits.CPUArg(),
		PIDsLimit:      cfg.Limits.PIDs,
	}, c.Binary, containerRunner(c, cfg.Limits))

	return remotion.New(remotion.Config{Driver: driver, QuotaBytes: cfg.Limits.DiskQuotaBytes, SourceCache: cfg.Cache})
}

// newMediaExecutor builds the ffmpeg executor. With a verified container
// runtime, ffmpeg runs inside the runtime image (the specification's choice for
// a runner that has one); without one it runs as a host process, which is what
// makes an ffmpeg-only runner useful at all.
//
// The media package's container runner sets no resource flags of its own, and
// its host runner sets neither a thread count nor a priority, so BOTH paths are
// wrapped here: the governor's limits are applied at the daemon's own CLI and
// CommandRunner seams rather than left to a library that has no field for them.
func newMediaExecutor(image string, c *runtime.Candidate, limits governor.Limits, cfg media.Config) (*media.Executor, error) {
	cfg.QuotaBytes = limits.DiskQuotaBytes
	if c != nil && image != "" {
		cfg.Container = &media.ContainerRunner{
			Binary: c.Binary,
			Image:  image,
			// "none" is the runtime's own keyword for a container with no
			// network at all, which is what a compile step needs.
			Network: "none",
			CLI:     mediaCLI(*c, limits),
		}
		return media.New(cfg)
	}
	cfg.Runner = hostRunner{limits: limits}
	return media.New(cfg)
}

// containerRunner runs the container CLI with the candidate's host environment,
// so a Docker Desktop socket, a rootless socket or a Podman machine is reached
// exactly the way E3's probe reached it. container.CLIRunner cannot do this: it
// executes the binary with the daemon's own environment.
//
// It also adds the governor's caps to the argument vector. The workspace driver
// already sets memory, CPU and pids from the same configuration, so those are
// left alone; the CPU-shares weight is the one it has no field for.
func containerRunner(c runtime.Candidate, limits governor.Limits) container.Runner {
	return func(ctx context.Context, args ...string) ([]byte, error) {
		return execCLI(ctx, c, limits.WithContainerResourceFlags(args)...)
	}
}

// mediaCLI adapts the same invocation to the media package's CLI shape, which
// keeps stdout and stderr apart (a composition list is JSON on stdout while the
// build log goes to stderr).
// The media package's CLI contract passes args[0] as the binary name, matching
// its own default CLI; the binary is therefore read from args[0] rather than
// shadowed by the candidate's (the two are the same binary, and the candidate's
// environment is what this wrapper adds).
func mediaCLI(c runtime.Candidate, limits governor.Limits) media.CLI {
	return func(ctx context.Context, args ...string) ([]byte, []byte, error) {
		if len(args) == 0 {
			return nil, nil, errors.New("daemon: empty container command")
		}
		withCaps := limits.WithContainerResourceFlags(args[1:])
		cmd := exec.CommandContext(ctx, args[0], withCaps...)
		cmd.Env = candidateEnv(c)
		var stdout, stderr bytes.Buffer
		cmd.Stdout, cmd.Stderr = &stdout, &stderr
		err := cmd.Run()
		return stdout.Bytes(), stderr.Bytes(), err
	}
}

// execCLI runs the container CLI and returns its combined output, which is what
// container.Runner is defined to produce.
func execCLI(ctx context.Context, c runtime.Candidate, args ...string) ([]byte, error) {
	cmd := exec.CommandContext(ctx, c.Binary, args...)
	cmd.Env = candidateEnv(c)
	return cmd.CombinedOutput()
}

// candidateEnv adds the candidate's endpoint (DOCKER_HOST / CONTAINER_HOST) to
// the daemon's environment.
func candidateEnv(c runtime.Candidate) []string {
	env := os.Environ()
	if c.Host == "" {
		return env
	}
	if c.Kind == runtime.KindPodman {
		return append(env, "CONTAINER_HOST="+c.Host)
	}
	return append(env, "DOCKER_HOST="+c.Host)
}
