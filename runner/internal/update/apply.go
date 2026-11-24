package update

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
)

// The refusals an apply can end in. Every one of them leaves the installed
// binary byte-identical to what it was.
var (
	// ErrJobRunning is returned when a job is in flight. An update never
	// interrupts work: the render is the reason the machine is paired at all,
	// and replacing the binary underneath a running job is how a job dies in a
	// way the gateway bills as a runner failure.
	ErrJobRunning = errors.New("update: a job is running, so the update is deferred")
	// ErrNoInstall means there is nothing this runner can replace.
	ErrNoInstall = errors.New("update: this runner cannot locate the installed binary")
	// ErrOSSigned means the operating system's own signature check refused the
	// artifact. It is separate from ErrSignature because it is a different
	// authority (Authenticode, not our release key).
	ErrOSSigned = errors.New("update: the operating system refused the artifact's signature")
	// ErrFormatMismatch means the verified artifact is not something this
	// installation can apply.
	ErrFormatMismatch = errors.New("update: the artifact's format does not match this installation")
)

// Gate reports whether the runner is currently executing a job. The daemon
// supplies one; a test supplies a function.
type Gate interface{ Busy() bool }

// GateFunc adapts a function to Gate.
type GateFunc func() bool

// Busy implements Gate.
func (f GateFunc) Busy() bool { return f() }

// Install is what an update replaces on this machine.
type Install struct {
	// Path is the file replaced by an in-place update.
	Path string
	// AppImage is true when Path is a running AppImage.
	AppImage bool
	// Managed is true when the file is owned by the system package manager
	// (a .deb): such an install is told about a release, never self-replaced.
	Managed bool
}

// CurrentInstall locates the installed runner.
//
// An AppImage is detected the way an AppImage expects to be: the runtime sets
// APPIMAGE to the file it mounted. A package-managed install is detected by
// where the binary lives (below /usr), because replacing a file dpkg owns
// behind dpkg's back leaves the system's own records wrong.
func CurrentInstall() (Install, error) {
	if appImage := strings.TrimSpace(os.Getenv("APPIMAGE")); appImage != "" {
		if info, err := os.Stat(appImage); err == nil && info.Mode().IsRegular() {
			return Install{Path: appImage, AppImage: true}, nil
		}
	}
	exe, err := os.Executable()
	if err != nil {
		return Install{}, fmt.Errorf("%w: %w", ErrNoInstall, err)
	}
	if resolved, err := filepath.EvalSymlinks(exe); err == nil {
		exe = resolved
	}
	if info, err := os.Stat(exe); err != nil || !info.Mode().IsRegular() {
		return Install{}, fmt.Errorf("%w: %s is not a regular file", ErrNoInstall, exe)
	}
	return Install{Path: exe, Managed: isPackageManaged(exe)}, nil
}

// isPackageManaged reports whether a binary lives where the distribution's
// packages put their programs.
func isPackageManaged(path string) bool {
	for _, prefix := range []string{"/usr/bin/", "/usr/local/bin/", "/usr/sbin/", "/opt/reelbolt/"} {
		if strings.HasPrefix(path, prefix) {
			return true
		}
	}
	return false
}

// FormatPreference is the artifact format order for an installation: an AppImage
// install wants the AppImage, anything else wants the plain archive, and a
// package-managed install prefers to be told about a .deb rather than have its
// files replaced behind the package manager's back.
func FormatPreference(install Install, goos string) []string {
	switch goos {
	case OSLinux:
		if install.Managed {
			return []string{FormatDeb, FormatTarGz, FormatAppImage}
		}
		if install.AppImage {
			return []string{FormatAppImage, FormatTarGz, FormatDeb}
		}
		return []string{FormatTarGz, FormatAppImage, FormatDeb}
	case OSWindows:
		return []string{FormatExe, FormatMSI}
	default:
		return nil
	}
}

// Outcome is what an update did.
type Outcome struct {
	// Action is the decision that produced this outcome.
	Action Action
	// Version is the release the outcome is about.
	Version string
	// Applied is true when the installed binary was replaced.
	Applied bool
	// RestartRequired is true when the new binary takes effect on next start.
	RestartRequired bool
	// NotifyOnly is set when the artifact was verified but not installed, with
	// Notify holding what to tell the user. It is not a failure: a .deb is
	// installed by the package manager, by design.
	NotifyOnly bool
	// Notify is the sentence to show the user for a NotifyOnly outcome.
	Notify string
	// Backup is the previous binary, when one was kept.
	Backup string
	// Reason is the one-line explanation.
	Reason string
}

