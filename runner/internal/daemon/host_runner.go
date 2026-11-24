package daemon

import (
	"bytes"
	"context"
	"errors"
	"os/exec"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/media"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

// hostRunner runs ffmpeg and ffprobe as host processes with this runner's
// limits: the thread cap in argv, the priority on the process.
//
// It replaces the media package's own HostRunner rather than wrapping it,
// because both limits live at the moment the process is created — one in the
// argument vector the package builds, the other on the process handle — and
// exec.Cmd exposes no hook between the fork and the exec. Everything else is
// deliberately identical to that runner, so a media job behaves the same way on
// a machine with no container runtime as it did before this WP.
type hostRunner struct{ limits governor.Limits }

// Run implements media.CommandRunner.
func (h hostRunner) Run(ctx context.Context, dir string, argv []string) (media.Result, error) {
	var res media.Result
	if len(argv) == 0 {
		return res, errors.New("daemon: empty command")
	}
	argv = h.limits.WithFFmpegThreads(argv)

	cmd := exec.CommandContext(ctx, argv[0], argv[1:]...)
	cmd.Dir = dir
	var stdout, stderr bytes.Buffer
	cmd.Stdout, cmd.Stderr = &stdout, &stderr

	started := time.Now()
	if err := cmd.Start(); err != nil {
		return res, err
	}
	// A priority that cannot be set is not a reason to fail an encode: the
	// process is already running and the job is more valuable than the
	// niceness. The error is dropped on purpose.
	_ = governor.LowerPriority(cmd.Process.Pid, h.limits.Priority)
	err := cmd.Wait()

	res = media.Result{Stdout: stdout.Bytes(), Stderr: stderr.Bytes(), Duration: time.Since(started)}
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
