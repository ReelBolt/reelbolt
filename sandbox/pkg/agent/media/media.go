// Package media executes a media session: video.compile and
// video.analyze.extract, whose ops are ffmpeg-level and whose every argument
// is re-validated here.
//
// A media session runs in a directory of its own that the worker creates and
// deletes. ffmpeg runs inside the runner's runtime container when it has one
// and as a host process when it does not; either way it only ever sees the
// customer's own media, filter specifications authored by first-party code, and
// an argv this package has already refused everything dangerous in.
package media

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// stderrTailBytes is how much of a failed command's stderr travels back. The
// explanation of a failed encode is at the end of it.
const stderrTailBytes = 8 << 10

// Result is one command's outcome.
type Result struct {
	ExitCode int
	Stdout   []byte
	Stderr   []byte
	Duration time.Duration
}

// CommandRunner runs one ffmpeg or ffprobe invocation. argv includes the
// program name as its first element.
type CommandRunner interface {
	Run(ctx context.Context, dir string, argv []string) (Result, error)
}

// HostRunner runs the command as a host process: no shell, so nothing in argv
// can be re-interpreted as a command.
type HostRunner struct{}

// Run implements CommandRunner.
func (HostRunner) Run(ctx context.Context, dir string, argv []string) (Result, error) {
	var res Result
	if len(argv) == 0 {
		return res, errors.New("media: empty command")
	}
	cmd := exec.CommandContext(ctx, argv[0], argv[1:]...)
	cmd.Dir = dir
	var stdout, stderr bytes.Buffer
	cmd.Stdout, cmd.Stderr = &stdout, &stderr
	started := time.Now()
	err := cmd.Run()
	res = Result{Stdout: stdout.Bytes(), Stderr: stderr.Bytes(), Duration: time.Since(started)}
	var exitErr *exec.ExitError
	if errors.As(err, &exitErr) {
		res.ExitCode = exitErr.ExitCode()
		return res, nil
	}
	if err != nil {
		return res, err
	}
	return res, nil
}

// CLI runs one container-CLI invocation, keeping stdout and stderr apart.
type CLI func(ctx context.Context, args ...string) (stdout, stderr []byte, err error)

// ContainerRunner runs ffmpeg and ffprobe inside the runtime image, with the
// session directory bind-mounted read-write at /media and nothing else visible.
//
// The flags are the same posture the workspace driver uses: no network, every
// capability dropped, no new privileges, a read-only root with a small tmpfs,
// and the session directory as the only writable mount.
type ContainerRunner struct {
	// Binary is the container CLI ("docker" or "podman").
	Binary string
	// CLI runs it. Nil means a real exec.
	CLI CLI
	// Image is the runtime image reference.
	Image string
	// Network is the container network. Empty means "none".
	Network string
	// RunAs is the uid:gid the command runs as, so the files it writes are
	// readable by the runner. Nil means the current process's.
	RunAs *[2]int
	// ExtraEnv are environment entries passed through.
	ExtraEnv []string
}

// Run implements CommandRunner.
func (c *ContainerRunner) Run(ctx context.Context, dir string, argv []string) (Result, error) {
	var res Result
	if len(argv) == 0 {
		return res, errors.New("media: empty command")
	}
	network := c.Network
	if network == "" {
		network = "none"
	}
	// args[0] is the CLI binary itself, not an argument to it: the default CLI
	// execs it by name, and an injected CLI that follows the same contract
	// (the container Runner contract: "the arguments after the binary name")
	// sees the binary the same way.
	args := []string{c.Binary, "run", "--rm", "--network", network,
		"--cap-drop", "ALL",
		"--security-opt", "no-new-privileges",
		"--read-only",
		"--tmpfs", "/tmp:rw,nosuid,nodev,size=256m",
	}
	if runAs := c.runAs(); runAs != "" {
		args = append(args, "--user", runAs)
	}
	for _, e := range c.ExtraEnv {
		args = append(args, "--env", e)
	}
	args = append(args, "-v", dir+":/media", "-w", "/media", c.Image)
	args = append(args, argv...)

	run := c.CLI
	if run == nil {
		run = execCLI
	}
	stdout, stderr, err := run(ctx, args...)
	res.Stdout, res.Stderr = stdout, stderr
	if err != nil {
		var exitErr *exec.ExitError
		if errors.As(err, &exitErr) {
			res.ExitCode = exitErr.ExitCode()
			return res, nil
		}
		return res, err
	}
	return res, nil
}