// Updater performs the whole sequence: check, download, verify, apply.
type Updater struct {
	// Host is the update host. Required.
	Host *Host
	// Keys are the trusted release keys. Required; empty refuses everything.
	Keys KeySet
	// Platform is this machine.
	Platform Platform
	// Install is what is replaced.
	Install Install
	// StagingDir holds downloads before they are verified. Default
	// <UserCacheDir>/ReelBolt/update.
	StagingDir string
	// Gate refuses an apply while a job runs. A nil gate means "never busy",
	// which is only correct for a one-shot "update apply" run by hand.
	Gate Gate
	// Log receives the one-line progress messages. Nil means silent.
	Log func(format string, args ...any)

	// mu guards appliedVersion.
	mu sync.Mutex
	// appliedVersion is the release this process installed, if it installed
	// one. The running binary is still the old one — the version only changes
	// when the process restarts — so without this a second check in the same
	// process would install the same release again.
	appliedVersion string
}

// effectiveVersion is the version the decision is made against: this build's,
// or the one this process has already installed and not yet restarted into.
func (u *Updater) effectiveVersion() string {
	u.mu.Lock()
	defer u.mu.Unlock()
	if u.appliedVersion != "" {
		return u.appliedVersion
	}
	return u.Platform.Version
}

func (u *Updater) recordApplied(version string) {
	u.mu.Lock()
	defer u.mu.Unlock()
	u.appliedVersion = version
}

// SetGate installs the job-activity gate.
//
// The daemon owns the count of jobs in flight and hands its own check in here,
// so the refusal is enforced at the moment of the swap and not only at the
// moment of the check.
func (u *Updater) SetGate(g Gate) { u.Gate = g }

// Check fetches and verifies the manifest and decides what to do about it.
//
// An unreachable host comes back as an error whose errors.Is(err,
// ErrHostUnreachable) is true; the caller is expected to log it and carry on.
// It is deliberately not fatal, and it is not retried in a loop here.
func (u *Updater) Check(ctx context.Context) (*Manifest, Decision, error) {
	if u.Host == nil {
		return nil, Decision{}, errors.New("update: no update host is configured")
	}
	m, _, err := u.Host.FetchManifest(ctx, u.Keys)
	if err != nil {
		return nil, Decision{}, err
	}
	p := u.Platform
	p.Version = u.effectiveVersion()
	if len(p.Formats) == 0 {
		p.Formats = FormatPreference(u.Install, p.GOOS)
	}
	d, err := Decide(p, m)
	if err != nil {
		return nil, Decision{}, err
	}
	return m, d, nil
}

// RunOnce performs one complete update attempt and reports what happened.
//
// It returns an error only for conditions the caller should act on; every
// refusal (hold-back, downgrade, a missing artifact, a platform with no
// self-update) comes back as an Outcome with Applied false and a Reason. The
// one error a caller must treat as "try again later" is ErrJobRunning.
func (u *Updater) RunOnce(ctx context.Context) (Outcome, error) {
	_, d, err := u.Check(ctx)
	if err != nil {
		return Outcome{Action: ActionHoldBack, Reason: "the update check did not complete: " + err.Error()}, err
	}
	out := Outcome{Action: d.Action, Version: d.Version, Reason: d.Reason}
	if d.Action != ActionUpdate {
		u.logf("update: %s", d.Reason)
		return out, nil
	}
	if u.Gate != nil && u.Gate.Busy() {
		u.logf("update: %s is available but a job is running; deferring", d.Version)
		return out, ErrJobRunning
	}
	staged, err := u.Stage(ctx, d)
	if err != nil {
		return out, err
	}
	applied, err := u.Apply(ctx, staged, d)
	if err != nil {
		return out, err
	}
	applied.Action = d.Action
	applied.Version = d.Version
	if applied.Reason == "" {
		applied.Reason = d.Reason
	}
	return applied, nil
}

// stagingDir is where downloads land.
func (u *Updater) stagingDir() (string, error) {
	if u.StagingDir != "" {
		return u.StagingDir, nil
	}
	cache, err := config.CacheDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(cache, "update"), nil
}

// Stage downloads the decision's artifact and verifies it against the manifest.
// The returned path is a complete, hash-matching file: nothing partial is ever
// returned, and a failure removes what it wrote.
func (u *Updater) Stage(ctx context.Context, d Decision) (string, error) {
	if d.Action != ActionUpdate {
		return "", fmt.Errorf("update: nothing to stage (%s: %s)", d.Action, d.Reason)
	}
	dir, err := u.stagingDir()
	if err != nil {
		return "", err
	}
	// The staging directory is per-version so a half-finished attempt at one
	// release cannot be mistaken for another.
	dir = filepath.Join(dir, sanitizeVersion(d.Version))
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return "", fmt.Errorf("update: create %s: %w", dir, err)
	}
	dest := filepath.Join(dir, artifactName(d.Artifact))
	u.logf("update: downloading %s (%d bytes)", d.Artifact.URL, d.Artifact.Size)
	if err := u.Host.Download(ctx, d.Artifact, dest); err != nil {
		return "", err
	}
	// Download already hashed the stream as it wrote; this hashes the file that
	// is actually on disk, which is what Apply will read.
	if err := VerifyArtifactFile(dest, d.Artifact); err != nil {
		_ = os.Remove(dest)
		return "", err
	}
	return dest, nil
}

