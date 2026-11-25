package main

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/runner/internal/update"
)

// runCmd executes one command with its streams captured.
func runCmd(t *testing.T, args ...string) (string, error) {
	t.Helper()
	var out bytes.Buffer
	err := run(args, &out, &out)
	return out.String(), err
}

// mustRun executes a command that is expected to succeed.
func mustRun(t *testing.T, args ...string) string {
	t.Helper()
	out, err := runCmd(t, args...)
	if err != nil {
		t.Fatalf("%v: %v\n%s", args, err, out)
	}
	return out
}

// genKey generates a release key into dir and returns the private key, its
// public half and the stamp the pipeline would store.
func genKey(t *testing.T, dir string) (ed25519.PrivateKey, ed25519.PublicKey, string) {
	t.Helper()
	path := filepath.Join(dir, "release.key")
	out := mustRun(t, "gen-key", "-out", path, "-id", "release-current")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	priv, err := parsePrivateKey(string(raw))
	if err != nil {
		t.Fatalf("the generated key does not parse: %v", err)
	}
	pub := priv.Public().(ed25519.PublicKey)
	stamp := "release-current:" + hex.EncodeToString(pub)
	if !strings.Contains(out, stamp) {
		t.Fatalf("gen-key did not print the public stamp %s:\n%s", stamp, out)
	}
	return priv, pub, stamp
}

// writeArtifact writes a fake built artifact.
func writeArtifact(t *testing.T, dir, name string, body []byte) string {
	t.Helper()
	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, body, 0o755); err != nil {
		t.Fatal(err)
	}
	return path
}

// assembleLinux builds a one-artifact manifest for linux/amd64 and returns the
// manifest path and the artifact path.
func assembleLinux(t *testing.T, dir, base, version string, rollout int) (string, string) {
	t.Helper()
	artifact := writeArtifact(t, dir, "reelbolt-runner-"+version+"-linux-amd64.tar.gz", []byte("runner "+version))
	manifest := filepath.Join(dir, "manifest.json")
	mustRun(t, "assemble",
		"-version", version,
		"-channel", "beta",
		"-rollout-percent", fmt.Sprint(rollout),
		"-url-base", base,
		"-out", manifest,
		"-artifact", "linux/amd64/tarball="+artifact,
	)
	return manifest, artifact
}

func TestGenKeyWritesAPrivateKeyAndPrintsOnlyThePublicOne(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "release.key")
	out := mustRun(t, "gen-key", "-out", path)

	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if perm := info.Mode().Perm(); runtime.GOOS != "windows" && perm != 0o600 { // POSIX modes do not apply on Windows
		t.Fatalf("the private key file is mode %o, want 600", perm)
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	priv, err := parsePrivateKey(string(raw))
	if err != nil {
		t.Fatal(err)
	}
	// The private key must never reach the log: a key printed once is a key in
	// a CI log, a terminal scrollback and a screenshot forever.
	if strings.Contains(out, hex.EncodeToString(priv)) {
		t.Fatal("gen-key printed the private key")
	}
	if !strings.Contains(out, "RUNNER_MANIFEST_SIGNING_KEY") {
		t.Fatal("gen-key did not say where the private key goes")
	}
	if _, err := runCmd(t, "gen-key", "-out", path); err == nil {
		t.Fatal("gen-key overwrote an existing key without -force")
	}
	if _, err := runCmd(t, "gen-key", "-out", path, "-force"); err != nil {
		t.Fatalf("gen-key -force: %v", err)
	}
}

