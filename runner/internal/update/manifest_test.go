package update

import (
	"strings"
	"testing"
)

func TestParseVersionAndCompare(t *testing.T) {
	cases := []struct {
		a, b string
		want int
	}{
		{"1.0.0", "1.0.0", 0},
		{"v1.0.0", "1.0.0", 0},
		{"1.0.1", "1.0.0", 1},
		{"1.0.0", "1.0.1", -1},
		{"1.10.0", "1.9.0", 1},
		{"2.0", "1.9.9", 1},
		{"1.0.0-beta.1", "1.0.0", -1},
		{"1.0.0", "1.0.0-beta.1", 1},
		{"1.0.0-beta.2", "1.0.0-beta.1", 1},
	}
	for _, tc := range cases {
		got, err := CompareVersions(tc.a, tc.b)
		if err != nil {
			t.Fatalf("CompareVersions(%q, %q): %v", tc.a, tc.b, err)
		}
		if got != tc.want {
			t.Errorf("CompareVersions(%q, %q) = %d, want %d", tc.a, tc.b, got, tc.want)
		}
	}
	for _, bad := range []string{"", "dev", "1.x.0", "one.two", "1..2"} {
		if _, err := ParseVersion(bad); err == nil {
			t.Errorf("ParseVersion(%q) accepted a version that is not one", bad)
		}
	}
}

func TestNewerIsStrict(t *testing.T) {
	same, err := Newer("1.2.3", "1.2.3")
	if err != nil || same {
		t.Errorf("Newer on equal versions = %v, %v; want false", same, err)
	}
	up, err := Newer("1.2.4", "1.2.3")
	if err != nil || !up {
		t.Errorf("Newer(1.2.4, 1.2.3) = %v, %v; want true", up, err)
	}
	down, err := Newer("1.2.3", "1.2.4")
	if err != nil || down {
		t.Errorf("Newer on an older candidate = %v, %v; want false", down, err)
	}
	if _, err := Newer("dev", "1.2.3"); err == nil {
		t.Error("Newer accepted an unparseable version")
	}
}

func TestRolloutBucketing(t *testing.T) {
	// A bucket is stable: a device cannot be re-rolled into a release by
	// retrying, which is the property the plan asks for.
	first := Bucket("device-abc")
	for i := 0; i < 100; i++ {
		if got := Bucket("device-abc"); got != first {
			t.Fatalf("Bucket is not stable: %d then %d", first, got)
		}
	}
	if first < 0 || first > 99 {
		t.Fatalf("Bucket = %d, outside 0-99", first)
	}
	// The domain prefix is 0-terminated, so no two ids can be shifted into one
	// another and land in the same bucket by construction.
	if Bucket("a") == Bucket("a"+string(rune(0))+"b") {
		t.Error("a device id containing a NUL is not distinguished from its parts")
	}
	// 0% admits nobody, 100% admits everybody, and the boundary is inclusive.
	if Admitted("device-abc", 0) {
		t.Error("a 0% rollout admitted a device")
	}
	if !Admitted("device-abc", 100) {
		t.Error("a 100% rollout held a device back")
	}
	if Admitted("device-abc", first) {
		t.Errorf("a rollout of %d%% admitted bucket %d, which is not below the cut", first, first)
	}
	if !Admitted("device-abc", first+1) {
		t.Errorf("a rollout of %d%% held back bucket %d, which is below the cut", first+1, first)
	}
	// Distinct devices spread across the range rather than piling into one
	// bucket, which is what makes a staged rollout meaningful.
	seen := map[int]int{}
	for i := 0; i < 2000; i++ {
		seen[Bucket("device-"+itoa(i))]++
	}
	if len(seen) < 90 {
		t.Errorf("2000 devices landed in only %d buckets", len(seen))
	}
}

