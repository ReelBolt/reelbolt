// Package remotion executes a remotion.workspace-session: the ops the engine
// sends while an agent writes a composition, builds it, reads the compiler's
// errors and renders the result.
//
// The workspace is the container driver's (sandbox/pkg/container): one
// no-egress container per session with a read-only root, all capabilities
// dropped and the customer's project bind-mounted into it. This package adds
// the protocol's op surface on top and nothing else — in particular it never
// relaxes the driver's exec allowlist, which is the input hygiene the
// specification describes and not the isolation.
//
// The workspace is model-authored code and is treated as hostile: every path an
// op carries is validated here, every file operation goes through an os.Root,
// and no op can name a file outside the workspace.
package remotion

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// maxInlineOutput caps the stdout and stderr one exec op returns inline. The
// protocol's payload limit is 4 MiB and a build log can be minutes long, so the
// tail is what is kept for an error stream.
const maxInlineOutput = 64 << 10

// Driver is the container-driver surface this executor uses. *container.Driver
// implements it; tests substitute a fake so an op surface can be exercised
// without a container runtime.
type Driver interface {
	Create(workflowExecutionID string) (*container.Sandbox, bool, error)
	GetByExecution(workflowExecutionID string) (*container.Sandbox, error)
	DeleteByExecution(workflowExecutionID string) error
	StopByExecution(workflowExecutionID string) error
	RunExecResult(workflowExecutionID string, req container.ExecRequest) (container.ExecResult, error)
	Touch(workflowExecutionID string)
	Config() container.Config
}

// Config is the executor's configuration.
type Config struct {
	// Driver is the container driver. Required.
	Driver Driver
	// QuotaBytes caps one session's workspace. Zero means 8 GiB.
	QuotaBytes int64
	// HTTPClient performs the presigned transfers. Nil means the agent's
	// default, which does not follow redirects.
	HTTPClient *http.Client
	// SourceCache keeps staged source media between jobs, so a second stage
	// of the same object is revalidated instead of downloaded (see
	// agent.FetchToFSCached). Nil means no cache: every stage downloads, which
	// is exactly the behaviour before the cache existed.
	SourceCache agent.SourceCache
	// AllCores, when 2 or more, raises a render's "--concurrency=N" to that many frames in
	// parallel (see agent.RaiseRemotionConcurrency). Zero leaves the command as sent.
	AllCores int
}

// Executor builds one session per job from the same configuration.
type Executor struct {
	cfg Config
}

// New builds an executor around a container driver.
func New(cfg Config) (*Executor, error) {
	if cfg.Driver == nil {
		return nil, errors.New("remotion: a container driver is required")
	}
	if cfg.HTTPClient == nil {
		cfg.HTTPClient = agent.DefaultHTTPClient()
	}
	if cfg.QuotaBytes <= 0 {
		cfg.QuotaBytes = 8 << 30
	}
	return &Executor{cfg: cfg}, nil
}

// New implements agent.Factory. It creates the workspace container, so a
// runner whose runtime is missing fails the job with container_unavailable
// rather than accepting work it cannot do.
func (e *Executor) New(ctx context.Context, job *agent.Job) (agent.Session, error) {
	if job.JobType != protocol.JobRemotionWorkspace {
		return nil, &agent.JobError{
			Class:   protocol.FailInput,
			Code:    protocol.ErrCodeUnsupportedOp,
			Message: "this executor runs only " + protocol.JobRemotionWorkspace,
		}
	}
	scope := agent.NewScopeID()
	// The driver's config is the runner's: a session must never widen it, so
	// the executor passes nothing and lets the driver's own settings govern.
	_ = ctx
	sb, _, err := e.cfg.Driver.Create(scope)
	if err != nil {
		return nil, &agent.JobError{
			Class:     protocol.FailRunner,
			Code:      protocol.ErrCodeContainerUnavailable,
			Message:   "the workspace container could not be started: " + err.Error(),
			Retryable: true,
		}
	}
	fs, err := agent.OpenRootFS(sb.WorkspacePath, e.cfg.QuotaBytes)
	if err != nil {
		_ = e.cfg.Driver.DeleteByExecution(scope)
		return nil, &agent.JobError{
			Class:     protocol.FailRunner,
			Code:      protocol.ErrCodeContainerUnavailable,
			Message:   "the workspace could not be opened: " + err.Error(),
			Retryable: true,
		}
	}
	s := &session{exec: e, job: job, scope: scope, fs: fs}
	if err := job.SpecInto(&s.spec); err != nil {
		// The spec is informational for this executor (the runtime image carries
		// the template), so a malformed one is logged through the status op
		// rather than failing the session.
		s.specErr = err
	}
	return s, nil
}