// The stamp strings are the only coupling between this tool and the runner's
// build. A rename on either side would otherwise be found by publishing a
// release no runner accepts, so it is found here instead.
func TestLdflagsNamesTheVariablesTheRunnerActuallyDeclares(t *testing.T) {
	for _, tc := range stampedSymbols {
		src, err := os.ReadFile(tc.file)
		if err != nil {
			t.Fatal(err)
		}
		decl := regexp.MustCompile("(?m)^\\s*(?:var\\s+)?" + regexp.QuoteMeta(tc.name) + "\\s*=")
		if !decl.Match(src) {
			t.Fatalf("%s no longer declares %s, so the -ldflags this tool prints would set nothing", tc.file, tc.name)
		}
	}
	// A library package is addressed by its full import path; a main package is
	// addressed as "main.X" and the bare symbol name is declared in the file.
	for _, v := range []string{currentVar, nextVar, telemetryVar} {
		if !strings.HasPrefix(v, "github.com/vecchiotom/reelbolt/runner/") {
			t.Fatalf("%s is not a package path in this module", v)
		}
	}
	if !strings.HasPrefix(mainVersionVar, "main.") || !strings.HasPrefix(mainFontsetVar, "main.") {
		t.Fatalf("a main package must be addressed as main.X, got %q and %q", mainVersionVar, mainFontsetVar)
	}
}

func TestLdflagsRefusesABuildWithNoReleaseKey(t *testing.T) {
	_, err := runCmd(t, "ldflags", "-version", "1.4.2", "-current-key", "")
	if err == nil {
		t.Fatal("ldflags emitted a build line with no release key, which would ship a runner that can never update")
	}
	if !strings.Contains(err.Error(), "refuses every update") {
		t.Fatalf("the refusal does not explain itself: %v", err)
	}
	// The dry-run escape hatch is explicit, and it cannot be combined with the
	// release tag: a release build that trusts no key is a release nobody can
	// update to.
	if _, err := runCmd(t, "ldflags", "-version", "1.4.2", "-no-release-key", "-release"); err == nil {
		t.Fatal("ldflags stamped a release build with no trust root")
	}
	dry := mustRun(t, "ldflags", "-version", "1.0.0", "-no-release-key")
	if strings.Contains(dry, currentVar) || strings.Contains(dry, "tags=release") {
		t.Fatalf("a dry-run build line carries a trust root or the release tag:\n%s", dry)
	}
	if !strings.Contains(dry, "must not be published") {
		t.Fatalf("the dry-run build line does not warn:\n%s", dry)
	}
	if _, err := runCmd(t, "ldflags", "-version", "not-a-version", "-current-key", "k:"+strings.Repeat("ab", 32)); err == nil {
		t.Fatal("ldflags accepted a version the manifest parser would reject")
	}
	if _, err := runCmd(t, "ldflags", "-version", "1.4.2", "-current-key", "k:short"); err == nil {
		t.Fatal("ldflags accepted a public key that is not 32 bytes")
	}
}

func TestLdflagsEmitsTheStampsABuildNeeds(t *testing.T) {
	_, pub, stamp := genKey(t, t.TempDir())
	next := "release-next:" + hex.EncodeToString(pub)
	out := mustRun(t, "ldflags", "-version", "1.4.2", "-fontset", "v3", "-current-key", stamp, "-next-key", next, "-release")
	for _, want := range []string{
		"ldflags=",
		// A main package is addressed as "main.X", not by its import path: a
		// full path is accepted and silently ignored. See keys.go.
		mainVersionVar + "=1.4.2",
		mainFontsetVar + "=v3",
		currentVar + "=" + stamp,
		nextVar + "=" + next,
		"tags=release",
	} {
		if !strings.Contains(out, want) {
			t.Fatalf("the build line is missing %q:\n%s", want, out)
		}
	}
	// Without -release the development escape hatch stays compiled in, which is
	// what a local dry run needs and what a published build must not have.
	out = mustRun(t, "ldflags", "-version", "1.4.2", "-current-key", stamp)
	if strings.Contains(out, "tags=release") {
		t.Fatal("ldflags emitted -tags release without being asked")
	}
}

