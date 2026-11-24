package update

import (
	"errors"
	"fmt"
	"runtime"
)

// Action is what the runner decided to do about one manifest.
type Action string

const (
	// ActionUpdate means a newer, admitted, platform-matching artifact exists.
	ActionUpdate Action = "update"
	// ActionUpToDate means this build is already the released one.
	ActionUpToDate Action = "up-to-date"
	// ActionHoldBack means the release exists but this device is outside the
	// rollout. It is not a failure and not a refusal: the next check may admit
	// it, and the current build keeps running.
	ActionHoldBack Action = "hold-back"
	// ActionRefuseDowngrade means the manifest offers an older build. A
	// downgrade is how an attacker with the release key (or a rolled-back
	// bucket) undoes a security fix, so it is never applied.
	ActionRefuseDowngrade Action = "refuse-downgrade"
	// ActionRefuseBelowFloor means this build is older than the platform's
	// supported floor. The runner must stop taking jobs.
	ActionRefuseBelowFloor Action = "refuse-below-floor"
	// ActionRefuseUnknownVersion means this build's own version does not parse
	// ("dev", a local build, a corrupted stamp). It is never self-updated: a
	// developer's build is not a release and must not be replaced by one.
	ActionRefuseUnknownVersion Action = "refuse-unknown-version"
	// ActionRefuseNoArtifact means the manifest has no build for this OS/arch.
	ActionRefuseNoArtifact Action = "refuse-no-artifact"
	// ActionRefuseUnsupportedPlatform means this platform has no self-update
	// path at all (D26 ships Windows x64 and Linux x64/arm64 only).
	ActionRefuseUnsupportedPlatform Action = "refuse-unsupported-platform"
)

// Decision is the outcome of comparing a manifest with this build.
type Decision struct {
	Action Action
	// Reason is one line, written for a user reading the log or the UI.
	Reason string
	// Version is the manifest's version.
	Version string
	// CurrentVersion is this build's version.
	CurrentVersion string
	// Artifact is set when Action is ActionUpdate.
	Artifact Artifact
	// Bucket is this device's rollout bucket, for the log.
	Bucket int
	// RolloutPercent is the manifest's rollout.
	RolloutPercent int
	// StopTakingJobs is set when the platform's supported floor is above this
	// build: the runner must stop accepting work.
	StopTakingJobs bool
}

// Platform is the machine the decision is made for. It is a struct rather than
// runtime.GOOS so every branch is reachable in a test on one machine.
type Platform struct {
	GOOS   string
	GOARCH string
	// Version is this build's version, as stamped at build time.
	Version string
	// DeviceID is the paired device, which decides the rollout bucket. Empty
	// (an unpaired machine) buckets as the empty string, which is stable and
	// harmless: an unpaired runner has no jobs to interrupt.
	DeviceID string
	// Formats overrides the artifact format preference. It is set by the
	// Updater from the installation it has to replace (an AppImage install
	// wants the AppImage, a package-managed install wants to be told about the
	// .deb), and empty means the platform default.
	Formats []string
}

// CurrentPlatform describes this process.
func CurrentPlatform(version, deviceID string) Platform {
	return Platform{GOOS: runtime.GOOS, GOARCH: runtime.GOARCH, Version: version, DeviceID: deviceID}
}

// preferredFormats is the apply order for a platform. The first format that
// exists wins, which is what makes an AppImage install update in place while a
// .deb install only gets told about the release.
func preferredFormats(goos string) []string {
	switch goos {
	case OSWindows:
		return []string{FormatExe, FormatMSI}
	case OSLinux:
		return []string{FormatAppImage, FormatTarGz, FormatDeb}
	default:
		return nil
	}
}

// ErrUnsupportedPlatform is returned for a platform with no self-update path.
var ErrUnsupportedPlatform = errors.New("update: this platform has no self-update path")