func TestParseManifestRejectsMalformedDocuments(t *testing.T) {
	artifact := func() Artifact {
		return Artifact{
			OS: OSLinux, Arch: "amd64", Format: FormatTarGz,
			URL:    "https://downloads.reelbolt.ai/runner/x.tar.gz",
			SHA256: strings.Repeat("a", 64), Size: 1024,
		}
	}
	base := func() Manifest {
		return Manifest{Version: "1.2.3", RolloutPercent: 100, Artifacts: []Artifact{artifact()}}
	}

	cases := map[string]func(*Manifest){
		"no artifacts":        func(m *Manifest) { m.Artifacts = nil },
		"unparseable version": func(m *Manifest) { m.Version = "dev" },
		"bad minSupported":    func(m *Manifest) { m.MinSupported = "soon" },
		"rollout over 100":    func(m *Manifest) { m.RolloutPercent = 101 },
		"negative rollout":    func(m *Manifest) { m.RolloutPercent = -1 },
		"short digest":        func(m *Manifest) { m.Artifacts[0].SHA256 = "abc" },
		"non-hex digest":      func(m *Manifest) { m.Artifacts[0].SHA256 = strings.Repeat("z", 64) },
		"zero size":           func(m *Manifest) { m.Artifacts[0].Size = 0 },
		"oversize":            func(m *Manifest) { m.Artifacts[0].Size = MaxArtifactBytes + 1 },
		"no url":              func(m *Manifest) { m.Artifacts[0].URL = "" },
		"non-http url":        func(m *Manifest) { m.Artifacts[0].URL = "file:///etc/passwd" },
		"unknown os":          func(m *Manifest) { m.Artifacts[0].OS = "plan9" },
		"darwin is not shipped": func(m *Manifest) {
			m.Artifacts[0].OS = OSDarwin
			m.Artifacts[0].Format = FormatTarGz
		},
		"msi on linux":    func(m *Manifest) { m.Artifacts[0].Format = FormatMSI },
		"appimage on win": func(m *Manifest) { m.Artifacts[0].OS = OSWindows },
		"unknown format":  func(m *Manifest) { m.Artifacts[0].Format = "zipslip" },
		"duplicate entry": func(m *Manifest) { m.Artifacts = append(m.Artifacts, artifact()) },
	}
	for name, mutate := range cases {
		t.Run(name, func(t *testing.T) {
			m := base()
			mutate(&m)
			raw, err := marshalJSON(m)
			if err != nil {
				t.Fatalf("marshal: %v", err)
			}
			if _, err := ParseManifest(raw); err == nil {
				t.Fatalf("ParseManifest accepted %s", name)
			}
		})
	}

	m := base()
	raw, err := marshalJSON(m)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	if _, err := ParseManifest(raw); err != nil {
		t.Fatalf("ParseManifest rejected a valid manifest: %v", err)
	}
}

func TestParseManifestRejectsUnknownFields(t *testing.T) {
	raw := []byte("{\"version\":\"1.0.0\",\"rolloutPercent\":100,\"artifacts\":[],\"surprise\":true}")
	if _, err := ParseManifest(raw); err == nil {
		t.Fatal("an unknown manifest field was accepted; a format change must be refused, not guessed at")
	}
}

func TestParseManifestRejectsOversizeInput(t *testing.T) {
	raw := make([]byte, MaxManifestBytes+1)
	for i := range raw {
		raw[i] = ' '
	}
	if _, err := ParseManifest(raw); err == nil {
		t.Fatal("an oversized manifest was accepted")
	}
}

func TestArtifactSelectionPrefersTheFirstFormatThatExists(t *testing.T) {
	m := Manifest{
		Version: "1.0.0",
		Artifacts: []Artifact{
			{OS: OSLinux, Arch: "amd64", Format: FormatDeb, URL: "https://e/x", SHA256: strings.Repeat("a", 64), Size: 1},
			{OS: OSLinux, Arch: "amd64", Format: FormatAppImage, URL: "https://e/y", SHA256: strings.Repeat("b", 64), Size: 1},
			{OS: OSWindows, Arch: "amd64", Format: FormatExe, URL: "https://e/z", SHA256: strings.Repeat("c", 64), Size: 1},
		},
	}
	got, ok := m.Artifact(OSLinux, "amd64", FormatAppImage, FormatDeb)
	if !ok || got.Format != FormatAppImage {
		t.Errorf("Artifact = %v, %v; want the AppImage first", got.Format, ok)
	}
	got, ok = m.Artifact(OSLinux, "amd64", FormatTarGz, FormatDeb)
	if !ok || got.Format != FormatDeb {
		t.Errorf("Artifact = %v, %v; want the deb when nothing earlier exists", got.Format, ok)
	}
	if _, ok := m.Artifact(OSLinux, "arm64"); ok {
		t.Error("Artifact returned a build for an arch the manifest does not list")
	}
}

func TestParseChannel(t *testing.T) {
	//nolint:gocritic // " beta " is a deliberate whitespace-normalisation case
	for in, want := range map[string]string{"": ChannelStable, "stable": ChannelStable, "BETA": ChannelBeta, " beta ": ChannelBeta} {
		got, err := ParseChannel(in)
		if err != nil {
			t.Fatalf("ParseChannel(%q): %v", in, err)
		}
		if got != want {
			t.Errorf("ParseChannel(%q) = %q, want %q", in, got, want)
		}
	}
	for _, bad := range []string{"../stable", "stable/../../etc", "nightly", "a b"} {
		if _, err := ParseChannel(bad); err == nil {
			t.Errorf("ParseChannel(%q) was accepted; a channel is a path element", bad)
		}
	}
}
