package main

import (
	"context"
	"crypto/ed25519"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/update"
)

// artifactList collects repeated -artifact flags.
type artifactList []string

func (a *artifactList) String() string { return strings.Join(*a, ",") }

func (a *artifactList) Set(v string) error {
	*a = append(*a, v)
	return nil
}

// fileNamePattern is what an artifact's file name may look like. The manifest
// carries a URL and the URL is built from this name, so a name with a space, a
// quote or a percent sign is refused here rather than published as a URL that
// means something other than what it says.
var fileNamePattern = regexp.MustCompile("^[A-Za-z0-9][A-Za-z0-9._+-]*$")

// cmdAssemble hashes the artifacts CI built and writes the manifest document.
//
// It hashes the files itself, on purpose. A digest the pipeline computes
// somewhere else and pastes in is a digest nobody checked against the bytes
// that will actually be uploaded; here the size and the SHA-256 in the manifest
// are read off the same file the upload step sends, in the same job.
func cmdAssemble(args []string, stdout io.Writer) error {
	var (
		version   string
		channel   string
		rollout   int
		minSup    string
		published string
		notes     string
		urlBase   string
		outPath   string
		artifacts artifactList
	)
	fs := newFlagSet("assemble",
		"Hash the built artifacts and write the manifest document.\n\n"+
			"A manifest the runner would refuse is a release nobody can install, so the\n"+
			"document is parsed back through the runner's own parser before it is written.", stdout)
	fs.StringVar(&version, "version", "", "the release version (required)")
	fs.StringVar(&channel, "channel", "", "the channel this is published to: stable or beta (required)")
	fs.IntVar(&rollout, "rollout-percent", -1, "how much of the fleet this is offered to, 0-100 (required)")
	fs.StringVar(&minSup, "min-supported", "", "the oldest version the platform still supports")
	fs.StringVar(&published, "published-at", "", "RFC 3339 timestamp; default is now, UTC")
	fs.StringVar(&notes, "notes", "", "free text shown to a user who asks what changed")
	fs.StringVar(&urlBase, "url-base", "", "the URL prefix the artifacts are served from (required)")
	fs.StringVar(&outPath, "out", "manifest.json", "where to write the manifest document")
	fs.Var(&artifacts, "artifact", "os/arch/format=path, repeatable (required); the URL is <url-base>/<basename of path>")
	if err := fs.Parse(args); err != nil {
		return err
	}

	if _, err := update.ParseVersion(version); err != nil {
		return fmt.Errorf("-version: %w", err)
	}
	chanName, err := update.ParseChannel(channel)
	if err != nil {
		return err
	}
	if rollout < 0 || rollout > 100 {
		return fmt.Errorf("-rollout-percent %d is outside 0-100", rollout)
	}
	if minSup != "" {
		if _, err := update.ParseVersion(minSup); err != nil {
			return fmt.Errorf("-min-supported: %w", err)
		}
	}
	if published == "" {
		published = time.Now().UTC().Format(time.RFC3339)
	}
	if _, err := time.Parse(time.RFC3339, published); err != nil {
		return fmt.Errorf("-published-at %q is not RFC 3339: %w", published, err)
	}
	if err := validateURLBase(urlBase); err != nil {
		return err
	}
	if len(artifacts) == 0 {
		return errors.New("at least one -artifact os/arch/format=path is required")
	}

	m := &update.Manifest{
		Version:        version,
		MinSupported:   minSup,
		Channel:        chanName,
		RolloutPercent: rollout,
		PublishedAt:    published,
		Notes:          notes,
	}
	for _, spec := range artifacts {
		a, err := artifactFrom(spec, urlBase)
		if err != nil {
			return err
		}
		m.Artifacts = append(m.Artifacts, a)
	}

	raw, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return fmt.Errorf("encode the manifest: %w", err)
	}
	raw = append(raw, '\n')

	// The document is read back through the runner's parser. It costs nothing,
	// and it is the only thing standing between a typo in this tool and a
	// published release that every runner refuses.
	if _, err := update.ParseManifest(raw); err != nil {
		return fmt.Errorf("the manifest this tool just built is not one the runner accepts, which is a bug in reelbolt-manifest: %w", err)
	}
	if err := os.WriteFile(outPath, raw, 0o644); err != nil {
		return fmt.Errorf("write %s: %w", outPath, err)
	}

	fmt.Fprintf(stdout, "manifest %s: version %s channel %s rollout %d%%\n", outPath, version, chanName, rollout)
	for _, a := range m.Artifacts {
		fmt.Fprintf(stdout, "  %-22s %10d bytes  %s\n", a.Key(), a.Size, a.SHA256)
	}
	fmt.Fprintf(stdout, "\nnext: reelbolt-manifest sign -manifest %s\n", outPath)
	return nil
}