// session is one job's workspace.
type session struct {
	exec    *Executor
	job     *agent.Job
	scope   string
	fs      *agent.RootFS
	spec    protocol.RemotionWorkspaceSpec
	specErr error
	closed  bool
}

// Close destroys the workspace and its container. It runs however the job ends,
// including a cancel, so a customer's disk is never left holding a runner's
// scratch files.
func (s *session) Close(ctx context.Context) {
	if s.closed {
		return
	}
	s.closed = true
	if s.fs != nil {
		_ = s.fs.Close()
	}
	if err := s.exec.cfg.Driver.DeleteByExecution(s.scope); err != nil && !errors.Is(err, container.ErrNotFound) {
		_ = ctx
	}
}

// Handle dispatches one op. An op that belongs to a media session, or that does
// not exist, is answered unsupported_op — the protocol's rule for an unknown
// op, and the reason a media op can never reach a workspace.
func (s *session) Handle(ctx context.Context, op string, args json.RawMessage) (any, *protocol.OpError) {
	switch op {
	case protocol.OpWriteFile:
		return s.writeFile(args)
	case protocol.OpReadFile:
		return s.readFile(args)
	case protocol.OpListFiles:
		return s.listFiles(args)
	case protocol.OpDeletePath:
		return s.deletePath(args)
	case protocol.OpExec:
		return s.execOp(ctx, args)
	case protocol.OpStatus:
		return s.status()
	case protocol.OpStage:
		return s.stage(ctx, args)
	case protocol.OpPublish:
		return s.publish(ctx, args)
	case protocol.OpRender:
		return s.render(ctx, args)
	case protocol.OpHydrate:
		return s.hydrate(ctx, args)
	case protocol.OpClose:
		return s.close()
	default:
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp,
			fmt.Sprintf("op %q is not valid in a %s", op, protocol.JobRemotionWorkspace), false)
	}
}

// ---------------------------------------------------------------------------
// Files
// ---------------------------------------------------------------------------

type writeFileArgs struct {
	Path     string `json:"path"`
	Content  string `json:"content"`
	Encoding string `json:"encoding"`
}

type fileResult struct {
	Bytes  int64  `json:"bytes"`
	SHA256 string `json:"sha256"`
}

func (s *session) writeFile(args json.RawMessage) (any, *protocol.OpError) {
	var a writeFileArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "writeFile args: "+err.Error(), false)
	}
	var data []byte
	switch strings.ToLower(strings.TrimSpace(a.Encoding)) {
	case "", "utf8":
		data = []byte(a.Content)
	case "base64":
		decoded, err := base64.StdEncoding.DecodeString(a.Content)
		if err != nil {
			return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "writeFile content is not valid base64", false)
		}
		data = decoded
	default:
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "writeFile encoding must be utf8 or base64", false)
	}
	n, err := s.fs.WriteFile(a.Path, data)
	if err != nil {
		return nil, mapFSError(err, protocol.ErrCodeFileTooLarge)
	}
	return fileResult{Bytes: n, SHA256: agent.SHA256Bytes(data)}, nil
}

type readFileArgs struct {
	Path     string `json:"path"`
	MaxBytes int64  `json:"maxBytes"`
}

type readResult struct {
	Content   string `json:"content"`
	Encoding  string `json:"encoding"`
	Bytes     int64  `json:"bytes"`
	Truncated bool   `json:"truncated"`
}

func (s *session) readFile(args json.RawMessage) (any, *protocol.OpError) {
	var a readFileArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "readFile args: "+err.Error(), false)
	}
	// "maxBytes (at most 4 MiB)": a larger request is clamped rather than
	// honoured, because the answer has to fit in one payload.
	maxBytes := a.MaxBytes
	if maxBytes <= 0 || maxBytes > agent.MaxInlineBytes {
		maxBytes = agent.MaxInlineBytes
	}
	data, err := s.fs.ReadFile(a.Path, maxBytes)
	if err != nil {
		return nil, mapFSError(err, protocol.ErrCodeFileTooLarge)
	}
	res := encodeContent(data)
	if !agent.FitsInline(res) {
		return nil, agent.OpError(protocol.ErrCodeFileTooLarge, "the file would not fit one inline payload once encoded", false)
	}
	return res, nil
}