func (c *ContainerRunner) runAs() string {
	if c.RunAs != nil {
		return strconv.Itoa(c.RunAs[0]) + ":" + strconv.Itoa(c.RunAs[1])
	}
	if os.Getuid() < 0 {
		return ""
	}
	return strconv.Itoa(os.Getuid()) + ":" + strconv.Itoa(os.Getgid())
}

func execCLI(ctx context.Context, args ...string) ([]byte, []byte, error) {
	if len(args) == 0 {
		return nil, nil, errors.New("media: empty container command")
	}
	// args[0] is the configured CLI binary (docker or podman); the rest are its
	// arguments.
	cmd := exec.CommandContext(ctx, args[0], args[1:]...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout, cmd.Stderr = &stdout, &stderr
	err := cmd.Run()
	return stdout.Bytes(), stderr.Bytes(), err
}

// Config is the executor's configuration.
type Config struct {
	// QuotaBytes caps one session directory. Zero means 8 GiB.
	QuotaBytes int64
	// HTTPClient performs the presigned transfers.
	HTTPClient *http.Client
	// FFmpegPath and FFprobePath are the host binaries, used when Container is
	// nil. Empty means "ffmpeg" and "ffprobe".
	FFmpegPath  string
	FFprobePath string
	// Container, when set, runs ffmpeg and ffprobe inside the runtime image
	// instead of as host processes — the choice the specification makes for a
	// runner that has a container runtime.
	Container *ContainerRunner
	// Runner overrides the host runner, for tests and for an embedder that
	// bundles ffmpeg somewhere unusual. Nil means HostRunner{}.
	Runner CommandRunner
	// SourceCache keeps staged source media between jobs, so a second stage
	// of the same object is revalidated instead of downloaded (see
	// agent.FetchToFSCached). Nil means no cache: every stage downloads, which
	// is exactly the behaviour before the cache existed.
	SourceCache agent.SourceCache
	// AllCores, when 2 or more, raises the "-threads N" of every ffmpeg command to that many
	// threads (see agent.RaiseFFmpegThreads). Zero leaves argv exactly as the caller sent it.
	AllCores int
}

// Executor builds one session per job.
type Executor struct{ cfg Config }

// New builds a media executor.
func New(cfg Config) (*Executor, error) {
	if cfg.HTTPClient == nil {
		cfg.HTTPClient = agent.DefaultHTTPClient()
	}
	if cfg.QuotaBytes <= 0 {
		cfg.QuotaBytes = 8 << 30
	}
	if cfg.FFmpegPath == "" {
		cfg.FFmpegPath = "ffmpeg"
	}
	if cfg.FFprobePath == "" {
		cfg.FFprobePath = "ffprobe"
	}
	return &Executor{cfg: cfg}, nil
}

// New implements agent.Factory.
func (e *Executor) New(ctx context.Context, job *agent.Job) (agent.Session, error) {
	switch job.JobType {
	case protocol.JobVideoCompile, protocol.JobVideoAnalyzeEx:
	default:
		return nil, &agent.JobError{
			Class:   protocol.FailInput,
			Code:    protocol.ErrCodeUnsupportedOp,
			Message: "this executor runs only media sessions",
		}
	}
	if err := os.MkdirAll(job.ScratchDir, 0o700); err != nil {
		return nil, &agent.JobError{
			Class:     protocol.FailPlatform,
			Code:      protocol.ErrCodeDiskFull,
			Message:   "the session directory could not be created: " + err.Error(),
			Retryable: true,
		}
	}
	fs, err := agent.OpenRootFS(job.ScratchDir, e.cfg.QuotaBytes)
	if err != nil {
		return nil, &agent.JobError{
			Class:     protocol.FailPlatform,
			Code:      protocol.ErrCodeDiskFull,
			Message:   "the session directory could not be opened: " + err.Error(),
			Retryable: true,
		}
	}
	_ = ctx
	return &session{exec: e, job: job, fs: fs, dir: job.ScratchDir}, nil
}

type session struct {
	exec   *Executor
	job    *agent.Job
	fs     *agent.RootFS
	dir    string
	closed bool
}

// Close deletes the session directory. The core also removes the job's scratch
// directory; this is the belt to that pair of braces, and it runs before the
// container or process could still be holding a file.
func (s *session) Close(ctx context.Context) {
	if s.closed {
		return
	}
	s.closed = true
	_ = s.fs.Close()
	_ = ctx
	if err := os.RemoveAll(s.dir); err != nil {
		// A directory that cannot be removed is worth knowing about, but it is
		// not a reason to leave the job open. The error is formatted for the
		// log and then discarded: this branch has no logger of its own, and
		// the core removes the same directory again after the session closes,
		// so a failure here is at worst a duplicate of that cleanup's.
		_, _ = fmt.Fprintf(io.Discard, "%v", err)
	}
}

// Handle dispatches one media op. A workspace op is answered unsupported_op:
// the two op sets do not overlap, and an op that does not exist is the
// protocol's own example of what unsupported_op is for.
func (s *session) Handle(ctx context.Context, op string, args json.RawMessage) (any, *protocol.OpError) {
	switch op {
	case protocol.OpStage:
		return s.stage(ctx, args)
	case protocol.OpFfmpeg:
		return s.ffmpeg(ctx, args)
	case protocol.OpFfprobe:
		return s.ffprobe(ctx, args)
	case protocol.OpRead:
		return s.read(args)
	case protocol.OpPublish:
		return s.publish(ctx, args)
	case protocol.OpStat:
		return s.stat(args)
	case protocol.OpClose:
		s.Close(ctx)
		s.job.Done()
		return map[string]any{}, nil
	default:
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp,
			fmt.Sprintf("op %q is not valid in a media session", op), false)
	}
}

