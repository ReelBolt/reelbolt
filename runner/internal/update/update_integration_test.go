package update

import (
	"context"
	"errors"
	"os"
	"strings"
	"testing"
)

// These are the acceptance-criteria tests of E9, run against a local file
// server standing in for the release host: no network, no real key, and no real
// binary, but the whole path — fetch, verify the signature, download, verify the
// bytes, install, report — is the one a release would take.
//
// What a real release pipeline (E10b) still has to prove, and this cannot:
//   - that the private key is offline and the published manifest verifies with
//     the public key compiled into the shipped build;
//   - that the artifacts a real build produces are the ones the manifest names,
//     on real Windows and Linux machines;
//   - that Authenticode verification passes on a signed Windows binary (the
//     Windows branch here compiles but cannot run without Windows).

func TestIntegrationUpdatesFromALocalReleaseServer(t *testing.T) {
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "v0.0.1")
	rel := newTestRelease(t, releaseOptions{Version: "0.0.2"})

	up := &Updater{
		Host:       rel.newHost(),
		Keys:       rel.KeySet,
		Platform:   Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "0.0.1", DeviceID: "device-1"},
		Install:    Install{Path: target},
		StagingDir: t.TempDir(),
		Gate:       GateFunc(func() bool { return false }),
		Log:        func(string, ...any) {},
	}

	out, err := up.RunOnce(context.Background())
	if err != nil {
		t.Fatalf("RunOnce: %v", err)
	}
	if !out.Applied || out.Version != "0.0.2" {
		t.Fatalf("the update was not applied: %+v", out)
	}
	got, err := os.ReadFile(target)
	if err != nil {
		t.Fatalf("read the installed binary: %v", err)
	}
	if !strings.Contains(string(got), "0.0.2") {
		t.Fatalf("the installed binary is %q, want the 0.0.2 artifact", got)
	}
	if backup, err := os.ReadFile(target + ".prev"); err != nil || string(backup) != "v0.0.1" {
		t.Fatalf("the previous binary was not kept: %q, %v", backup, err)
	}
	// A second run is up to date, not an update.
	out, err = up.RunOnce(context.Background())
	if err != nil {
		t.Fatalf("second RunOnce: %v", err)
	}
	if out.Action != ActionUpToDate || out.Applied {
		t.Fatalf("second run: %+v", out)
	}
}

func TestIntegrationRefusesATamperedArtifactEndToEnd(t *testing.T) {
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "v0.0.1")
	rel := newTestRelease(t, releaseOptions{Version: "0.0.2", Tamper: true})
	staging := t.TempDir()

	up := &Updater{
		Host:       rel.newHost(),
		Keys:       rel.KeySet,
		Platform:   Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "0.0.1", DeviceID: "device-1"},
		Install:    Install{Path: target},
		StagingDir: staging,
		Log:        func(string, ...any) {},
	}
	_, err := up.RunOnce(context.Background())
	if !errors.Is(err, ErrDigest) {
		t.Fatalf("a tampered artifact produced %v, want ErrDigest", err)
	}
	got, _ := os.ReadFile(target)
	if string(got) != "v0.0.1" {
		t.Fatalf("the installed binary changed to %q after a failed verification", got)
	}
	// Nothing partial is left where the next attempt could pick it up.
	if err := walkHasNoPartFiles(staging); err != nil {
		t.Fatal(err)
	}
}

func TestIntegrationRefusesAManifestSomeoneElseSigned(t *testing.T) {
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "v0.0.1")
	// The manifest is valid, the key is not ours: an attacker who can write to
	// the bucket still cannot make a runner install their build.
	other := newTestRelease(t, releaseOptions{Version: "0.0.2"})
	rel := newTestRelease(t, releaseOptions{Version: "0.0.2", SignWith: other.Key})

	up := &Updater{
		Host:       rel.newHost(),
		Keys:       rel.KeySet,
		Platform:   Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "0.0.1", DeviceID: "device-1"},
		Install:    Install{Path: target},
		StagingDir: t.TempDir(),
		Log:        func(string, ...any) {},
	}
	if _, err := up.RunOnce(context.Background()); !errors.Is(err, ErrSignature) {
		t.Fatalf("a foreign signature produced %v, want ErrSignature", err)
	}
	if got, _ := os.ReadFile(target); string(got) != "v0.0.1" {
		t.Fatalf("the installed binary changed to %q", got)
	}
	if n := rel.ArtifactRequests.Load(); n != 0 {
		t.Fatalf("the artifact was fetched %d times despite a bad signature", n)
	}
}