// encodeContent picks the transport encoding for a file's bytes. Binary content
// is base64 rather than a lossy conversion: the engine asked for the bytes, not
// for text that happens to survive a round trip.
func encodeContent(data []byte) readResult {
	if utf8.Valid(data) {
		return readResult{Content: string(data), Encoding: "utf8", Bytes: int64(len(data))}
	}
	return readResult{
		Content:   base64.StdEncoding.EncodeToString(data),
		Encoding:  "base64",
		Bytes:     int64(len(data)),
		Truncated: false,
	}
}

type listFilesArgs struct {
	Path  string `json:"path"`
	Depth int    `json:"depth"`
}

func (s *session) listFiles(args json.RawMessage) (any, *protocol.OpError) {
	var a listFilesArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "listFiles args: "+err.Error(), false)
	}
	entries, err := s.list(a.Path, a.Depth)
	if err != nil {
		return nil, mapFSError(err, protocol.ErrCodeFileTooLarge)
	}
	return map[string]any{"entries": entries}, nil
}

func (s *session) list(path string, depth int) ([]agent.Entry, error) {
	if path == "" || path == "." {
		return s.fs.ListRoot(depth)
	}
	return s.fs.List(path, depth)
}

type deletePathArgs struct {
	Path string `json:"path"`
}

func (s *session) deletePath(args json.RawMessage) (any, *protocol.OpError) {
	var a deletePathArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "deletePath args: "+err.Error(), false)
	}
	deleted, err := s.fs.Delete(a.Path)
	if err != nil {
		return nil, mapFSError(err, protocol.ErrCodeFileTooLarge)
	}
	return map[string]any{"deleted": deleted}, nil
}

// ---------------------------------------------------------------------------
// exec
// ---------------------------------------------------------------------------

type execArgs struct {
	Command    string   `json:"command"`
	Args       []string `json:"args"`
	TimeoutSec int      `json:"timeoutSec"`
}

type execResult struct {
	ExitCode        int    `json:"exitCode"`
	Stdout          string `json:"stdout"`
	Stderr          string `json:"stderr"`
	StdoutTruncated bool   `json:"stdoutTruncated"`
	StderrTruncated bool   `json:"stderrTruncated"`
	DurationMs      int64  `json:"durationMs"`
}

func (s *session) execOp(ctx context.Context, args json.RawMessage) (any, *protocol.OpError) {
	var a execArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "exec args: "+err.Error(), false)
	}
	// The runner re-validates the command itself, even though the engine built
	// it from first-party code: the specification is explicit that a bug
	// upstream must not let anything reach a customer's machine.
	if s.exec.cfg.AllCores >= 2 {
		a.Args = agent.RaiseRemotionConcurrency(a.Args, s.exec.cfg.AllCores)
	}
	req := container.ExecRequest{Command: a.Command, Args: a.Args, TimeoutSeconds: a.TimeoutSec}
	if err := container.ValidateExec(req); err != nil {
		return nil, agent.OpError(protocol.ErrCodeExecNotAllowed,
			fmt.Sprintf("%s is not in the exec allowlist", a.Command), false)
	}
	res, err := s.exec.cfg.Driver.RunExecResult(s.scope, req)
	if err != nil {
		return nil, agent.OpError(protocol.ErrCodeContainerUnavailable, "the command could not be run: "+err.Error(), true)
	}
	stdout, stdoutCut := clipHead(res.Stdout, maxInlineOutput)
	stderr, stderrCut := clipTail(res.Stderr, maxInlineOutput)
	_ = ctx
	return execResult{
		ExitCode:        res.ExitCode,
		Stdout:          stdout,
		Stderr:          stderr,
		StdoutTruncated: stdoutCut,
		StderrTruncated: stderrCut,
		DurationMs:      res.Duration.Milliseconds(),
	}, nil
}