type fileResult struct {
	Bytes  int64  `json:"bytes"`
	SHA256 string `json:"sha256"`
}

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

type readArgs struct {
	Path     string `json:"path"`
	MaxBytes int64  `json:"maxBytes"`
}

type readResult struct {
	Content  string `json:"content"`
	Encoding string `json:"encoding"`
	Bytes    int64  `json:"bytes"`
}

func (s *session) read(args json.RawMessage) (any, *protocol.OpError) {
	var a readArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "read args: "+err.Error(), false)
	}
	maxBytes := a.MaxBytes
	if maxBytes <= 0 || maxBytes > agent.MaxInlineBytes {
		maxBytes = agent.MaxInlineBytes
	}
	data, err := s.fs.ReadFile(a.Path, maxBytes)
	if err != nil {
		return nil, mapFSError(err)
	}
	out := readResult{Bytes: int64(len(data))}
	if utf8.Valid(data) {
		out.Content, out.Encoding = string(data), "utf8"
	} else {
		out.Content, out.Encoding = base64.StdEncoding.EncodeToString(data), "base64"
	}
	if !agent.FitsInline(out) {
		return nil, agent.OpError(protocol.ErrCodeFileTooLarge, "the file would not fit one inline payload once encoded", false)
	}
	return out, nil
}

type statArgs struct {
	Path string `json:"path"`
}

func (s *session) stat(args json.RawMessage) (any, *protocol.OpError) {
	var a statArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "stat args: "+err.Error(), false)
	}
	exists, size, err := s.fs.Stat(a.Path)
	if err != nil {
		return nil, mapFSError(err)
	}
	return map[string]any{"exists": exists, "bytes": size}, nil
}

type commandArgs struct {
	Argv       []string `json:"argv"`
	TimeoutSec int      `json:"timeoutSec"`
}

type ffmpegResult struct {
	ExitCode   int    `json:"exitCode"`
	StderrTail string `json:"stderrTail"`
	DurationMs int64  `json:"durationMs"`
}

type ffprobeResult struct {
	ExitCode   int    `json:"exitCode"`
	Stdout     string `json:"stdout"`
	StderrTail string `json:"stderrTail"`
	DurationMs int64  `json:"durationMs"`
}

func (s *session) ffmpeg(ctx context.Context, args json.RawMessage) (any, *protocol.OpError) {
	var a commandArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "ffmpeg args: "+err.Error(), false)
	}
	if err := ValidateFFmpegOutputs(a.Argv); err != nil {
		return nil, agent.OpError(protocol.ErrCodeArgvNotAllowed, err.Error(), false)
	}
	argv, opErr := s.validated(a.Argv, s.exec.cfg.FFmpegPath)
	if opErr != nil {
		return nil, opErr
	}
	runCtx, cancel := s.commandContext(ctx, a.TimeoutSec)
	defer cancel()
	res, err := s.runner().Run(runCtx, s.dir, argv)
	if err != nil {
		return nil, agent.OpError("container_unavailable", "the encode could not be started: "+err.Error(), true)
	}
	return ffmpegResult{
		ExitCode:   res.ExitCode,
		StderrTail: tail(string(res.Stderr), stderrTailBytes),
		DurationMs: res.Duration.Milliseconds(),
	}, nil
}

