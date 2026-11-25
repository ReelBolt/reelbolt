package update

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// decisionFor is the Decision RunOnce would have produced for a test release.
func decisionFor(rel *testRelease) Decision {
	return Decision{Action: ActionUpdate, Version: rel.Manifest.Version, Artifact: rel.Manifest.Artifacts[0]}
}

// newUpdaterFor builds an Updater whose "installed binary" is a file in a
// temporary directory. Nothing here touches the binary running the test.
func newUpdaterFor(t *testing.T, rel *testRelease, target string, gate Gate) *Updater {
	t.Helper()
	return &Updater{
		Host:     rel.newHost(),
		Keys:     rel.KeySet,
		Platform: Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "0.0.1", DeviceID: "device-1"},
		Install:  Install{Path: target},
		Gate:     gate,
	}
}

func TestApplyInstallsAVerifiedArtifact(t *testing.T) {
	skipOnWindows(t)
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "OLD BINARY")
	rel := newTestRelease(t, releaseOptions{})
	up := newUpdaterFor(t, rel, target, nil)

	staged, err := up.Stage(context.Background(), Decision{Action: ActionUpdate, Version: rel.Manifest.Version, Artifact: rel.Manifest.Artifacts[0]})
	if err != nil {
		t.Fatalf("Stage: %v", err)
	}
	out, err := up.Apply(context.Background(), staged, decisionFor(rel))
	if err != nil {
		t.Fatalf("Apply: %v", err)
	}
	if !out.Applied || !out.RestartRequired {
		t.Fatalf("unexpected outcome: %+v", out)
	}
	got, err := os.ReadFile(target)
	if err != nil {
		t.Fatalf("read the installed binary: %v", err)
	}
	if string(got) != string(rel.Artifact) {
		t.Fatalf("the installed binary is %q, want the verified artifact", got)
	}
	backup, err := os.ReadFile(target + ".prev")
	if err != nil {
		t.Fatalf("the previous binary was not kept: %v", err)
	}
	if string(backup) != "OLD BINARY" {
		t.Fatalf("the backup holds %q", backup)
	}
	// Nothing is left in the target directory but the binary, its backup and
	// no half-written .new file.
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range entries {
		if strings.HasSuffix(e.Name(), ".new") {
			t.Fatalf("a staging file was left behind: %s", e.Name())
		}
	}
}

func TestApplyRefusesWhileAJobIsRunning(t *testing.T) {
	// "Never update mid-job" is the rule that matters most on a user's machine:
	// the render is why the machine is paired at all.
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "OLD BINARY")
	rel := newTestRelease(t, releaseOptions{})
	up := newUpdaterFor(t, rel, target, GateFunc(func() bool { return true }))

	staged, err := up.Stage(context.Background(), Decision{Action: ActionUpdate, Version: rel.Manifest.Version, Artifact: rel.Manifest.Artifacts[0]})
	if err != nil {
		t.Fatalf("Stage: %v", err)
	}
	if _, err := up.Apply(context.Background(), staged, decisionFor(rel)); !errors.Is(err, ErrJobRunning) {
		t.Fatalf("Apply while busy produced %v, want ErrJobRunning", err)
	}
	got, _ := os.ReadFile(target)
	if string(got) != "OLD BINARY" {
		t.Fatal("the installed binary changed while a job was running")
	}
	// RunOnce refuses too, before it downloads anything.
	if _, err := up.RunOnce(context.Background()); !errors.Is(err, ErrJobRunning) {
		t.Fatalf("RunOnce while busy produced %v, want ErrJobRunning", err)
	}
}

func TestApplyRefusesAStagedFileThatChangedAfterVerification(t *testing.T) {
	// The bytes are verified again at the moment of the swap, so a file swapped
	// between the download and the install is not installed. This is the
	// time-of-check/time-of-use window, closed.
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "OLD BINARY")
	rel := newTestRelease(t, releaseOptions{})
	up := newUpdaterFor(t, rel, target, nil)

	staged, err := up.Stage(context.Background(), Decision{Action: ActionUpdate, Version: rel.Manifest.Version, Artifact: rel.Manifest.Artifacts[0]})
	if err != nil {
		t.Fatalf("Stage: %v", err)
	}
	if err := os.WriteFile(staged, []byte("EVIL"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := up.Apply(context.Background(), staged, decisionFor(rel)); !errors.Is(err, ErrDigest) {
		t.Fatalf("a swapped staged file produced %v, want ErrDigest", err)
	}
	got, _ := os.ReadFile(target)
	if string(got) != "OLD BINARY" {
		t.Fatal("an artifact that changed after verification was installed")
	}
}

func TestApplyNotifiesInsteadOfReplacingAPackageManagedInstall(t *testing.T) {
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "OLD BINARY")
	rel := newTestRelease(t, releaseOptions{})
	// The release publishes a .deb for a package-managed install.
	rel.Manifest.Artifacts[0].Format = FormatDeb
	up := newUpdaterFor(t, rel, target, nil)

	staged, err := up.Stage(context.Background(), Decision{Action: ActionUpdate, Version: rel.Manifest.Version, Artifact: rel.Manifest.Artifacts[0]})
	if err != nil {
		t.Fatalf("Stage: %v", err)
	}
	out, err := up.Apply(context.Background(), staged, decisionFor(rel))
	if err != nil {
		t.Fatalf("Apply: %v", err)
	}
	if out.Applied || !out.NotifyOnly {
		t.Fatalf("unexpected outcome: %+v", out)
	}
	if !strings.Contains(out.Notify, "package manager") {
		t.Errorf("the notification does not say what to do: %q", out.Notify)
	}
	got, _ := os.ReadFile(target)
	if string(got) != "OLD BINARY" {
		t.Fatal("a package-managed install was replaced in place")
	}
}