func TestAssembleHashesTheFilesItWasGiven(t *testing.T) {
	dir := t.TempDir()
	body := []byte("a runner build")
	artifact := writeArtifact(t, dir, "reelbolt-runner-1.4.2-linux-amd64.tar.gz", body)
	manifest := filepath.Join(dir, "manifest.json")
	mustRun(t, "assemble",
		"-version", "1.4.2",
		"-channel", "stable",
		"-rollout-percent", "10",
		"-published-at", "2026-10-05T09:00:00Z",
		"-min-supported", "1.2.0",
		"-url-base", "https://downloads.reelbolt.ai/runner/1.4.2",
		"-out", manifest,
		"-artifact", "linux/amd64/tarball="+artifact,
	)

	raw, err := os.ReadFile(manifest)
	if err != nil {
		t.Fatal(err)
	}
	// The runner's own parser is the judge: the pipeline does not get to invent
	// a manifest format.
	m, err := update.ParseManifest(raw)
	if err != nil {
		t.Fatalf("the runner refuses the manifest this tool wrote: %v", err)
	}
	if m.Version != "1.4.2" || m.Channel != "stable" || m.RolloutPercent != 10 || m.MinSupported != "1.2.0" {
		t.Fatalf("unexpected manifest: %+v", m)
	}
	wantSum, err := update.HashFile(artifact)
	if err != nil {
		t.Fatal(err)
	}
	got := m.Artifacts[0]
	if got.Size != int64(len(body)) || got.SHA256 != wantSum {
		t.Fatalf("artifact is %d/%s, want %d/%s", got.Size, got.SHA256, len(body), wantSum)
	}
	if got.URL != "https://downloads.reelbolt.ai/runner/1.4.2/reelbolt-runner-1.4.2-linux-amd64.tar.gz" {
		t.Fatalf("artifact url is %s", got.URL)
	}
	again, err := os.ReadFile(manifest)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(raw, again) {
		t.Fatal("the manifest changed between reads")
	}
}

func TestAssembleRefusesWhatTheRunnerWouldRefuse(t *testing.T) {
	dir := t.TempDir()
	artifact := writeArtifact(t, dir, "reelbolt-runner-1.4.2-linux-amd64.tar.gz", []byte("x"))
	base := []string{"assemble", "-version", "1.4.2", "-channel", "beta", "-rollout-percent", "100", "-out", filepath.Join(dir, "m.json")}

	for _, tc := range []struct{ name, spec, urlBase, want string }{
		{"macOS is not published", "darwin/arm64/tarball=" + artifact, "https://downloads.reelbolt.ai/runner/1.4.2", "D26"},
		{"a Windows artifact cannot be an AppImage", "windows/amd64/appimage=" + artifact, "https://downloads.reelbolt.ai/runner/1.4.2", "Linux-only"},
		{"an unknown format", "linux/amd64/zip=" + artifact, "https://downloads.reelbolt.ai/runner/1.4.2", "unknown format"},
		{"an unknown os", "plan9/amd64/tarball=" + artifact, "https://downloads.reelbolt.ai/runner/1.4.2", "unknown os"},
		{"a plaintext public host", "linux/amd64/tarball=" + artifact, "http://downloads.reelbolt.ai/runner/1.4.2", "must be https"},
		{"a missing file", "linux/amd64/tarball=" + filepath.Join(dir, "nope"), "https://downloads.reelbolt.ai/runner/1.4.2", missingFileMessage()},
	} {
		args := append(append([]string{}, base...), "-url-base", tc.urlBase, "-artifact", tc.spec)
		_, err := runCmd(t, args...)
		if err == nil {
			t.Fatalf("%s: assemble accepted it", tc.name)
		}
		if !strings.Contains(err.Error(), tc.want) {
			t.Fatalf("%s: error %q does not mention %q", tc.name, err, tc.want)
		}
	}

	// A file name that is not URL-safe would publish a URL that means something
	// other than what the manifest says.
	odd := writeArtifact(t, dir, "reel bolt.tar.gz", []byte("x"))
	args := append(append([]string{}, base...), "-url-base", "https://downloads.reelbolt.ai/runner/1.4.2", "-artifact", "linux/amd64/tarball="+odd)
	if _, err := runCmd(t, args...); err == nil || !strings.Contains(err.Error(), "URL-safe") {
		t.Fatalf("assemble accepted a file name that is not URL-safe: %v", err)
	}

	// The same (os, arch, format) twice is a manifest the runner refuses.
	args = append(append([]string{}, base...), "-url-base", "https://downloads.reelbolt.ai/runner/1.4.2",
		"-artifact", "linux/amd64/tarball="+artifact, "-artifact", "linux/amd64/tarball="+artifact)
	if _, err := runCmd(t, args...); err == nil || !strings.Contains(err.Error(), "bug in reelbolt-manifest") {
		t.Fatalf("assemble accepted a duplicate artifact: %v", err)
	}

	// A rollout outside 0-100.
	args = []string{"assemble", "-version", "1.4.2", "-channel", "beta", "-rollout-percent", "101",
		"-out", filepath.Join(dir, "m2.json"), "-url-base", "https://downloads.reelbolt.ai/runner/1.4.2",
		"-artifact", "linux/amd64/tarball=" + artifact}
	if _, err := runCmd(t, args...); err == nil {
		t.Fatal("assemble accepted a rollout above 100")
	}
}