// Decide compares a verified manifest with this build.
//
// The order of the checks is the order of the reasons a user could read: the
// floor first (a build below it must stop working whatever else is true), then
// this build's own version, then the release's version, then the rollout, then
// whether there is anything to install.
func Decide(p Platform, m *Manifest) (Decision, error) {
	d := Decision{
		Version:        m.Version,
		CurrentVersion: p.Version,
		RolloutPercent: m.RolloutPercent,
		Bucket:         Bucket(p.DeviceID),
	}

	// 1. The platform's floor. minSupported is the offline twin of the gateway's
	// welcome.minAgentVersion: a runner below it must stop taking jobs. An
	// unparseable own version cannot be compared, so it is not claimed to be
	// below the floor — it is refused for the other reason below.
	if m.MinSupported != "" && p.Version != "" {
		if cur, err := ParseVersion(p.Version); err == nil {
			if floor, err := ParseVersion(m.MinSupported); err == nil && cur.Compare(floor) < 0 {
				d.Action = ActionRefuseBelowFloor
				d.StopTakingJobs = true
				d.Reason = fmt.Sprintf("this runner is %s, below the supported %s: it will not take jobs until it is updated",
					p.Version, m.MinSupported)
				return d, nil
			}
		}
	}

	// 2. This build's own version. "dev", an unstamped build or anything else
	// that is not a version is never replaced: a developer's binary is not the
	// released one, and silently swapping it for the released one would destroy
	// work and hide which build produced the output.
	cur, err := ParseVersion(p.Version)
	if err != nil {
		d.Action = ActionRefuseUnknownVersion
		d.Reason = fmt.Sprintf("this build's version %q is not a release version, so it will not update itself", p.Version)
		//nolint:nilerr // the refusal is carried on d.Action/d.Reason, not as an error
		return d, nil
	}

	// 3. Up, never down.
	rel, err := ParseVersion(m.Version)
	if err != nil {
		// Unreachable through FetchManifest, which validates the manifest. Kept
		// because a caller can hand Decide a manifest the parser never saw.
		return d, fmt.Errorf("update: the manifest version %q: %w", m.Version, err)
	}
	switch rel.Compare(cur) {
	case 0:
		d.Action = ActionUpToDate
		d.Reason = fmt.Sprintf("this runner is already %s", p.Version)
		return d, nil
	case -1:
		d.Action = ActionRefuseDowngrade
		d.Reason = fmt.Sprintf("the published release %s is older than this runner's %s; a downgrade is never applied",
			m.Version, p.Version)
		return d, nil
	}

	// 4. The rollout. A device outside it is left alone, and the decision says
	// so rather than looking like a failure.
	if !Admitted(p.DeviceID, m.RolloutPercent) {
		d.Action = ActionHoldBack
		d.Reason = fmt.Sprintf("release %s is rolling out to %d%% of machines; this device is bucket %d of 100",
			m.Version, m.RolloutPercent, d.Bucket)
		return d, nil
	}

	// 5. Is there anything for this machine?
	formats := p.Formats
	if len(formats) == 0 {
		formats = preferredFormats(p.GOOS)
	}
	if len(formats) == 0 {
		d.Action = ActionRefuseUnsupportedPlatform
		d.Reason = fmt.Sprintf("%s/%s has no self-update path (D26 ships Windows x64 and Linux x64/arm64)",
			p.GOOS, p.GOARCH)
		return d, nil
	}
	a, ok := m.Artifact(p.GOOS, p.GOARCH, formats...)
	if !ok {
		d.Action = ActionRefuseNoArtifact
		d.Reason = fmt.Sprintf("release %s publishes no build for %s/%s", m.Version, p.GOOS, p.GOARCH)
		return d, nil
	}
	d.Action = ActionUpdate
	d.Artifact = a
	d.Reason = fmt.Sprintf("release %s is available for %s/%s (%s)", m.Version, p.GOOS, p.GOARCH, a.Format)
	return d, nil
}