// artifactFrom hashes one built file and describes it the way the manifest
// wants.
func artifactFrom(spec, urlBase string) (update.Artifact, error) {
	key, path, ok := strings.Cut(spec, "=")
	if !ok || strings.TrimSpace(path) == "" {
		return update.Artifact{}, fmt.Errorf("-artifact %q is not os/arch/format=path", spec)
	}
	parts := strings.Split(strings.TrimSpace(key), "/")
	if len(parts) != 3 {
		return update.Artifact{}, fmt.Errorf("-artifact %q: the left of = must be os/arch/format", spec)
	}
	a := update.Artifact{OS: parts[0], Arch: parts[1], Format: parts[2]}
	// Checking the shape here gives a better error than the manifest parse
	// would, and it is the same rule: an artifact the runner would refuse must
	// never reach the signing step.
	if a.OS == update.OSDarwin {
		return update.Artifact{}, fmt.Errorf("-artifact %q: macOS is deferred by D26 and is not published at all", spec)
	}
	if a.OS != update.OSWindows && a.OS != update.OSLinux {
		return update.Artifact{}, fmt.Errorf("-artifact %q: unknown os %q", spec, a.OS)
	}
	if a.Arch != "amd64" && a.Arch != "arm64" {
		return update.Artifact{}, fmt.Errorf("-artifact %q: unknown arch %q (this release matrix is amd64 and arm64)", spec, a.Arch)
	}
	switch a.Format {
	case update.FormatExe, update.FormatMSI:
		if a.OS != update.OSWindows {
			return update.Artifact{}, fmt.Errorf("-artifact %q: format %q is Windows-only", spec, a.Format)
		}
	case update.FormatAppImage, update.FormatDeb, update.FormatTarGz:
		if a.OS != update.OSLinux {
			return update.Artifact{}, fmt.Errorf("-artifact %q: format %q is Linux-only", spec, a.Format)
		}
	default:
		return update.Artifact{}, fmt.Errorf("-artifact %q: unknown format %q", spec, a.Format)
	}

	info, err := os.Stat(path)
	if err != nil {
		return update.Artifact{}, fmt.Errorf("-artifact %q: %w", spec, err)
	}
	if !info.Mode().IsRegular() {
		return update.Artifact{}, fmt.Errorf("-artifact %q: %s is not a regular file", spec, path)
	}
	name := filepath.Base(path)
	if !fileNamePattern.MatchString(name) {
		return update.Artifact{}, fmt.Errorf("-artifact %q: the file name %q is not URL-safe; use letters, digits, dot, underscore, plus and dash", spec, name)
	}
	sum, err := update.HashFile(path)
	if err != nil {
		return update.Artifact{}, err
	}
	a.Size = info.Size()
	a.SHA256 = sum
	a.URL = strings.TrimSuffix(urlBase, "/") + "/" + name
	return a, nil
}

// validateURLBase accepts https anywhere and http only on this machine: the
// runner applies exactly that rule to the URLs it is asked to download from, so
// a pipeline that publishes an http:// URL for a public host is publishing a
// manifest every runner refuses.
func validateURLBase(raw string) error {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return errors.New("-url-base is required")
	}
	u, err := url.Parse(raw)
	if err != nil {
		return fmt.Errorf("-url-base %q: %w", raw, err)
	}
	if u.Scheme == "https" && u.Hostname() != "" {
		return nil
	}
	if u.Scheme == "http" {
		switch strings.ToLower(u.Hostname()) {
		case "localhost", "127.0.0.1", "::1":
			return nil
		}
	}
	return fmt.Errorf("-url-base %q must be https (http is accepted only for localhost, for a local dry run)", raw)
}

