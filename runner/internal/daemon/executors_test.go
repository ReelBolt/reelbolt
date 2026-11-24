package daemon

import (
	"context"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
)

// The media package's container runner sets no resource flags of its own, so
// the daemon's CLI wrapper is where a compile job gets its caps. This drives
// the real wrapper with a binary that prints its arguments.
func TestTheMediaCLIAddsTheGovernorsCaps(t *testing.T) {
	limits := governor.LimitsConfig{
		Memory: 2 << 30, CPUs: 1.5, PIDs: 128, FFmpegThreads: 1,
		Priority: governor.PriorityLow, DiskQuotaBytes: 3 << 30,
	}.Resolve(governor.Host{})

	cli := mediaCLI(runtime.Candidate{Binary: "docker"}, limits)
	stdout, stderr, err := cli(context.Background(), "echo",
		"run", "--rm", "--network", "none", "reelbolt-sandbox-runtime:local", "ffmpeg", "-i", "in.mp4")
	if err != nil {
		t.Fatalf("the wrapper could not run: %v (%s)", err, stderr)
	}
	line := strings.TrimSpace(string(stdout))
	for _, want := range []string{"--memory 2g", "--cpus 1.5", "--pids-limit 128", "--cpu-shares 256"} {
		if !strings.Contains(line, want) {
			t.Errorf("the media container has no %q: %s", want, line)
		}
	}
	// The command the media package asked for must still be intact and last.
	if !strings.HasSuffix(line, "reelbolt-sandbox-runtime:local ffmpeg -i in.mp4") {
		t.Errorf("the invocation was rewritten: %s", line)
	}
	if !strings.HasPrefix(line, "run ") {
		t.Errorf("the subcommand must stay first: %s", line)
	}
}

// The workspace driver sets its own memory, CPU and pids from the same
// configuration, so the runner wrapper must only add what is missing.
func TestTheWorkspaceRunnerDoesNotDuplicateTheDriversOwnFlags(t *testing.T) {
	limits := governor.LimitsConfig{}.Resolve(governor.Host{})
	args := []string{"run", "-d", "--name", "c", "--memory", "1g", "--cpus", "1", "--pids-limit", "64", "image"}
	got := limits.WithContainerResourceFlags(args)
	joined := strings.Join(got, " ")
	if strings.Count(joined, "--memory") != 1 || strings.Count(joined, "--cpus") != 1 {
		t.Fatalf("the driver's flags were duplicated: %s", joined)
	}
	if !strings.Contains(joined, "--cpu-shares 512") {
		t.Fatalf("the CPU-shares weight is the one flag the driver has no field for: %s", joined)
	}
}