func TestSignAndVerifyRoundTrip(t *testing.T) {
	dir := t.TempDir()
	_, pub, stamp := genKey(t, dir)
	manifest, _ := assembleLinux(t, dir, "https://downloads.reelbolt.ai/runner/1.4.2", "1.4.2", 100)

	before, err := os.ReadFile(manifest)
	if err != nil {
		t.Fatal(err)
	}
	mustRun(t, "sign", "-manifest", manifest, "-key-file", filepath.Join(dir, "release.key"), "-expect-public", stamp)
	after, err := os.ReadFile(manifest)
	if err != nil {
		t.Fatal(err)
	}
	// The signature covers the bytes. Anything that rewrites the document after
	// signing invalidates it, so signing must not touch it.
	if !bytes.Equal(before, after) {
		t.Fatal("sign rewrote the manifest it signed")
	}

	out := mustRun(t, "verify", "-manifest", manifest, "-public-key", stamp, "-expect-version", "1.4.2", "-expect-channel", "beta")
	if !strings.Contains(out, "version 1.4.2") {
		t.Fatalf("verify output: %s", out)
	}

	// The runner agrees with this tool about what was signed.
	raw, _ := os.ReadFile(manifest)
	sig, err := os.ReadFile(manifest + ".sig")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := update.VerifyManifest(raw, sig, update.KeySet{{ID: "k", Public: pub}}); err != nil {
		t.Fatalf("the runner rejects a manifest this tool signed: %v", err)
	}
}

func TestVerifyRefusesATamperedManifest(t *testing.T) {
	dir := t.TempDir()
	_, _, stamp := genKey(t, dir)
	manifest, _ := assembleLinux(t, dir, "https://downloads.reelbolt.ai/runner/1.4.2", "1.4.2", 100)
	mustRun(t, "sign", "-manifest", manifest, "-key-file", filepath.Join(dir, "release.key"))

	// One byte appended after signing is the attack the detached signature
	// exists to stop.
	f, err := os.OpenFile(manifest, os.O_APPEND|os.O_WRONLY, 0)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := f.Write([]byte("\n")); err != nil {
		t.Fatal(err)
	}
	_ = f.Close()

	_, err = runCmd(t, "verify", "-manifest", manifest, "-public-key", stamp)
	if err == nil {
		t.Fatal("verify accepted a manifest that changed after signing")
	}
	if !errors.Is(err, update.ErrSignature) {
		t.Fatalf("want ErrSignature, got %v", err)
	}
}