func TestIntegrationDefersWhileAJobRuns(t *testing.T) {
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "v0.0.1")
	rel := newTestRelease(t, releaseOptions{Version: "0.0.2"})
	busy := true

	up := &Updater{
		Host:       rel.newHost(),
		Keys:       rel.KeySet,
		Platform:   Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "0.0.1", DeviceID: "device-1"},
		Install:    Install{Path: target},
		StagingDir: t.TempDir(),
		Gate:       GateFunc(func() bool { return busy }),
		Log:        func(string, ...any) {},
	}
	if _, err := up.RunOnce(context.Background()); !errors.Is(err, ErrJobRunning) {
		t.Fatalf("an update during a job produced %v, want ErrJobRunning", err)
	}
	if n := rel.ArtifactRequests.Load(); n != 0 {
		t.Fatalf("the artifact was downloaded %d times while a job was running", n)
	}
	if got, _ := os.ReadFile(target); string(got) != "v0.0.1" {
		t.Fatal("the binary changed while a job was running")
	}

	// The job ends and the next attempt installs the release.
	busy = false
	out, err := up.RunOnce(context.Background())
	if err != nil {
		t.Fatalf("RunOnce after the job ended: %v", err)
	}
	if !out.Applied {
		t.Fatalf("the deferred update was not applied afterwards: %+v", out)
	}
}

func TestIntegrationHoldsBackADeviceOutsideTheRollout(t *testing.T) {
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "v0.0.1")
	rel := newTestRelease(t, releaseOptions{Version: "0.0.2", ZeroRollout: true})

	up := &Updater{
		Host:       rel.newHost(),
		Keys:       rel.KeySet,
		Platform:   Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "0.0.1", DeviceID: "device-1"},
		Install:    Install{Path: target},
		StagingDir: t.TempDir(),
		Log:        func(string, ...any) {},
	}
	out, err := up.RunOnce(context.Background())
	if err != nil {
		t.Fatalf("RunOnce: %v", err)
	}
	if out.Action != ActionHoldBack || out.Applied {
		t.Fatalf("a 0%% rollout did not hold the device back: %+v", out)
	}
	if n := rel.ArtifactRequests.Load(); n != 0 {
		t.Fatalf("a held-back device downloaded the artifact %d times", n)
	}
}

func TestIntegrationUnreachableHostLeavesTheRunnerAlone(t *testing.T) {
	// The update host being down is not the runner's problem: the build keeps
	// running and the check is retried later.
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "v0.0.1")
	rel := newTestRelease(t, releaseOptions{Version: "0.0.2"})
	host := rel.newHost()
	rel.Server.Close()

	up := &Updater{
		Host:       host,
		Keys:       rel.KeySet,
		Platform:   Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "0.0.1", DeviceID: "device-1"},
		Install:    Install{Path: target},
		StagingDir: t.TempDir(),
		Log:        func(string, ...any) {},
	}
	if _, err := up.RunOnce(context.Background()); !errors.Is(err, ErrHostUnreachable) {
		t.Fatalf("an unreachable host produced %v, want ErrHostUnreachable", err)
	}
	if got, _ := os.ReadFile(target); string(got) != "v0.0.1" {
		t.Fatal("the binary changed while the update host was unreachable")
	}
}

func TestIntegrationRefusesADowngradeEndToEnd(t *testing.T) {
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "v2.0.0")
	rel := newTestRelease(t, releaseOptions{Version: "1.9.0"})

	up := &Updater{
		Host:       rel.newHost(),
		Keys:       rel.KeySet,
		Platform:   Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "2.0.0", DeviceID: "device-1"},
		Install:    Install{Path: target},
		StagingDir: t.TempDir(),
		Log:        func(string, ...any) {},
	}
	out, err := up.RunOnce(context.Background())
	if err != nil {
		t.Fatalf("RunOnce: %v", err)
	}
	if out.Action != ActionRefuseDowngrade || out.Applied {
		t.Fatalf("a downgrade was not refused: %+v", out)
	}
	if n := rel.ArtifactRequests.Load(); n != 0 {
		t.Fatalf("a downgrade downloaded the artifact %d times", n)
	}
	if got, _ := os.ReadFile(target); string(got) != "v2.0.0" {
		t.Fatal("the binary was replaced by an older release")
	}
}

func TestIntegrationUnstampedBuildNeverSelfUpdates(t *testing.T) {
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "dev build")
	rel := newTestRelease(t, releaseOptions{Version: "0.0.2"})

	up := &Updater{
		Host:       rel.newHost(),
		Keys:       rel.KeySet,
		Platform:   Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "dev", DeviceID: "device-1"},
		Install:    Install{Path: target},
		StagingDir: t.TempDir(),
		Log:        func(string, ...any) {},
	}
	out, err := up.RunOnce(context.Background())
	if err != nil {
		t.Fatalf("RunOnce: %v", err)
	}
	if out.Action != ActionRefuseUnknownVersion || out.Applied {
		t.Fatalf("a development build updated itself: %+v", out)
	}
	if got, _ := os.ReadFile(target); string(got) != "dev build" {
		t.Fatal("a development build was replaced by a release")
	}
}

// walkHasNoPartFiles asserts that no partial download survived.
func walkHasNoPartFiles(dir string) error {
	entries, err := os.ReadDir(dir)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil
		}
		return err
	}
	for _, e := range entries {
		if strings.HasSuffix(e.Name(), partSuffix) {
			return errors.New("a partial download survived: " + e.Name())
		}
		if e.IsDir() {
			if err := walkHasNoPartFiles(dir + string(os.PathSeparator) + e.Name()); err != nil {
				return err
			}
		}
	}
	return nil
}