// Apply installs a staged, verified artifact.
//
// The sequence is the security-relevant part, so it is stated here rather than
// left to each platform's implementation:
//
//  1. a job must not be running;
//  2. the staged file is re-verified against the manifest (the bytes on disk
//     now, not the bytes that were hashed while downloading);
//  3. for a Windows artifact, the operating system's own signature check;
//  4. the current binary is moved aside as a backup;
//  5. the new file is put in place by a rename in the same directory, so the
//     path is either the old binary or the new one and never a partial file;
//  6. if anything after step 4 fails, the backup is restored.
func (u *Updater) Apply(ctx context.Context, staged string, d Decision) (Outcome, error) {
	var out Outcome
	a := d.Artifact
	if u.Gate != nil && u.Gate.Busy() {
		return out, ErrJobRunning
	}
	if u.Install.Path == "" {
		return out, ErrNoInstall
	}
	if err := VerifyArtifactFile(staged, a); err != nil {
		return out, err
	}
	if err := u.verifyPlatformSignature(staged, a); err != nil {
		return out, err
	}
	switch a.Format {
	case FormatDeb:
		// A package-managed install is verified and announced, never applied:
		// the package manager owns those files, and a runner that wrote over
		// them would leave the system's own database wrong.
		out.NotifyOnly = true
		out.Notify = fmt.Sprintf("ReelBolt runner %s is available. Install it with your package manager, for example: sudo apt install %s",
			d.Version, staged)
		out.Reason = "the release was verified but this installation is package-managed, so it is not replaced in place"
		u.logf("update: %s", out.Reason)
		return out, nil
	case FormatMSI:
		return u.applyMSI(ctx, staged, out)
	case FormatExe, FormatAppImage, FormatTarGz:
		return u.applyReplace(staged, d, out)
	default:
		return out, fmt.Errorf("%w: %q", ErrFormatMismatch, a.Format)
	}
}

// applyReplace swaps the installed file for the verified one.
func (u *Updater) applyReplace(staged string, d Decision, out Outcome) (Outcome, error) {
	a := d.Artifact
	target := u.Install.Path
	if err := u.checkFormatMatchesInstall(a); err != nil {
		return out, err
	}
	info, err := os.Stat(target)
	if err != nil {
		return out, fmt.Errorf("update: %w: %s: %w", ErrNoInstall, target, err)
	}
	dir := filepath.Dir(target)
	// Everything happens in the target's own directory: a rename across
	// filesystems is a copy, and a copy is not atomic.
	next := filepath.Join(dir, "."+filepath.Base(target)+".new")
	backup := target + ".prev"

	in, err := os.Open(staged)
	if err != nil {
		return out, fmt.Errorf("update: open the staged artifact: %w", err)
	}
	defer func() { _ = in.Close() }()
	mode := info.Mode().Perm()
	if mode&0o111 == 0 {
		mode |= 0o755
	}
	// O_EXCL on a fresh name, so a pre-existing file cannot be written through.
	_ = os.Remove(next)
	f, err := os.OpenFile(next, os.O_CREATE|os.O_EXCL|os.O_WRONLY, mode)
	if err != nil {
		return out, fmt.Errorf("update: create %s: %w", next, err)
	}
	if _, err := f.ReadFrom(in); err != nil {
		_ = f.Close()
		_ = os.Remove(next)
		return out, fmt.Errorf("update: write %s: %w", next, err)
	}
	if err := f.Sync(); err != nil {
		_ = f.Close()
		_ = os.Remove(next)
		return out, fmt.Errorf("update: flush %s: %w", next, err)
	}
	if err := f.Close(); err != nil {
		_ = os.Remove(next)
		return out, fmt.Errorf("update: close %s: %w", next, err)
	}
	// Re-verify the copy: the copy is the bytes that will run.
	if err := VerifyArtifactFile(next, a); err != nil {
		_ = os.Remove(next)
		return out, err
	}

	// The swap itself is per-platform: Linux renames over the running binary
	// (the process keeps the old inode), Windows cannot replace a running
	// image and hands the last step to a helper that waits for this process to
	// exit.
	swapped, err := u.swapInstalled(target, next, backup, out)
	if err != nil {
		return swapped, err
	}
	out = swapped
	if out.Applied {
		u.recordApplied(d.Version)
		out.Applied = true
		out.RestartRequired = true
		out.Backup = backup
		out.Reason = fmt.Sprintf("release %s installed; it takes effect the next time the runner starts", d.Version)
		u.logf("%s", out.Reason)
	}
	return out, nil
}