func TestVerifyRefusesAnotherKeyAndTheWrongVersion(t *testing.T) {
	dir := t.TempDir()
	_, _, stamp := genKey(t, dir)
	manifest, _ := assembleLinux(t, dir, "https://downloads.reelbolt.ai/runner/1.4.2", "1.4.2", 100)
	mustRun(t, "sign", "-manifest", manifest, "-key-file", filepath.Join(dir, "release.key"))

	if _, err := runCmd(t, "verify", "-manifest", manifest, "-public-key", stamp, "-expect-version", "1.4.3"); err == nil {
		t.Fatal("verify accepted the wrong version")
	}
	if _, err := runCmd(t, "verify", "-manifest", manifest, "-public-key", stamp, "-expect-channel", "stable"); err == nil {
		t.Fatal("verify accepted the wrong channel")
	}
	// A different key must not verify: this is the check that makes "signed by
	// the key compiled into the build" mean something.
	otherPub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	_, err = runCmd(t, "verify", "-manifest", manifest, "-public-key", "other:"+hex.EncodeToString(otherPub))
	if !errors.Is(err, update.ErrSignature) {
		t.Fatalf("want ErrSignature for a foreign key, got %v", err)
	}
}

// Signing with a key other than the one the release stamps into the build is
// the failure that would otherwise be discovered by every user at once, so the
// pipeline is made to state which key it expects.
func TestSignRefusesAKeyTheReleaseDoesNotCarry(t *testing.T) {
	dir := t.TempDir()
	_, _, stamp := genKey(t, dir)
	manifest, _ := assembleLinux(t, dir, "https://downloads.reelbolt.ai/runner/1.4.2", "1.4.2", 100)
	otherPub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	_, err = runCmd(t, "sign", "-manifest", manifest,
		"-key-file", filepath.Join(dir, "release.key"),
		"-expect-public", "release-current:"+hex.EncodeToString(otherPub))
	if err == nil {
		t.Fatal("sign signed with a key the build does not carry")
	}
	if !strings.Contains(err.Error(), "nothing was written") {
		t.Fatalf("the refusal does not say it wrote nothing: %v", err)
	}
	if _, err := os.Stat(manifest + ".sig"); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("a rejected signature was written anyway")
	}
	if _, err := runCmd(t, "verify", "-manifest", manifest, "-public-key", stamp); err == nil {
		t.Fatal("the manifest verifies before it was signed")
	}
}

