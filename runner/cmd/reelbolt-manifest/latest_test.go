package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// releaseFixture builds a manifest with one artifact per platform, signs it, and
// returns the paths.
func releaseFixture(t *testing.T, dir string) (manifest, stamp string) {
	t.Helper()
	_, _, stamp = genKey(t, dir)
	writeArtifact(t, dir, "reelbolt-runner-1.4.2-windows-amd64.msi", []byte("an msi"))
	writeArtifact(t, dir, "reelbolt-runner-1.4.2-windows-amd64.exe", []byte("an exe"))
	writeArtifact(t, dir, "reelbolt-runner-1.4.2-linux-amd64", []byte("a raw linux binary"))
	writeArtifact(t, dir, "reelbolt-runner-1.4.2-linux-amd64.AppImage", []byte("an appimage"))
	writeArtifact(t, dir, "reelbolt-runner-1.4.2-linux-amd64.deb", []byte("a deb"))
	writeArtifact(t, dir, "reelbolt-runner-1.4.2-linux-arm64.AppImage", []byte("an arm appimage"))
	manifest = filepath.Join(dir, "manifest.json")
	mustRun(t, "assemble",
		"-version", "1.4.2",
		"-channel", "beta",
		"-rollout-percent", "25",
		"-published-at", "2026-10-05T09:00:00Z",
		"-url-base", "https://downloads.reelbolt.ai/runner/1.4.2",
		"-out", manifest,
		"-artifact", "windows/amd64/msi="+filepath.Join(dir, "reelbolt-runner-1.4.2-windows-amd64.msi"),
		"-artifact", "windows/amd64/exe="+filepath.Join(dir, "reelbolt-runner-1.4.2-windows-amd64.exe"),
		"-artifact", "linux/amd64/tarball="+filepath.Join(dir, "reelbolt-runner-1.4.2-linux-amd64"),
		"-artifact", "linux/amd64/appimage="+filepath.Join(dir, "reelbolt-runner-1.4.2-linux-amd64.AppImage"),
		"-artifact", "linux/amd64/deb="+filepath.Join(dir, "reelbolt-runner-1.4.2-linux-amd64.deb"),
		"-artifact", "linux/arm64/appimage="+filepath.Join(dir, "reelbolt-runner-1.4.2-linux-arm64.AppImage"),
	)
	mustRun(t, "sign", "-manifest", manifest, "-key-file", filepath.Join(dir, "release.key"))
	return manifest, stamp
}