// clipHead keeps the first n bytes. A command's stdout is where structured
// output lives, so its beginning is what a caller needs.
func clipHead(b []byte, n int) (string, bool) {
	if len(b) <= n {
		return string(b), false
	}
	return string(b[:n]), true
}

// clipTail keeps the last n bytes. A command's stderr is where the failure is
// explained, and that explanation is at the end.
func clipTail(b []byte, n int) (string, bool) {
	if len(b) <= n {
		return string(b), false
	}
	return string(b[len(b)-n:]), true
}

// ---------------------------------------------------------------------------
// status
// ---------------------------------------------------------------------------

type statusResult struct {
	WorkspaceReady bool  `json:"workspaceReady"`
	DiskUsedBytes  int64 `json:"diskUsedBytes"`
	OpenProcesses  int   `json:"openProcesses"`
}

func (s *session) status() (any, *protocol.OpError) {
	// OpenProcesses is always zero, and that is a statement rather than a
	// placeholder: a session handles one op at a time, so while this op is
	// being answered no other op — and therefore no child process — can be
	// running.
	return statusResult{
		WorkspaceReady: !s.closed,
		DiskUsedBytes:  s.fs.DiskUsed(),
		OpenProcesses:  0,
	}, nil
}

// ---------------------------------------------------------------------------
// stage, publish, hydrate, render
// ---------------------------------------------------------------------------

type stageArgs struct {
	URL      string `json:"url"`
	Path     string `json:"path"`
	MaxBytes int64  `json:"maxBytes"`
	SHA256   string `json:"sha256"`
}

func (s *session) stage(ctx context.Context, args json.RawMessage) (any, *protocol.OpError) {
	var a stageArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "stage args: "+err.Error(), false)
	}
	transfer, opErr := agent.FetchToFSCached(ctx, s.exec.cfg.HTTPClient, s.exec.cfg.SourceCache, s.fs, a.URL, a.Path, a.MaxBytes, a.SHA256)
	if opErr != nil {
		return nil, opErr
	}
	return fileResult{Bytes: transfer.Bytes, SHA256: transfer.SHA256}, nil
}

type publishArgs struct {
	Path        string `json:"path"`
	PutURL      string `json:"putUrl"`
	MaxBytes    int64  `json:"maxBytes"`
	ContentType string `json:"contentType"`
}

func (s *session) publish(ctx context.Context, args json.RawMessage) (any, *protocol.OpError) {
	var a publishArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "publish args: "+err.Error(), false)
	}
	transfer, opErr := agent.PublishFromFS(ctx, s.exec.cfg.HTTPClient, s.fs, a.PutURL, a.Path, a.MaxBytes, a.ContentType)
	if opErr != nil {
		return nil, opErr
	}
	return fileResult{Bytes: transfer.Bytes, SHA256: transfer.SHA256}, nil
}

type hydrateEntry struct {
	Path   string `json:"path"`
	SHA256 string `json:"sha256"`
	URL    string `json:"url"`
}

type hydrateArgs struct {
	Manifest []hydrateEntry `json:"manifest"`
}

// hydrate rebuilds a workspace on a fresh runner from the engine's journal: what
// is already there and still matches its digest is skipped, everything else is
// fetched. It is how a session survives the machine it started on.
func (s *session) hydrate(ctx context.Context, args json.RawMessage) (any, *protocol.OpError) {
	var a hydrateArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "hydrate args: "+err.Error(), false)
	}
	restored, skipped := []string{}, []string{}
	// Sorted so a journal replay is deterministic and two identical hydrations
	// fetch in the same order.
	sort.SliceStable(a.Manifest, func(i, j int) bool { return a.Manifest[i].Path < a.Manifest[j].Path })
	for _, entry := range a.Manifest {
		if _, err := agent.VirtualPath(entry.Path); err != nil {
			return nil, agent.OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
		}
		if entry.SHA256 != "" {
			if _, _, err := s.fs.Stat(entry.Path); err == nil {
				if digest, err := s.fs.Digest(entry.Path); err == nil && strings.EqualFold(digest, entry.SHA256) {
					skipped = append(skipped, entry.Path)
					continue
				}
			}
		}
		if _, opErr := agent.FetchToFSCached(ctx, s.exec.cfg.HTTPClient, s.exec.cfg.SourceCache, s.fs, entry.URL, entry.Path, 0, entry.SHA256); opErr != nil {
			return nil, opErr
		}
		restored = append(restored, entry.Path)
	}
	return map[string]any{"restored": restored, "skipped": skipped}, nil
}