func (s *session) ffprobe(ctx context.Context, args json.RawMessage) (any, *protocol.OpError) {
	var a commandArgs
	if err := json.Unmarshal(args, &a); err != nil {
		return nil, agent.OpError(protocol.ErrCodeUnsupportedOp, "ffprobe args: "+err.Error(), false)
	}
	argv, opErr := s.validated(a.Argv, s.exec.cfg.FFprobePath)
	if opErr != nil {
		return nil, opErr
	}
	runCtx, cancel := s.commandContext(ctx, a.TimeoutSec)
	defer cancel()
	res, err := s.runner().Run(runCtx, s.dir, argv)
	if err != nil {
		return nil, agent.OpError("container_unavailable", "the probe could not be started: "+err.Error(), true)
	}
	stdout := string(res.Stdout)
	if len(stdout) > agent.MaxInlineBytes || !agent.FitsInline(stdout) {
		return nil, agent.OpError(protocol.ErrCodeOutputTooLarge, "ffprobe wrote more than the inline payload limit", false)
	}
	return ffprobeResult{
		ExitCode:   res.ExitCode,
		Stdout:     stdout,
		StderrTail: tail(string(res.Stderr), stderrTailBytes),
		DurationMs: res.Duration.Milliseconds(),
	}, nil
}

// validated applies the worker's own argv rules and then the whitelist the
// specification requires on every invocation.
//
// The program is the worker's to choose and is never taken from the op: the
// protocol's argv is "array of strings, not including the program", so an argv
// that names one is a caller mistake rather than a way to run something else.
func (s *session) validated(argv []string, program string) ([]string, *protocol.OpError) {
	if len(argv) == 0 {
		return nil, agent.OpError(protocol.ErrCodeArgvNotAllowed, "empty argv", false)
	}
	if argv[0] == "ffmpeg" || argv[0] == "ffprobe" {
		return nil, agent.OpError(protocol.ErrCodeArgvNotAllowed,
			"argv must not include the program; the worker chooses it", false)
	}
	if err := ValidateArgv(argv, s.dir); err != nil {
		return nil, agent.OpError(protocol.ErrCodeArgvNotAllowed, err.Error(), false)
	}
	argv, err := WithProtocolWhitelist(WithSafeConcat(argv))
	if err != nil {
		return nil, agent.OpError(protocol.ErrCodeArgvNotAllowed, err.Error(), false)
	}
	if s.exec.cfg.AllCores >= 2 {
		argv = agent.RaiseFFmpegThreads(argv, s.exec.cfg.AllCores)
	}
	return append([]string{program}, argv...), nil
}

func (s *session) runner() CommandRunner {
	if s.exec.cfg.Container != nil {
		return s.exec.cfg.Container
	}
	if s.exec.cfg.Runner != nil {
		return s.exec.cfg.Runner
	}
	return HostRunner{}
}

func (s *session) commandContext(ctx context.Context, timeoutSec int) (context.Context, context.CancelFunc) {
	if timeoutSec <= 0 {
		return context.WithCancel(ctx)
	}
	return context.WithTimeout(ctx, time.Duration(timeoutSec)*time.Second)
}

func tail(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[len(s)-n:]
}

// mapFSError maps a session-filesystem failure onto the protocol's error codes.
// not_found is an addition to the published list: the protocol has no code for
// "there is no such file", and answering invalid_path would tell the engine its
// path was unsafe when it was merely absent.
func mapFSError(err error) *protocol.OpError {
	switch {
	case errors.Is(err, agent.ErrUnsafePath):
		return agent.OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
	case errors.Is(err, agent.ErrTooLarge):
		return agent.OpError(protocol.ErrCodeFileTooLarge, err.Error(), false)
	case errors.Is(err, agent.ErrNoSpace):
		return agent.OpError(protocol.ErrCodeDiskFull, err.Error(), false)
	case errors.Is(err, os.ErrNotExist):
		return agent.OpError("not_found", err.Error(), false)
	default:
		return agent.OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
	}
}

var _ = strings.TrimSpace