func TestLatestDescribesWhatTheDownloadPageNeeds(t *testing.T) {
	dir := t.TempDir()
	manifest, stamp := releaseFixture(t, dir)
	out := filepath.Join(dir, "latest.json")
	uploads := filepath.Join(dir, "uploads.txt")
	mustRun(t, "latest",
		"-manifest", manifest,
		"-public-key", stamp,
		"-url-base", "https://downloads.reelbolt.ai/runner/beta/latest",
		"-manifest-url", "https://downloads.reelbolt.ai/runner/beta/manifest.json",
		"-checksums-url", "https://downloads.reelbolt.ai/runner/beta/latest/SHA256SUMS",
		"-out", out,
		"-uploads-out", uploads,
		"-dist", dir,
	)

	raw, err := os.ReadFile(out)
	if err != nil {
		t.Fatal(err)
	}
	var doc latestDocument
	if err := json.Unmarshal(raw, &doc); err != nil {
		t.Fatalf("latest.json does not parse: %v", err)
	}
	if doc.Version != "1.4.2" || doc.Channel != "beta" || doc.ManifestSHA256 == "" {
		t.Fatalf("unexpected document: %+v", doc)
	}
	if doc.ChecksumsURL != "https://downloads.reelbolt.ai/runner/beta/latest/SHA256SUMS" {
		t.Fatalf("checksumsUrl not recorded: %q", doc.ChecksumsURL)
	}
	if len(doc.Artifacts) != 6 {
		t.Fatalf("want 6 artifacts, got %d", len(doc.Artifacts))
	}

	byFormat := map[string]latestArtifact{}
	for _, a := range doc.Artifacts {
		byFormat[a.Format] = a
		if a.SHA256 != strings.ToLower(a.SHA256) {
			t.Fatalf("%s: sha256 is not lowercase hex", a.Format)
		}
		if !strings.HasPrefix(a.URL, "https://downloads.reelbolt.ai/runner/beta/latest/") {
			t.Fatalf("%s: url %q is not under the stable base", a.Format, a.URL)
		}
	}
	// The raw executable behind the manifest's tarball format is an update
	// payload, and offering it as a download would hand someone a binary with no
	// bundled ffmpeg and no PATH entry.
	if got := byFormat["tarball"]; got.Kind != "update-payload" || got.Recommended {
		t.Fatalf("the tarball payload is presented as a download: %+v", got)
	}
	if got := byFormat["msi"]; got.Kind != "installer" || !got.Recommended || got.Platform != "windows-x64" {
		t.Fatalf("the Windows installer is not the recommended download: %+v", got)
	}
	if got := byFormat["appimage"]; !got.Recommended {
		t.Fatalf("the AppImage is not the recommended Linux download: %+v", got)
	}
	// Every platform the release targets is represented.
	platforms := map[string]bool{}
	for _, a := range doc.Artifacts {
		platforms[a.Platform] = true
	}
	for _, want := range []string{"windows-x64", "linux-x64", "linux-arm64"} {
		if !platforms[want] {
			t.Fatalf("no artifact for %s", want)
		}
	}
	if strings.Contains(string(raw), "darwin") {
		t.Fatal("latest.json mentions macOS, which D26 does not ship")
	}

	// The upload list is what the stable URLs need, and it must name real files.
	list, err := os.ReadFile(uploads)
	if err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(strings.TrimSpace(string(list)), "\n")
	if len(lines) != 6 {
		t.Fatalf("want 6 objects to copy, got %d", len(lines))
	}
	for _, line := range lines {
		parts := strings.Split(line, "\t")
		if len(parts) != 2 {
			t.Fatalf("bad upload line %q", line)
		}
		if _, err := os.Stat(parts[0]); err != nil {
			t.Fatalf("upload list names a file that does not exist: %v", err)
		}
		if strings.Contains(parts[1], "/") {
			t.Fatalf("object key %q must be a plain file name under the latest/ prefix", parts[1])
		}
	}
}

// A convenience document must never be produced from a manifest nobody verified:
// that is the one way it could describe artifacts the release key did not sign.
func TestLatestRefusesAnUnverifiedManifest(t *testing.T) {
	dir := t.TempDir()
	manifest, stamp := releaseFixture(t, dir)
	out := filepath.Join(dir, "latest.json")

	if _, err := runCmd(t, "latest",
		"-manifest", manifest, "-public-key", stamp,
		"-url-base", "https://downloads.reelbolt.ai/runner/beta/latest", "-out", out); err != nil {
		t.Fatalf("a genuine manifest was refused: %v", err)
	}
	_ = os.Remove(out)

	// Tamper with the manifest after signing.
	f, err := os.OpenFile(manifest, os.O_APPEND|os.O_WRONLY, 0)
	if err != nil {
		t.Fatal(err)
	}
	_, _ = f.Write([]byte(" "))
	_ = f.Close()

	if _, err := runCmd(t, "latest",
		"-manifest", manifest, "-public-key", stamp,
		"-url-base", "https://downloads.reelbolt.ai/runner/beta/latest", "-out", out); err == nil {
		t.Fatal("latest described a manifest whose signature does not verify")
	}
	if _, err := os.Stat(out); !os.IsNotExist(err) {
		t.Fatal("latest wrote a document for an unverified manifest")
	}
}

// The stable URLs are copies of files that must exist. Publishing a URL for an
// artifact that was never built is how a download page ends up serving a 404.
func TestLatestRefusesAnArtifactThatWasNotBuilt(t *testing.T) {
	dir := t.TempDir()
	manifest, stamp := releaseFixture(t, dir)
	missing := t.TempDir()
	_, err := runCmd(t, "latest",
		"-manifest", manifest, "-public-key", stamp,
		"-url-base", "https://downloads.reelbolt.ai/runner/beta/latest",
		"-out", filepath.Join(dir, "latest.json"),
		"-dist", missing)
	if err == nil {
		t.Fatal("latest published a stable URL for an artifact that is not in the dist directory")
	}
	if !strings.Contains(err.Error(), "was not built") {
		t.Fatalf("the refusal does not explain itself: %v", err)
	}
}
