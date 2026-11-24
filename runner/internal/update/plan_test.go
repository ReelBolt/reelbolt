package update

import (
	"strings"
	"testing"
)

// manifestFor builds a manifest with one artifact for the given platform.
func manifestFor(version, minSupported string, rollout int, goos, goarch, format string) *Manifest {
	return &Manifest{
		Version:        version,
		MinSupported:   minSupported,
		Channel:        ChannelStable,
		RolloutPercent: rollout,
		Artifacts: []Artifact{{
			OS: goos, Arch: goarch, Format: format,
			URL:    "https://downloads.reelbolt.ai/runner/x",
			SHA256: strings.Repeat("a", 64), Size: 1024,
		}},
	}
}

func TestDecideOffersANewerRelease(t *testing.T) {
	p := Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "1.0.0", DeviceID: "dev-1"}
	d, err := Decide(p, manifestFor("1.0.1", "1.0.0", 100, OSLinux, "amd64", FormatTarGz))
	if err != nil {
		t.Fatalf("Decide: %v", err)
	}
	if d.Action != ActionUpdate {
		t.Fatalf("Action = %s (%s), want update", d.Action, d.Reason)
	}
	if d.Artifact.Format != FormatTarGz {
		t.Errorf("Artifact = %+v", d.Artifact)
	}
}

func TestDecideIsUpToDateOnTheSameVersion(t *testing.T) {
	p := Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "1.0.0", DeviceID: "dev-1"}
	d, _ := Decide(p, manifestFor("1.0.0", "", 100, OSLinux, "amd64", FormatTarGz))
	if d.Action != ActionUpToDate {
		t.Fatalf("Action = %s, want up-to-date", d.Action)
	}
}

func TestDecideRefusesADowngrade(t *testing.T) {
	// Downgrades are how a published-but-withdrawn release, or an attacker with
	// a stale copy of the manifest, undoes a security fix. They are never
	// applied, whatever the rollout says.
	p := Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "1.4.0", DeviceID: "dev-1"}
	d, _ := Decide(p, manifestFor("1.3.9", "", 100, OSLinux, "amd64", FormatTarGz))
	if d.Action != ActionRefuseDowngrade {
		t.Fatalf("Action = %s (%s), want refuse-downgrade", d.Action, d.Reason)
	}
	if !strings.Contains(d.Reason, "never applied") {
		t.Errorf("the reason does not explain the refusal: %q", d.Reason)
	}
}

func TestDecideHoldsBackADeviceOutsideTheRollout(t *testing.T) {
	p := Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "1.0.0", DeviceID: "dev-1"}
	bucket := Bucket(p.DeviceID)
	// A rollout that stops short of this device's bucket holds it back, and
	// says so without treating it as a failure.
	d, _ := Decide(p, manifestFor("1.1.0", "", bucket, OSLinux, "amd64", FormatTarGz))
	if d.Action != ActionHoldBack {
		t.Fatalf("Action = %s (%s), want hold-back for bucket %d of %d%%", d.Action, d.Reason, bucket, bucket)
	}
	if d.Bucket != bucket || d.RolloutPercent != bucket {
		t.Errorf("the decision does not report the bucket: %+v", d)
	}
	d, _ = Decide(p, manifestFor("1.1.0", "", bucket+1, OSLinux, "amd64", FormatTarGz))
	if d.Action != ActionUpdate {
		t.Fatalf("Action = %s, want update; bucket %d is inside %d%%", d.Action, bucket, bucket+1)
	}
}

func TestDecideStopsTakingJobsBelowTheSupportedFloor(t *testing.T) {
	p := Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "1.0.0", DeviceID: "dev-1"}
	d, _ := Decide(p, manifestFor("1.5.0", "1.2.0", 100, OSLinux, "amd64", FormatTarGz))
	if d.Action != ActionRefuseBelowFloor {
		t.Fatalf("Action = %s, want refuse-below-floor", d.Action)
	}
	if !d.StopTakingJobs {
		t.Error("the decision does not stop the runner taking jobs")
	}
	// The floor is a floor, not a gate on a build that is exactly at it.
	d, _ = Decide(p, manifestFor("1.5.0", "1.0.0", 100, OSLinux, "amd64", FormatTarGz))
	if d.Action != ActionUpdate || d.StopTakingJobs {
		t.Fatalf("a build at the floor was refused: %+v", d)
	}
}

