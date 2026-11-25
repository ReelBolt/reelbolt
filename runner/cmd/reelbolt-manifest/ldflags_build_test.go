package main

import (
	"context"
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

// runCommand runs a command with a deadline. Nothing here talks to the network,
// but a wedged toolchain must fail the test rather than hang the suite.
func runCommand(t *testing.T, dir string, args ...string) ([]byte, error) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	cmd := exec.CommandContext(ctx, args[0], args[1:]...)
	cmd.Dir = dir
	return cmd.CombinedOutput()
}

// TestLdflagsStampARealBuild is the only test here that compiles the runner, and
// it exists because the failure it catches is silent.
//
// Go accepts "-X github.com/vecchiotom/reelbolt/runner/cmd/reelbolt-runner
// .Version=1.4.2", records it in the binary's build settings, and then ignores
// it: a MAIN package must be addressed as "main.Version". A build stamped that
// way reports "dev", and "dev" means it refuses every update
// (refuse-unknown-version), so the release is one no installed runner can ever
// move off. Nothing fails; the binary just quietly will not update.
//
// So the build line is checked against the real toolchain instead of against the
// string this tool prints, and both halves are checked: the version the binary
// reports and the trust root it carries.
func TestLdflagsStampARealBuild(t *testing.T) {
	if testing.Short() {
		t.Skip("compiles the runner")
	}
	goTool, err := exec.LookPath("go")
	if err != nil {
		t.Skip("no go toolchain")
	}

	dir := t.TempDir()
	_, _, stamp := genKey(t, dir)
	const collector = "https://collector.example.test/telemetry/v1/reports"
	line := mustRun(t, "ldflags", "-version", "1.4.2", "-fontset", "v9", "-current-key", stamp, "-release",
		"-telemetry-endpoint", collector)

	var ldflags, tags string
	for _, l := range strings.Split(strings.TrimSpace(line), "\n") {
		switch {
		case strings.HasPrefix(l, "ldflags="):
			ldflags = strings.TrimPrefix(l, "ldflags=")
		case strings.HasPrefix(l, "tags="):
			tags = strings.TrimPrefix(l, "tags=")
		}
	}
	if ldflags == "" {
		t.Fatalf("no ldflags in:\n%s", line)
	}
	if tags != "release" {
		t.Fatalf("want tags=release, got %q", tags)
	}

	// The command the release workflow runs, verbatim.
	bin := filepath.Join(dir, "reelbolt-runner"+exeSuffix())
	args := []string{"build", "-trimpath"}
	if tags != "" {
		args = append(args, "-tags", tags)
	}
	args = append(args, "-ldflags", ldflags, "-o", bin, "./cmd/reelbolt-runner")
	if out, err := runCommand(t, filepath.Join("..", ".."), append([]string{goTool}, args...)...); err != nil {
		t.Fatalf("go build: %v\n%s", err, out)
	}

	out, err := runCommand(t, "", bin, "version")
	if err != nil {
		t.Fatalf("version: %v\n%s", err, out)
	}
	if !strings.Contains(string(out), "1.4.2") {
		t.Fatalf("the stamped build does not report the version it was given: %s", out)
	}

	// The collector address. The headless runner does not link the telemetry
	// package, so a probe built with the same line reports what it holds.
	probe := filepath.Join(dir, "telemetryprobe"+exeSuffix())
	probeArgs := append([]string{goTool, "build", "-trimpath", "-tags", tags, "-ldflags", ldflags, "-o", probe},
		"./cmd/reelbolt-manifest/testdata/telemetryprobe")
	if out, err := runCommand(t, filepath.Join("..", ".."), probeArgs...); err != nil {
		t.Fatalf("go build the probe: %v\n%s", err, out)
	}
	if out, err := runCommand(t, "", probe); err != nil || strings.TrimSpace(string(out)) != collector {
		t.Fatalf("the stamped collector is %q (%v), want %q; the -X for telemetry.CollectorEndpoint did not land", out, err, collector)
	}

	// The trust root, offline: "status" reports the key names it will verify
	// against, and a build with none says so. It must not touch the network.
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()
	status := exec.CommandContext(ctx, bin, "status", "--json")
	status.Env = append(os.Environ(), "XDG_CONFIG_HOME="+filepath.Join(dir, "config"))
	out, err = status.CombinedOutput()
	if err != nil {
		t.Fatalf("status: %v\n%s", err, out)
	}
	var rep struct {
		Version     string `json:"version"`
		TrustedKeys string `json:"trustedKeys"`
	}
	if err := json.Unmarshal(out, &rep); err != nil {
		t.Fatalf("status --json: %v\n%s", err, out)
	}
	if rep.Version != "1.4.2" {
		t.Fatalf("status reports version %q, want 1.4.2; the -X for main.Version did not land", rep.Version)
	}
	if !strings.Contains(rep.TrustedKeys, "release-current") {
		t.Fatalf("status reports trusted keys %q, want the stamped release key", rep.TrustedKeys)
	}
}

// The negative control for the same bug, so the reason the build line says
// "main." is recorded in the suite rather than only in a comment. It asserts the
// behaviour of the toolchain this repository is built with; if a future Go ever
// accepts the full path, this test is the thing that says so out loud.
func TestAFullPathXDoesNotStampAMainPackage(t *testing.T) {
	if testing.Short() {
		t.Skip("compiles the runner")
	}
	goTool, err := exec.LookPath("go")
	if err != nil {
		t.Skip("no go toolchain")
	}
	dir := t.TempDir()
	bin := filepath.Join(dir, "reelbolt-runner"+exeSuffix())
	full := "github.com/vecchiotom/reelbolt/runner/cmd/reelbolt-runner.Version=1.4.2"
	if out, err := runCommand(t, filepath.Join("..", ".."), goTool, "build", "-ldflags", "-X "+full, "-o", bin, "./cmd/reelbolt-runner"); err != nil {
		t.Fatalf("go build: %v\n%s", err, out)
	}
	out, err := runCommand(t, "", bin, "version")
	if err != nil {
		t.Fatalf("version: %v\n%s", err, out)
	}
	if strings.Contains(string(out), "1.4.2") {
		t.Fatalf("this toolchain now honours a full-path -X for a main package (%s); the build line in keys.go should be simplified, and this test deleted", out)
	}
	if !strings.Contains(string(out), "dev") {
		t.Fatalf("unexpected version output: %s", out)
	}
}

// exeSuffix is the executable extension of the platform the test runs on: go
// build -o writes exactly the name given, and Windows will not exec a file
// without ".exe".
func exeSuffix() string {
	if runtime.GOOS == "windows" {
		return ".exe"
	}
	return ""
}