// cmdSign writes the detached signature over the manifest's exact bytes.
func cmdSign(args []string, stdout io.Writer) error {
	var (
		manifestPath string
		outPath      string
		keyFile      string
		keyEnv       string
		expectPublic string
	)
	fs := newFlagSet("sign",
		"Write the detached signature (<manifest>.sig) over the manifest's bytes.\n\n"+
			"The manifest is read as bytes and never re-encoded: the signature covers the\n"+
			"bytes, so re-serialising the document after signing invalidates it.", stdout)
	fs.StringVar(&manifestPath, "manifest", "", "the manifest document to sign (required)")
	fs.StringVar(&outPath, "out", "", "where to write the signature; default <manifest>.sig")
	fs.StringVar(&keyFile, "key-file", "", "file holding the release private key (hex or base64)")
	fs.StringVar(&keyEnv, "key-env", "", "environment variable holding the release private key (preferred over -key-file)")
	fs.StringVar(&expectPublic, "expect-public", "", "the public key this release is built with, as <id>:<hex>; the signature is refused unless the signing key matches it")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if strings.TrimSpace(manifestPath) == "" {
		return errors.New("-manifest is required")
	}
	if outPath == "" {
		outPath = manifestPath + ".sig"
	}

	raw, err := os.ReadFile(manifestPath)
	if err != nil {
		return fmt.Errorf("read %s: %w", manifestPath, err)
	}
	m, err := update.ParseManifest(raw)
	if err != nil {
		return fmt.Errorf("refusing to sign %s: %w", manifestPath, err)
	}

	priv, err := loadPrivateKey(keyFile, keyEnv)
	if err != nil {
		return err
	}
	derived := priv.Public().(ed25519.PublicKey)

	if strings.TrimSpace(expectPublic) != "" {
		want, err := parseExpectedKey(expectPublic)
		if err != nil {
			return fmt.Errorf("-expect-public: %w", err)
		}
		if !samePublicKey(derived, want.Public) {
			return fmt.Errorf("the signing key is not %q: a manifest signed with it would not verify on any runner carrying that public key, so nothing was written", want.ID)
		}
	}

	sig := update.SignManifest(raw, priv)
	// Self-check: the bytes that were signed must verify with the public half
	// of the key that signed them. This catches a build of this tool that has
	// drifted from the runner's own definition of what is signed.
	if _, err := update.VerifyManifest(raw, []byte(update.EncodeSignature(sig)), keySetOf(update.Key{ID: "self-check", Public: derived})); err != nil {
		return fmt.Errorf("the signature this tool just produced does not verify against its own public key: %w", err)
	}
	if err := os.WriteFile(outPath, []byte(update.EncodeSignature(sig)), 0o644); err != nil {
		return fmt.Errorf("write %s: %w", outPath, err)
	}
	sum, err := update.HashFile(manifestPath)
	if err != nil {
		return err
	}
	fmt.Fprintf(stdout, "signed %s\n  version %s channel %s rollout %d%%\n  manifest sha256 %s\n", outPath, m.Version, m.Channel, m.RolloutPercent, sum)
	fmt.Fprint(stdout, "  the manifest bytes are unchanged: publish this file and its .sig as they are\n")
	return nil
}

// verifyArtifact is one row of the JSON report.
type verifyArtifact struct {
	Key      string `json:"key"`
	Size     int64  `json:"size"`
	SHA256   string `json:"sha256"`
	URL      string `json:"url"`
	Verified bool   `json:"artifactVerified"`
}

// verifyReport is what "verify -json" prints.
type verifyReport struct {
	Manifest  string           `json:"manifest"`
	Sig       string           `json:"signature"`
	KeyIDs    string           `json:"keyIds"`
	Version   string           `json:"version"`
	Channel   string           `json:"channel"`
	Rollout   int              `json:"rolloutPercent"`
	Artifacts []verifyArtifact `json:"artifacts"`
}