type renderArgs struct {
	CompositionID string   `json:"compositionId"`
	Args          []string `json:"args"`
	PutURL        string   `json:"putUrl"`
	OutputName    string   `json:"outputName"`
}

type renderResult struct {
	Bytes      int64  `json:"bytes"`
	SHA256     string `json:"sha256"`
	DurationMs int64  `json:"durationMs"`
}

// render runs the Remotion CLI inside the sandbox container and publishes the
// result.
//
// The render flags come from the engine but are the runner's to check: a flag
// that names a host path is refused here, and the CLI itself only ever sees the
// composition id, a workspace-relative output name and flags that passed.
func (s *session) render(ctx context.Context, args json.RawMessage) (any, *protocol.OpError) {
	var a renderArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "render args: "+err.Error(), false)
	}
	if _, err := agent.VirtualPath(a.OutputName); err != nil {
		return nil, agent.OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
	}
	if a.CompositionID == "" || strings.ContainsAny(a.CompositionID, "/\\") {
		return nil, agent.OpError(protocol.ErrCodeInvalidPath, "the composition id must be a plain name", false)
	}
	for _, arg := range a.Args {
		if err := checkRenderFlag(arg); err != nil {
			return nil, agent.OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
		}
	}
	argv := append([]string{"remotion", "render", a.CompositionID, a.OutputName}, a.Args...)
	req := container.ExecRequest{Command: "npx", Args: argv}
	if err := container.ValidateExec(req); err != nil {
		return nil, agent.OpError(protocol.ErrCodeExecNotAllowed, "the render command is not in the exec allowlist", false)
	}
	res, err := s.exec.cfg.Driver.RunExecResult(s.scope, req)
	if err != nil {
		return nil, agent.OpError(protocol.ErrCodeContainerUnavailable, "the render could not be run: "+err.Error(), true)
	}
	if res.ExitCode != 0 {
		stderr, _ := clipTail(res.Stderr, 4096)
		return nil, agent.OpError("render_failed",
			fmt.Sprintf("remotion render exited %d: %s", res.ExitCode, stderr), false)
	}
	transfer, opErr := agent.PublishFromFS(ctx, s.exec.cfg.HTTPClient, s.fs, a.PutURL, a.OutputName, 0, "video/mp4")
	if opErr != nil {
		return nil, opErr
	}
	return renderResult{
		Bytes:      transfer.Bytes,
		SHA256:     transfer.SHA256,
		DurationMs: res.Duration.Milliseconds(),
	}, nil
}

// checkRenderFlag refuses a render flag whose value is a path. The flags
// themselves are the engine's; what is refused is a value that names a file
// outside the workspace, which is where a render could otherwise read or write
// on the customer's machine.
func checkRenderFlag(arg string) error {
	value := arg
	if i := strings.IndexByte(arg, '='); i >= 0 {
		value = arg[i+1:]
	}
	if agent.LooksLikeHostPath(value) {
		return fmt.Errorf("render flag %q names a path outside the workspace", arg)
	}
	return nil
}

func (s *session) close() (any, *protocol.OpError) {
	s.Close(context.Background())
	s.job.Done()
	return map[string]any{}, nil
}

// mapFSError maps a workspace-filesystem failure onto the protocol's error
// codes. Two codes are additions to the published list — not_found and, in
// render, render_failed — because the specification's set has no way to say
// "there is no such file" or "the render itself failed", and reporting either
// as invalid_path would mislead the engine.
func mapFSError(err error, tooLargeCode string) *protocol.OpError {
	switch {
	case errors.Is(err, agent.ErrUnsafePath):
		return agent.OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
	case errors.Is(err, agent.ErrTooLarge):
		return agent.OpError(tooLargeCode, err.Error(), false)
	case errors.Is(err, agent.ErrNoSpace):
		return agent.OpError(protocol.ErrCodeDiskFull, err.Error(), false)
	case errors.Is(err, os.ErrNotExist):
		return agent.OpError("not_found", err.Error(), false)
	default:
		return agent.OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
	}
}
