package container

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"log"
	"os/exec"
	"regexp"
	"strings"
	"time"
)

// DefaultMaxExecTimeout is the ceiling on a requested exec deadline when
// Config.MaxExecTimeout is unset.
const DefaultMaxExecTimeout = 6 * time.Hour

// execDeadline is the request's own deadline, clamped to the configured
// ceiling, or the configured default when the request names none.
func (d *Driver) execDeadline(req ExecRequest) time.Duration {
	if req.TimeoutSeconds <= 0 {
		return d.cfg.ExecTimeout
	}
	ceiling := d.cfg.MaxExecTimeout
	if ceiling <= 0 {
		ceiling = DefaultMaxExecTimeout
	}
	timeout := time.Duration(req.TimeoutSeconds) * time.Second
	if timeout > ceiling {
		timeout = ceiling
	}
	return timeout
}

// execMarkerVar tags every process an exec starts inside the sandbox (the
// variable is inherited by npm, node, chrome and ffmpeg alike), so a timed-out
// exec can be reaped by marker. Killing the `docker exec` client on timeout
// does NOT kill the command inside the container: a timed-out render kept
// running for minutes, starving every later render of CPU and memory.
const execMarkerVar = "REELBOLT_EXEC_ID"

func newExecMarker() string {
	b := make([]byte, 12)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// execArgs builds `exec -w /workspace -e MARKER=<id> <container> <cmd> <args...>`.
func execArgs(container, marker string, req ExecRequest) []string {
	args := []string{"exec", "-w", "/workspace", "-e", execMarkerVar + "=" + marker, container, req.Command}
	return append(args, req.Args...)
}

// killMarkedProcessesScript SIGKILLs every process whose environment carries
// the marker. The marker is hex, so it is safe to splice into the script.
func killMarkedProcessesScript(marker string) string {
	return `for p in /proc/[0-9]*; do ` +
		`if tr '\0' '\n' < "$p/environ" 2>/dev/null | grep -qx '` + execMarkerVar + `=` + marker + `'; then ` +
		`kill -9 "${p#/proc/}" 2>/dev/null; fi; done; true`
}

// reapTimedOutExec kills what a timed-out exec left running in the container.
func (d *Driver) reapTimedOutExec(ctx context.Context, container, marker string) {
	if ctx.Err() == nil {
		return
	}
	reapCtx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if out, err := d.run(reapCtx, "exec", container, "sh", "-c", killMarkedProcessesScript(marker)); err != nil {
		log.Printf("could not reap timed-out exec %s in %s: %v: %s", marker, container, err, strings.TrimSpace(string(out)))
		return
	}
	log.Printf("reaped processes of timed-out exec %s in %s", marker, container)
}

func (d *Driver) RunExec(workflowExecutionID string, req ExecRequest) (string, error) {
	// The exec is counted as in flight from here until it returns (restart,
	// headless-shell probe and the command itself), so the janitor cannot stop
	// the container under it however long the command runs.
	sb, release, err := d.acquire(workflowExecutionID, true)
	if err != nil {
		return "", err
	}
	defer release()
	if err := ValidateExec(req); err != nil {
		return "", err
	}

	// Fail with a clear message if the image lacks the headless shell, rather
	// than letting Remotion die later with an opaque ENOENT.
	if _, err := d.withRevive(workflowExecutionID, func() (string, error) {
		return "", d.verifyHeadlessShell(sb.ContainerName)
	}); err != nil {
		return "", err
	}

	// normalize request args (in-place) before executing
	SanitizeExecRequest(&req)

	timeout := d.execDeadline(req)

	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()

	marker := newExecMarker()
	args := execArgs(sb.ContainerName, marker, req)
	defer d.reapTimedOutExec(ctx, sb.ContainerName, marker)
	log.Printf("exec request for workflow %s: %s %v", workflowExecutionID, req.Command, req.Args)
	out, err := d.withRevive(workflowExecutionID, func() (string, error) {
		output, err := d.run(ctx, args...)
		return string(output), err
	})
	output := []byte(out)
	if err != nil {
		log.Printf("exec failed for workflow %s: %v; output: %s", workflowExecutionID, err, string(output))
		return string(output), fmt.Errorf("execution failed: %w", err)
	}
	log.Printf("exec succeeded for workflow %s; output length %d", workflowExecutionID, len(output))
	return string(output), nil
}

// ExecResult is one exec's outcome with stdout and stderr kept apart.
//
// The separation is not cosmetic: `npx remotion compositions` prints JSON on
// stdout while everything the user watches scrolls past on stderr, so a caller
// that merged the two could not parse the composition list it asked for.
type ExecResult struct {
	Stdout   []byte
	Stderr   []byte
	ExitCode int
	Duration time.Duration
}

// RunExecResult runs an allowlisted command inside the sandbox and returns its
// streams separately, with the command's own exit code rather than a Go error.
//
// A non-zero exit is a normal result here — the caller is an engine that wants
// to read a build failure — so the returned error is reserved for an exec that
// could not be started at all.
//
// It runs the CLI directly rather than through the injectable Runner, because a
// Runner returns combined output by construction. Tests of callers substitute
// the Driver interface instead.
func (d *Driver) RunExecResult(workflowExecutionID string, req ExecRequest) (ExecResult, error) {
	var res ExecResult
	sb, release, err := d.acquire(workflowExecutionID, true)
	if err != nil {
		return res, err
	}
	defer release()
	if err := ValidateExec(req); err != nil {
		return res, err
	}
	if _, err := d.withRevive(workflowExecutionID, func() (string, error) {
		return "", d.verifyHeadlessShell(sb.ContainerName)
	}); err != nil {
		return res, err
	}
	SanitizeExecRequest(&req)

	timeout := d.execDeadline(req)
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()

	marker := newExecMarker()
	args := execArgs(sb.ContainerName, marker, req)
	defer d.reapTimedOutExec(ctx, sb.ContainerName, marker)
	res, runErr := d.execCLI(ctx, args)
	if runErr == nil && res.ExitCode != 0 && isNotRunning(string(res.Stderr)) {
		// The exec never started: the container was down. Bring it back and
		// try once more; a second miss is reported as a sandbox failure.
		if rerr := d.revive(workflowExecutionID); rerr != nil {
			return res, notRunningError(rerr)
		}
		res, runErr = d.execCLI(ctx, args)
		if runErr == nil && res.ExitCode != 0 && isNotRunning(string(res.Stderr)) {
			return res, notRunningError(nil)
		}
	}
	return res, runErr
}

// execCLI runs one `<binary> exec ...` directly and splits its streams. A
// non-zero exit of the command is reported in the result, not as an error.
func (d *Driver) execCLI(ctx context.Context, args []string) (ExecResult, error) {
	cmd := exec.CommandContext(ctx, d.Binary, args...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout, cmd.Stderr = &stdout, &stderr
	started := time.Now()
	runErr := cmd.Run()
	res := ExecResult{
		Stdout:   stdout.Bytes(),
		Stderr:   stderr.Bytes(),
		Duration: time.Since(started),
	}
	var exitErr *exec.ExitError
	if errors.As(runErr, &exitErr) {
		res.ExitCode = exitErr.ExitCode()
		runErr = nil
	} else if runErr != nil {
		res.ExitCode = -1
	}
	return res, runErr
}

// isNotRunning recognises the container CLIs' answer to an exec in a container
// that is not up: docker says "container <id> is not running", podman "can only
// create exec sessions on running containers: container state improper".
func isNotRunning(output string) bool {
	// A daemon refusal is one short line. Requiring that keeps a command that
	// merely prints these words (and genuinely ran) from being retried.
	if len(output) > 1024 {
		return false
	}
	return (strings.Contains(output, "Error response from daemon") && strings.Contains(output, "is not running")) ||
		strings.Contains(output, "can only create exec sessions on running containers")
}

// notRunningError is the error body the engine shows the model when a command
// could not run because the sandbox container had gone down. The command did
// not run, the workspace on disk is intact, and a retry is safe.
func notRunningError(cause error) error {
	if cause != nil && !errors.Is(cause, ErrSandboxNotRunning) {
		cause = fmt.Errorf("%w and could not be restarted: %w", ErrSandboxNotRunning, cause)
	}
	if cause != nil {
		return cause
	}
	return fmt.Errorf("%w: the sandbox container was stopped or restarted underneath this command, so it did not run; "+
		"files in /workspace are intact but any process that was running in the sandbox was lost - retry the command", ErrSandboxNotRunning)
}

// withRevive runs attempt and, if it fails because the container is not
// running, restarts the container and runs it once more. Only a failure of
// the "is not running" kind is retried - that error means the exec never
// started, so repeating it cannot run a command twice.
func (d *Driver) withRevive(workflowExecutionID string, attempt func() (string, error)) (string, error) {
	out, err := attempt()
	if err == nil || !isNotRunning(out+" "+err.Error()) {
		return out, err
	}
	if rerr := d.revive(workflowExecutionID); rerr != nil {
		return out, notRunningError(rerr)
	}
	out, err = attempt()
	if err != nil && isNotRunning(out+" "+err.Error()) {
		return out, notRunningError(nil)
	}
	return out, err
}

// SanitizeExecRequest ensures that certain dangerous or missing arguments are
// added or normalized before a container exec is performed. Remotion projects
// require a Chromium-like binary capable of running in headless mode. The
// upstream Chrome binary no longer includes the traditional `--headless` flag
// (error seen: "headless mode has been replaced and is no longer included in
// the chrome binary"), so we explicitly point the CLI at the lightweight
// headless-shell binary bundled by Remotion.
func SanitizeExecRequest(req *ExecRequest) {
	if req == nil {
		return
	}
	if req.Command == "npx" && len(req.Args) >= 2 && req.Args[0] == "remotion" &&
		req.Args[1] == "render" {
		has := false
		for _, a := range req.Args[2:] {
			if strings.HasPrefix(a, "--chromium-executable") {
				has = true
				break
			}
		}
		if !has {
			// headlessShellPath is a stable, architecture-neutral symlink baked
			// into the image (see sandbox/Dockerfile).
			req.Args = append(req.Args, "--chromium-executable="+headlessShellPath)
		}
	}
}

// headlessShellPath is the stable, architecture-neutral location of Remotion's
// chrome-headless-shell inside a sandbox workspace. The sandbox-runtime image
// build resolves the real, architecture-specific binary (the directory and file
// names differ between amd64 and arm64) and links it here, and the template
// is copied into /workspace with that link intact. There is no fallback to a
// system Chromium: the runtime image does not ship one.
const headlessShellPath = "/workspace/node_modules/.remotion/headless-shell"

// verifyHeadlessShell checks, inside the sandbox container, that the headless
// shell exists and is executable. A missing binary is a hard error.
func (d *Driver) verifyHeadlessShell(container string) error {
	cmd := `[ -x ` + headlessShellPath + ` ] || { echo "headless shell not found or not executable at ` + headlessShellPath + `; the sandbox-runtime image is missing Remotion's chrome-headless-shell" >&2; exit 1; }`
	out, err := d.run(context.Background(), "exec", container, "sh", "-c", cmd)
	if err != nil {
		return fmt.Errorf("headless shell unavailable in sandbox: %w: %s", err, strings.TrimSpace(string(out)))
	}
	return nil
}

func ValidateExec(req ExecRequest) error {
	if req.Command == "" {
		return ErrBadExec
	}
	switch req.Command {
	case "npm":
		if len(req.Args) < 2 || req.Args[0] != "run" {
			return ErrBadExec
		}
		if _, ok := allowedNPMScript[req.Args[1]]; !ok {
			return ErrBadExec
		}
		if len(req.Args) > 2 {
			// Trailing args are accepted only after one explicit "--" separator, so they reach the
			// script and are never read by npm as its own config (--script-shell, --prefix, ...).
			if req.Args[2] != "--" || len(req.Args) < 4 {
				return ErrBadExec
			}
			for _, a := range req.Args[3:] {
				if !npmRunArgRe.MatchString(a) {
					return ErrBadExec
				}
			}
		}
		return nil
	case "npx":
		if len(req.Args) < 2 || req.Args[0] != "remotion" {
			return ErrBadExec
		}
		switch req.Args[1] {
		case "render", "still", "compositions":
			return nil
		default:
			return ErrBadExec
		}
	case "node":
		// Exactly one first-party script, shipped in the image (not the workspace), with flag-only
		// arguments: the layout lint behind the engine's CheckLayout tool. Input hygiene like the
		// rest of this list — the container boundary is the containment.
		if len(req.Args) < 1 || req.Args[0] != LayoutLintScriptPath || len(req.Args) > 9 {
			return ErrBadExec
		}
		for _, a := range req.Args[1:] {
			if !layoutLintArgRe.MatchString(a) {
				return ErrBadExec
			}
		}
		return nil
	default:
		return ErrBadExec
	}
}

// LayoutLintScriptPath is the layout lint the sandbox-runtime image ships
// (sandbox/template/reelbolt/check-layout.mjs, copied to /opt/remotion-template by the
// Dockerfile). It is the only script `node` may run through /exec.
const LayoutLintScriptPath = "/opt/remotion-template/reelbolt/check-layout.mjs"

// npmRunArgRe is the shape of each script argument after the single "--" in `npm run <script> -- ...`:
// --flag or --flag=value.
var npmRunArgRe = regexp.MustCompile(`^--[a-z][a-z0-9-]{0,40}(=[A-Za-z0-9_.,:/@=+-]{1,200})?$`)

// layoutLintArgRe is the only argument shape the layout lint takes: --name=value, the value
// made of composition-id, number and list characters.
var layoutLintArgRe = regexp.MustCompile(`^--[a-z][a-z-]{0,30}=[A-Za-z0-9_.,-]{1,200}$`)

// MaxReadFileBytes caps a single /files/content read. The workspace is written

// ExecRequest is a command to run inside a sandbox container. The command and
// arguments are checked by ValidateExec before anything is executed.
type ExecRequest struct {
	Command        string   `json:"command"`
	Args           []string `json:"args"`
	TimeoutSeconds int      `json:"timeoutSeconds"`
}