func TestSignRefusesToSignSomethingTheRunnerWouldRefuse(t *testing.T) {
	dir := t.TempDir()
	genKey(t, dir)
	broken := filepath.Join(dir, "manifest.json")
	if err := os.WriteFile(broken, []byte(`{"version":"1.4.2","rolloutPercent":10,"artifacts":[]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := runCmd(t, "sign", "-manifest", broken, "-key-file", filepath.Join(dir, "release.key")); err == nil {
		t.Fatal("sign signed a manifest with no artifacts")
	}
}

func TestSignRefusesWithoutAKey(t *testing.T) {
	dir := t.TempDir()
	manifest, _ := assembleLinux(t, dir, "https://downloads.reelbolt.ai/runner/1.4.2", "1.4.2", 100)
	if _, err := runCmd(t, "sign", "-manifest", manifest); err == nil {
		t.Fatal("sign signed without a key")
	}
	_, err := runCmd(t, "sign", "-manifest", manifest, "-key-env", "REELBOLT_TEST_MISSING_KEY")
	if err == nil || !strings.Contains(err.Error(), "REELBOLT_TEST_MISSING_KEY") {
		t.Fatalf("sign did not name the missing environment variable: %v", err)
	}
}

// The pipeline's post-upload check: the bytes the world can download are the
// bytes the manifest describes. A digest only ever compared against a file in
// the build directory does not tell you that.
func TestVerifyDownloadsAndHashesThePublishedArtifacts(t *testing.T) {
	dir := t.TempDir()
	_, _, stamp := genKey(t, dir)
	body := []byte("the artifact CI built")
	name := "reelbolt-runner-1.4.2-linux-amd64.tar.gz"
	artifact := writeArtifact(t, dir, name, body)

	served := body
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if filepath.Base(r.URL.Path) != name {
			http.NotFound(w, r)
			return
		}
		_, _ = w.Write(served)
	}))
	defer srv.Close()

	manifest := filepath.Join(dir, "manifest.json")
	mustRun(t, "assemble",
		"-version", "1.4.2",
		"-channel", "beta",
		"-rollout-percent", "100",
		"-url-base", srv.URL,
		"-out", manifest,
		"-artifact", "linux/amd64/tarball="+artifact,
	)
	mustRun(t, "sign", "-manifest", manifest, "-key-file", filepath.Join(dir, "release.key"))

	out := mustRun(t, "verify", "-manifest", manifest, "-public-key", stamp, "-download", "-json")
	if !strings.Contains(out, "artifactVerified") {
		t.Fatalf("verify -json does not report the artifact check: %s", out)
	}

	// The bucket serving something else — a truncated upload, an overwritten
	// object, an artifact from a different build — must fail here and not on a
	// user's machine.
	served = []byte("the artifact someone else built")
	_, err := runCmd(t, "verify", "-manifest", manifest, "-public-key", stamp, "-download")
	if err == nil {
		t.Fatal("verify -download accepted an artifact the digest does not describe")
	}
	if !errors.Is(err, update.ErrDigest) {
		t.Fatalf("want ErrDigest, got %v", err)
	}
}

func TestVerifyRefusesAMissingSignature(t *testing.T) {
	dir := t.TempDir()
	_, _, stamp := genKey(t, dir)
	manifest, _ := assembleLinux(t, dir, "https://downloads.reelbolt.ai/runner/1.4.2", "1.4.2", 100)
	if _, err := runCmd(t, "verify", "-manifest", manifest, "-public-key", stamp); err == nil {
		t.Fatal("verify accepted a manifest with no signature file")
	}
}

func TestParsePrivateKeyAcceptsASeedOrAFullKey(t *testing.T) {
	_, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	for name, encoded := range map[string]string{
		"hex":         hex.EncodeToString(priv),
		"seed in hex": hex.EncodeToString(priv.Seed()),
		"base64":      base64.StdEncoding.EncodeToString(priv),
		"seed base64": base64.StdEncoding.EncodeToString(priv.Seed()),
		"padded":      "  " + hex.EncodeToString(priv) + "\n",
	} {
		got, err := parsePrivateKey(encoded)
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if !samePublicKey(got.Public().(ed25519.PublicKey), priv.Public().(ed25519.PublicKey)) {
			t.Fatalf("%s: the parsed key is a different key", name)
		}
	}
	if _, err := parsePrivateKey(""); err == nil {
		t.Fatal("an empty key parsed")
	}
	if _, err := parsePrivateKey(hex.EncodeToString([]byte("too short"))); err == nil {
		t.Fatal("a short key parsed")
	}
}

func TestUnknownCommandAndUsage(t *testing.T) {
	if _, err := runCmd(t, "nonsense"); err == nil {
		t.Fatal("an unknown command was accepted")
	}
	if out, err := runCmd(t); err == nil || !strings.Contains(err.Error(), "no command given") {
		t.Fatalf("no command: %v / %q", err, out)
	}
	if out := mustRun(t, "help"); !strings.Contains(out, "gen-key") {
		t.Fatalf("help does not list the commands: %s", out)
	}
}

// missingFileMessage is the OS's own wording for "that path does not exist".
func missingFileMessage() string {
	if runtime.GOOS == "windows" {
		return "cannot find the file specified"
	}
	return "no such file"
}