func TestApplyRefusesAnArtifactForADifferentInstallation(t *testing.T) {
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "OLD BINARY")
	rel := newTestRelease(t, releaseOptions{})
	rel.Manifest.Artifacts[0].Format = FormatAppImage
	up := newUpdaterFor(t, rel, target, nil)

	staged, err := up.Stage(context.Background(), Decision{Action: ActionUpdate, Version: rel.Manifest.Version, Artifact: rel.Manifest.Artifacts[0]})
	if err != nil {
		t.Fatalf("Stage: %v", err)
	}
	if _, err := up.Apply(context.Background(), staged, decisionFor(rel)); !errors.Is(err, ErrFormatMismatch) {
		t.Fatalf("an AppImage over a tarball install produced %v, want ErrFormatMismatch", err)
	}
	got, _ := os.ReadFile(target)
	if string(got) != "OLD BINARY" {
		t.Fatal("a mismatched artifact was installed")
	}
}

func TestApplyRefusesWhenThereIsNothingToReplace(t *testing.T) {
	rel := newTestRelease(t, releaseOptions{})
	up := newUpdaterFor(t, rel, "", nil)
	if _, err := up.Apply(context.Background(), "whatever", decisionFor(rel)); !errors.Is(err, ErrNoInstall) {
		t.Fatalf("Apply with no install produced %v, want ErrNoInstall", err)
	}
}

func TestRollbackRestoresThePreviousBinary(t *testing.T) {
	skipOnWindows(t)
	dir := t.TempDir()
	target := writeFakeBinary(t, dir, "reelbolt-runner", "OLD BINARY")
	rel := newTestRelease(t, releaseOptions{})
	up := newUpdaterFor(t, rel, target, nil)

	staged, err := up.Stage(context.Background(), Decision{Action: ActionUpdate, Version: rel.Manifest.Version, Artifact: rel.Manifest.Artifacts[0]})
	if err != nil {
		t.Fatalf("Stage: %v", err)
	}
	if _, err := up.Apply(context.Background(), staged, decisionFor(rel)); err != nil {
		t.Fatalf("Apply: %v", err)
	}
	if err := up.Rollback(); err != nil {
		t.Fatalf("Rollback: %v", err)
	}
	got, err := os.ReadFile(target)
	if err != nil {
		t.Fatalf("read after rollback: %v", err)
	}
	if string(got) != "OLD BINARY" {
		t.Fatalf("the binary after rollback is %q, want the previous one", got)
	}
	// A rollback with nothing to roll back to is an error, not a silent no-op.
	if err := up.Rollback(); err == nil {
		t.Fatal("a second rollback succeeded with no backup present")
	}
}

func TestStageRefusesToStageARefusal(t *testing.T) {
	rel := newTestRelease(t, releaseOptions{})
	up := newUpdaterFor(t, rel, writeFakeBinary(t, t.TempDir(), "x", "OLD"), nil)
	if _, err := up.Stage(context.Background(), Decision{Action: ActionHoldBack, Reason: "not this device"}); err == nil {
		t.Fatal("Stage accepted a decision that is not an update")
	}
}

func TestSweepStagingRemovesOldAttempts(t *testing.T) {
	dir := t.TempDir()
	rel := newTestRelease(t, releaseOptions{})
	up := newUpdaterFor(t, rel, writeFakeBinary(t, dir, "x", "OLD"), nil)
	up.StagingDir = dir

	old := filepath.Join(dir, "0.0.0")
	if err := os.MkdirAll(old, 0o700); err != nil {
		t.Fatal(err)
	}
	fresh := filepath.Join(dir, "0.0.9")
	if err := os.MkdirAll(fresh, 0o700); err != nil {
		t.Fatal(err)
	}
	past := time.Now().Add(-48 * time.Hour)
	if err := os.Chtimes(old, past, past); err != nil {
		t.Fatal(err)
	}
	removed, err := up.SweepStaging(time.Now(), 24*time.Hour)
	if err != nil {
		t.Fatalf("SweepStaging: %v", err)
	}
	if removed != 1 {
		t.Fatalf("removed %d entries, want 1", removed)
	}
	if _, err := os.Stat(old); !errors.Is(err, os.ErrNotExist) {
		t.Error("the old staging directory survived the sweep")
	}
	if _, err := os.Stat(fresh); err != nil {
		t.Error("the fresh staging directory was removed")
	}
}

func TestSanitizeVersionKeepsPathsInsideTheStagingRoot(t *testing.T) {
	for _, v := range []string{"../../etc/passwd", "1.0.0/../../../root", "a b c", strings.Repeat("9", 200)} {
		got := sanitizeVersion(v)
		if strings.ContainsAny(got, "/\\") || strings.Contains(got, "..") {
			t.Errorf("sanitizeVersion(%q) = %q, which can escape the staging root", v, got)
		}
		if len(got) > 64 {
			t.Errorf("sanitizeVersion(%q) is %d characters", v, len(got))
		}
	}
	if got := sanitizeVersion(""); got != "unknown" {
		t.Errorf("sanitizeVersion(\"\") = %q", got)
	}
	if got := sanitizeVersion("1.2.3-beta.1"); got != "1.2.3-beta.1" {
		t.Errorf("sanitizeVersion kept too little: %q", got)
	}
}

func TestArtifactNameComesFromTheManifestNotTheURL(t *testing.T) {
	a := Artifact{
		OS: OSLinux, Arch: "amd64", Format: FormatTarGz,
		URL: "https://downloads.reelbolt.ai/runner/../../../../etc/cron.d/evil",
	}
	name := artifactName(a)
	if strings.ContainsAny(name, "/\\") {
		t.Fatalf("artifactName = %q, which is a path", name)
	}
}