// restoreBackup puts the previous binary back and reports both failures when it
// cannot, so a half-applied update is visible rather than silent.
func restoreBackup(target, backup string, cause error) error {
	if restoreErr := os.Rename(backup, target); restoreErr != nil {
		return fmt.Errorf("update: install %s failed (%w) and the rollback failed too (%w); the previous binary is at %s",
			target, cause, restoreErr, backup)
	}
	return fmt.Errorf("update: install %s: %w (rolled back)", target, cause)
}

// checkFormatMatchesInstall refuses an artifact this installation cannot apply.
func (u *Updater) checkFormatMatchesInstall(a Artifact) error {
	switch {
	case a.Format == FormatAppImage && !u.Install.AppImage:
		return fmt.Errorf("%w: an AppImage cannot be installed over %s", ErrFormatMismatch, u.Install.Path)
	case a.Format == FormatTarGz && u.Install.AppImage:
		return fmt.Errorf("%w: this is an AppImage install; replacing it with an archive would break the runtime",
			ErrFormatMismatch)
	case a.Format == FormatExe && u.Platform.GOOS != OSWindows:
		return fmt.Errorf("%w: an .exe on %s", ErrFormatMismatch, u.Platform.GOOS)
	}
	return nil
}

// Rollback restores the binary that an update moved aside.
func (u *Updater) Rollback() error {
	target := u.Install.Path
	if target == "" {
		return ErrNoInstall
	}
	backup := target + ".prev"
	if _, err := os.Stat(backup); err != nil {
		return fmt.Errorf("update: no previous binary at %s", backup)
	}
	failed := target + ".failed"
	_ = os.Remove(failed)
	if _, err := os.Stat(target); err == nil {
		if err := os.Rename(target, failed); err != nil {
			return fmt.Errorf("update: %w", err)
		}
	}
	if err := os.Rename(backup, target); err != nil {
		return fmt.Errorf("update: restore %s: %w", target, err)
	}
	u.logf("update: rolled back to the previous binary (%s)", target)
	return nil
}

// SweepStaging removes download scaffolding: version directories from earlier
// attempts, so a machine that updates often does not accumulate every release it
// ever fetched.
func (u *Updater) SweepStaging(now time.Time, retention time.Duration) (int, error) {
	dir, err := u.stagingDir()
	if err != nil {
		return 0, err
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return 0, nil
		}
		return 0, fmt.Errorf("update: read %s: %w", dir, err)
	}
	cutoff := now.Add(-retention)
	removed := 0
	for _, e := range entries {
		info, err := e.Info()
		if err != nil {
			continue
		}
		if !info.ModTime().Before(cutoff) {
			continue
		}
		if err := os.RemoveAll(filepath.Join(dir, e.Name())); err != nil {
			return removed, fmt.Errorf("update: remove %s: %w", e.Name(), err)
		}
		removed++
	}
	return removed, nil
}

// sanitizeVersion makes a manifest version safe to use as a directory name. It
// is a whitelist rather than an escape: a version is a small alphabet, and a
// hostile one must not be able to reach outside the staging root.
func sanitizeVersion(v string) string {
	out := make([]rune, 0, len(v))
	for _, r := range strings.TrimSpace(v) {
		switch {
		case r >= '0' && r <= '9', r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z':
			out = append(out, r)
		case r == '.' || r == '-' || r == '+' || r == '_':
			// A separator at the start, or one following another, is dropped:
			// the result is a name, and a name may not begin with a dot or
			// contain "..", however the input was spelled.
			if len(out) == 0 || out[len(out)-1] == '.' || out[len(out)-1] == '-' || out[len(out)-1] == '+' || out[len(out)-1] == '_' {
				continue
			}
			out = append(out, r)
		}
		if len(out) >= 64 {
			break
		}
	}
	if len(out) == 0 {
		return "unknown"
	}
	return string(out)
}

// artifactName is the file name a staged artifact is written under. It comes
// from the manifest entry, never from the URL: a URL is attacker-influenceable
// text and a file name is a filesystem operation.
func artifactName(a Artifact) string {
	return fmt.Sprintf("reelbolt-runner-%s-%s.%s", a.OS, a.Arch, a.Format)
}

func (u *Updater) logf(format string, args ...any) {
	if u.Log != nil {
		u.Log(format, args...)
	}
}