// cmdVerify checks a manifest and signature against a release public key, and
// optionally checks that every artifact the manifest names really is the file
// the digest describes. The second half is what makes "the published artifacts
// are the ones CI built" a checked fact rather than a claim.
func cmdVerify(args []string, stdout io.Writer) error {
	var (
		manifestPath string
		sigPath      string
		publicKey    string
		asJSON       bool
		expectVer    string
		expectChan   string
		download     bool
		timeout      time.Duration
	)
	fs := newFlagSet("verify",
		"Verify a manifest against a release public key.\n\n"+
			"Pass the public key the release build was stamped with, not the one you\n"+
			"happen to have: a manifest that verifies against a key no build carries is a\n"+
			"manifest no runner will accept.", stdout)
	fs.StringVar(&manifestPath, "manifest", "", "the manifest document (required)")
	fs.StringVar(&sigPath, "sig", "", "the detached signature; default <manifest>.sig")
	fs.StringVar(&publicKey, "public-key", "", "the release public key, as <id>:<hex> (required)")
	fs.StringVar(&expectVer, "expect-version", "", "refuse the manifest unless it is this version")
	fs.StringVar(&expectChan, "expect-channel", "", "refuse the manifest unless its channel field is this")
	fs.BoolVar(&download, "download", false, "also fetch every artifact and check its size and SHA-256")
	fs.DurationVar(&timeout, "download-timeout", 2*time.Minute, "per-artifact timeout for -download")
	fs.BoolVar(&asJSON, "json", false, "print JSON")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if strings.TrimSpace(manifestPath) == "" {
		return errors.New("-manifest is required")
	}
	if sigPath == "" {
		sigPath = manifestPath + ".sig"
	}
	expect, err := parseExpectedKey(publicKey)
	if err != nil {
		return fmt.Errorf("-public-key: %w", err)
	}
	raw, err := os.ReadFile(manifestPath)
	if err != nil {
		return fmt.Errorf("read %s: %w", manifestPath, err)
	}
	sig, err := os.ReadFile(sigPath)
	if err != nil {
		return fmt.Errorf("read %s: %w", sigPath, err)
	}
	m, err := update.VerifyManifest(raw, sig, keySetOf(expect))
	if err != nil {
		return fmt.Errorf("%s against key %s: %w", manifestPath, expect.ID, err)
	}
	if expectVer != "" && m.Version != expectVer {
		return fmt.Errorf("the manifest is version %s, not the expected %s", m.Version, expectVer)
	}
	if expectChan != "" {
		chanName, err := update.ParseChannel(expectChan)
		if err != nil {
			return err
		}
		if m.Channel != chanName {
			return fmt.Errorf("the manifest is for channel %q, not %q", m.Channel, chanName)
		}
	}

	rep := verifyReport{
		Manifest: manifestPath,
		Sig:      sigPath,
		KeyIDs:   expect.ID,
		Version:  m.Version,
		Channel:  m.Channel,
		Rollout:  m.RolloutPercent,
	}
	for _, a := range m.Artifacts {
		row := verifyArtifact{Key: a.Key(), Size: a.Size, SHA256: a.SHA256, URL: a.URL}
		if download {
			if err := verifyRemoteArtifact(context.Background(), a, timeout); err != nil {
				return err
			}
			row.Verified = true
		}
		rep.Artifacts = append(rep.Artifacts, row)
	}

	if asJSON {
		enc := json.NewEncoder(stdout)
		enc.SetIndent("", "  ")
		return enc.Encode(rep)
	}
	fmt.Fprintf(stdout, "verified %s with key %q\n  version %s channel %s rollout %d%%\n", manifestPath, expect.ID, m.Version, m.Channel, m.RolloutPercent)
	for _, a := range rep.Artifacts {
		mark := "listed, not fetched"
		if a.Verified {
			mark = "downloaded and hashed"
		}
		fmt.Fprintf(stdout, "  %-22s %10d bytes  %s (%s)\n", a.Key, a.Size, a.SHA256, mark)
	}
	return nil
}

// verifyRemoteArtifact fetches one artifact and checks it against the manifest's
// entry. The download is bounded by the size the manifest declares, so a host
// that answers with more than the manifest describes cannot fill a disk here.
func verifyRemoteArtifact(ctx context.Context, a update.Artifact, timeout time.Duration) error {
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, a.URL, nil)
	if err != nil {
		return fmt.Errorf("artifact %s: %w", a.Key(), err)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return fmt.Errorf("artifact %s: %w", a.Key(), err)
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("artifact %s: %s answered %s", a.Key(), a.URL, resp.Status)
	}
	f, err := os.CreateTemp("", "reelbolt-verify-*")
	if err != nil {
		return err
	}
	defer func() {
		_ = f.Close()
		_ = os.Remove(f.Name())
	}()
	written, err := io.Copy(f, io.LimitReader(resp.Body, a.Size+1))
	if err != nil {
		return fmt.Errorf("artifact %s: %w", a.Key(), err)
	}
	if written != a.Size {
		return fmt.Errorf("artifact %s: %w: %s served %d bytes, the manifest says %d", a.Key(), update.ErrDigest, a.URL, written, a.Size)
	}
	if err := f.Close(); err != nil {
		return err
	}
	return update.VerifyArtifactFile(f.Name(), a)
}