func TestDecideNeverUpdatesAnUnstampedBuild(t *testing.T) {
	// "dev" is what a locally built binary reports. Replacing it with a release
	// would destroy the developer's build and hide which build produced output.
	for _, version := range []string{"dev", "", "local", "1.2.x"} {
		p := Platform{GOOS: OSLinux, GOARCH: "amd64", Version: version, DeviceID: "dev-1"}
		d, err := Decide(p, manifestFor("9.9.9", "", 100, OSLinux, "amd64", FormatTarGz))
		if err != nil {
			t.Fatalf("Decide(%q): %v", version, err)
		}
		if d.Action != ActionRefuseUnknownVersion {
			t.Errorf("version %q: Action = %s (%s), want refuse-unknown-version", version, d.Action, d.Reason)
		}
	}
}

func TestDecideReportsAPlatformWithNoBuild(t *testing.T) {
	p := Platform{GOOS: OSLinux, GOARCH: "arm64", Version: "1.0.0", DeviceID: "dev-1"}
	d, _ := Decide(p, manifestFor("1.1.0", "", 100, OSLinux, "amd64", FormatTarGz))
	if d.Action != ActionRefuseNoArtifact {
		t.Fatalf("Action = %s (%s), want refuse-no-artifact", d.Action, d.Reason)
	}
}

func TestDecideRefusesAPlatformWithNoSelfUpdatePath(t *testing.T) {
	// D26 ships Windows x64 and Linux x64/arm64. macOS has no update path
	// because it has no runner.
	p := Platform{GOOS: OSDarwin, GOARCH: "arm64", Version: "1.0.0", DeviceID: "dev-1"}
	m := &Manifest{Version: "1.1.0", RolloutPercent: 100, Artifacts: []Artifact{{
		OS: OSLinux, Arch: "arm64", Format: FormatTarGz,
		URL: "https://downloads.reelbolt.ai/x", SHA256: strings.Repeat("a", 64), Size: 1,
	}}}
	d, _ := Decide(p, m)
	if d.Action != ActionRefuseUnsupportedPlatform {
		t.Fatalf("Action = %s (%s), want refuse-unsupported-platform", d.Action, d.Reason)
	}
	if !strings.Contains(d.Reason, "D26") {
		t.Errorf("the reason does not name the decision that excludes it: %q", d.Reason)
	}
}

func TestDecideHonoursAnExplicitFormatPreference(t *testing.T) {
	// A package-managed install prefers to be told about the .deb rather than
	// have its files replaced behind the package manager's back.
	m := &Manifest{Version: "1.1.0", RolloutPercent: 100, Artifacts: []Artifact{
		{OS: OSLinux, Arch: "amd64", Format: FormatTarGz, URL: "https://downloads.reelbolt.ai/t", SHA256: strings.Repeat("a", 64), Size: 1},
		{OS: OSLinux, Arch: "amd64", Format: FormatDeb, URL: "https://downloads.reelbolt.ai/d", SHA256: strings.Repeat("b", 64), Size: 1},
	}}
	p := Platform{GOOS: OSLinux, GOARCH: "amd64", Version: "1.0.0", DeviceID: "dev-1",
		Formats: FormatPreference(Install{Managed: true}, OSLinux)}
	d, err := Decide(p, m)
	if err != nil {
		t.Fatalf("Decide: %v", err)
	}
	if d.Artifact.Format != FormatDeb {
		t.Fatalf("Artifact = %s, want the deb for a package-managed install", d.Artifact.Format)
	}
}

func TestFormatPreferencePerInstall(t *testing.T) {
	cases := []struct {
		name    string
		install Install
		goos    string
		want    string
	}{
		{"appimage install", Install{AppImage: true}, OSLinux, FormatAppImage},
		{"tarball install", Install{}, OSLinux, FormatTarGz},
		{"package-managed install", Install{Managed: true}, OSLinux, FormatDeb},
		{"windows", Install{}, OSWindows, FormatExe},
		{"unsupported", Install{}, OSDarwin, ""},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := FormatPreference(tc.install, tc.goos)
			if tc.want == "" {
				if len(got) != 0 {
					t.Fatalf("FormatPreference = %v, want none", got)
				}
				return
			}
			if len(got) == 0 || got[0] != tc.want {
				t.Fatalf("FormatPreference = %v, want %s first", got, tc.want)
			}
		})
	}
}
