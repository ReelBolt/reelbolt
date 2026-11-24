package main

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/url"
	"os"
	"strings"

	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
	"github.com/vecchiotom/reelbolt/runner/internal/update"
)

// The -ldflags a release build is stamped with.
//
// A MAIN PACKAGE IS ADDRESSED AS "main.X", NOT BY ITS IMPORT PATH. This is the
// trap that cost an afternoon here: "-X main.Version=1.4.2" sets the daemon's
// version, and "-X github.com/vecchiotom/reelbolt/runner/cmd/reelbolt-runner
// .Version=1.4.2" is accepted, recorded in the binary's build settings, and
// silently ignored - the binary still reports "dev". A library package is the
// other way round: the update keys below are addressed by full path and that is
// the only form that works for them. Both are verified against the real
// toolchain by runner/cmd/reelbolt-manifest/ldflags_build_test.go.
//
// BOTH main packages are stamped with the same "main." names, because a release
// ships both binaries and they must agree about which build is running: the tray
// shows the version, and the daemon makes update decisions against it. One line
// therefore stamps whichever of the two is being built.
const (
	mainVersionVar = "main.Version"
	mainFontsetVar = "main.FontsetVersion"
	currentVar     = "github.com/vecchiotom/reelbolt/runner/internal/update.ReleaseKeyCurrentHex"
	nextVar        = "github.com/vecchiotom/reelbolt/runner/internal/update.ReleaseKeyNextHex"
	// telemetryVar is the crash collector's address (runner/internal/telemetry).
	telemetryVar = "github.com/vecchiotom/reelbolt/runner/internal/telemetry.CollectorEndpoint"
)

// TelemetryStampEnv overrides the collector address "ldflags" stamps when no
// -telemetry-endpoint flag is given. The value "none" stamps nothing, which is
// what a local build that must never post anywhere wants.
const TelemetryStampEnv = "REELBOLT_TELEMETRY_ENDPOINT_STAMP"

// stampedSymbols are the variables the build line above expects to exist, in the
// files the build line cannot check by itself. release_test.go fails when one of
// them stops being declared, so a rename is a test failure rather than a release
// that reports "dev" and refuses every update.
var stampedSymbols = []struct{ file, name string }{
	{"../reelbolt-runner/main.go", "Version"},
	{"../reelbolt-runner/main.go", "FontsetVersion"},
	{"../reelbolt-runner-desktop/main.go", "Version"},
	{"../reelbolt-runner-desktop/main.go", "FontsetVersion"},
	{"../../internal/update/pubkeys.go", "ReleaseKeyCurrentHex"},
	{"../../internal/update/pubkeys.go", "ReleaseKeyNextHex"},
	{"../../internal/telemetry/endpoint.go", "CollectorEndpoint"},
}

// cmdGenKey creates the release keypair. It is the one command in this tool that
// produces a secret, it is never run by CI, and it prints only the public half.
func cmdGenKey(args []string, stdout io.Writer) error {
	var (
		outPath string
		id      = "release-current"
		force   bool
	)
	fs := newFlagSet("gen-key",
		"Create the Ed25519 release keypair the runner's auto-update trusts.\n\n"+
			"Run this once, on a machine you control, and then store the two halves in\n"+
			"the GitHub environment \"release\": the private key as the secret\n"+
			"RUNNER_MANIFEST_SIGNING_KEY and the public stamp as the variable\n"+
			"RUNNER_RELEASE_PUBLIC_KEY_CURRENT. The private key is written to a file and\n"+
			"is never printed.", stdout)
	fs.StringVar(&outPath, "out", "", "file to write the private key to (created mode 0600)")
	fs.StringVar(&id, "id", id, "key id recorded in the public stamp, for the log")
	fs.BoolVar(&force, "force", false, "overwrite an existing key file")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if strings.TrimSpace(outPath) == "" {
		return errors.New("-out is required: the private key is only ever written to a file")
	}
	if id == "" || strings.ContainsAny(id, ": \t") {
		return fmt.Errorf("-id %q must be non-empty and must not contain a colon or a space", id)
	}

	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return fmt.Errorf("generate the release keypair: %w", err)
	}
	// The file holds the 64-byte private key as hex: one line of hex is what a
	// Github secret wants, and hex survives being pasted through a shell, a
	// password manager and a YAML file without a quoting accident.
	if err := writeNewFile(outPath, []byte(hex.EncodeToString(priv)+"\n"), force); err != nil {
		return err
	}
	stamp := id + ":" + hex.EncodeToString(pub)
	fmt.Fprintf(stdout, "private key written to %s (mode 0600, not printed)\n\n", outPath)
	fmt.Fprintf(stdout, "environment \"release\", secret:\n\n  RUNNER_MANIFEST_SIGNING_KEY = <the contents of %s>\n\n", outPath)
	fmt.Fprintf(stdout, "environment \"release\", variable:\n\n  RUNNER_RELEASE_PUBLIC_KEY_CURRENT = %s\n\n", stamp)
	fmt.Fprint(stdout,
		"A build stamped with that variable accepts a manifest signed with this key and\n"+
			"nothing else. Keep a copy of the private key where you can restore it from:\n"+
			"a lost release key cannot be recovered, and rotating to a new one needs the\n"+
			"old one still trusted. To rotate, first ship a release carrying the new key as\n"+
			"RUNNER_RELEASE_PUBLIC_KEY_NEXT, then start signing with the new key and move\n"+
			"it into RUNNER_RELEASE_PUBLIC_KEY_CURRENT.\n")
	return nil
}

// writeNewFile creates path with mode, refusing to clobber an existing file
// unless force is set. A key file that is silently overwritten is a key that is
// silently lost.
func writeNewFile(path string, data []byte, force bool) error {
	flags := os.O_WRONLY | os.O_CREATE | os.O_EXCL
	if force {
		flags = os.O_WRONLY | os.O_CREATE | os.O_TRUNC
	}
	f, err := os.OpenFile(path, flags, 0o600)
	if err != nil {
		if errors.Is(err, os.ErrExist) {
			return fmt.Errorf("%s already exists; move it aside or pass -force (overwriting a release key loses it)", path)
		}
		return fmt.Errorf("create %s: %w", path, err)
	}
	defer func() { _ = f.Close() }()
	if _, err := f.Write(data); err != nil {
		return fmt.Errorf("write %s: %w", path, err)
	}
	// OpenFile's mode is filtered by the process umask, and on a permissive
	// umask the result would be world-readable. Chmod is not filtered.
	if err := os.Chmod(path, 0o600); err != nil {
		return fmt.Errorf("chmod %s: %w", path, err)
	}
	return nil
}

// cmdLdflags prints the build line that stamps a release build.
//
// It refuses a build with no release public key. Such a build is not a lax
// release: TrustedKeys returns ErrNoTrustedKey and the runner refuses every
// update before it touches the network, so shipping one is a build that can
// never be updated — the failure is silent until a user is stuck on an old
// version.
func cmdLdflags(args []string, stdout io.Writer) error {
	var (
		version      string
		fontset      string
		current      string
		next         string
		release      bool
		noReleaseKey bool
		telemetryURL string
	)
	fs := newFlagSet("ldflags",
		"Print the -ldflags and -tags that stamp a release build.\n\n"+
			"The output is one key=value per line, ready to append to a job's\n"+
			"GITHUB_OUTPUT file (or to read by hand locally).", stdout)
	fs.StringVar(&version, "version", "", "the release version, dotted (required, e.g. 1.4.2)")
	fs.StringVar(&fontset, "fontset", "", "the bundled font set's tag, if this release changes it")
	fs.StringVar(&current, "current-key", "", "the release public key this build trusts, as <id>:<hex> (required)")
	fs.StringVar(&next, "next-key", "", "the public key the next release will be signed with, as <id>:<hex>")
	fs.BoolVar(&release, "release", false, "also emit tags=release, which compiles out the development trust escape hatch")
	fs.BoolVar(&noReleaseKey, "no-release-key", false, "stamp no trust root at all, for a LOCAL dry run: the build this produces refuses every update, and no release may be published from it")
	fs.StringVar(&telemetryURL, "telemetry-endpoint", defaultTelemetryStamp(),
		"the crash collector a build posts opt-in reports to; \"none\" stamps nothing (default: $"+TelemetryStampEnv+", else "+telemetry.ProductionCollectorEndpoint+")")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if strings.TrimSpace(version) == "" {
		return errors.New("-version is required")
	}
	if _, err := update.ParseVersion(version); err != nil {
		return fmt.Errorf("-version: %w", err)
	}
	if noReleaseKey {
		if release {
			return errors.New("-no-release-key and -release are opposites: a release build that trusts no key is a release nobody can update to")
		}
		if strings.TrimSpace(current) != "" {
			return errors.New("-no-release-key and -current-key are opposites")
		}
	} else {
		if strings.TrimSpace(current) == "" {
			return errors.New("-current-key is required: a build with no release public key refuses every update, so publishing one ships a runner that can never be updated. Pass -no-release-key to build one on purpose, for a local dry run only")
		}
		if _, err := update.ParsePublicKey(current); err != nil {
			return fmt.Errorf("-current-key: %w", err)
		}
		if strings.TrimSpace(next) != "" {
			if _, err := update.ParsePublicKey(next); err != nil {
				return fmt.Errorf("-next-key: %w", err)
			}
		}
	}

	stampTelemetry, err := checkTelemetryStamp(telemetryURL)
	if err != nil {
		return err
	}

	parts := []string{"-X", mainVersionVar + "=" + version}
	if strings.TrimSpace(fontset) != "" {
		parts = append(parts, "-X", mainFontsetVar+"="+fontset)
	}
	if !noReleaseKey {
		parts = append(parts, "-X", currentVar+"="+current)
		if strings.TrimSpace(next) != "" {
			parts = append(parts, "-X", nextVar+"="+next)
		}
	}
	if stampTelemetry != "" {
		parts = append(parts, "-X", telemetryVar+"="+stampTelemetry)
	}
	fmt.Fprintf(stdout, "ldflags=%s\n", strings.Join(parts, " "))
	if release {
		fmt.Fprintln(stdout, "tags=release")
	}
	if noReleaseKey {
		fmt.Fprint(stdout, "# WARNING: no release public key was stamped. This build refuses every update and must not be published to a channel.\n")
	}
	return nil
}

// defaultTelemetryStamp is the collector a build is stamped with when the flag is
// not given: the environment's override, else the production collector.
func defaultTelemetryStamp() string {
	if v := strings.TrimSpace(os.Getenv(TelemetryStampEnv)); v != "" {
		return v
	}
	return telemetry.ProductionCollectorEndpoint
}

// checkTelemetryStamp returns the address to stamp, "" for none, or why it is
// refused. It is stricter than the runner's own Endpoint(): a release must not
// post a crash report in clear text, so anything but https is refused here
// unless it is a loopback address (a local fake collector).
func checkTelemetryStamp(raw string) (string, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" || raw == "none" {
		return "", nil
	}
	if strings.ContainsAny(raw, " \t'\"") {
		return "", fmt.Errorf("-telemetry-endpoint %q contains a character -ldflags cannot carry", raw)
	}
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" {
		return "", fmt.Errorf("-telemetry-endpoint %q is not an absolute URL", raw)
	}
	switch {
	case u.Scheme == "https":
	case u.Scheme == "http" && isLoopbackHost(u.Hostname()):
	default:
		return "", fmt.Errorf("-telemetry-endpoint %q must be https (http only for a loopback collector)", raw)
	}
	if u.RawQuery != "" || u.Fragment != "" || u.User != nil {
		return "", fmt.Errorf("-telemetry-endpoint %q must not carry a query, fragment or credentials", raw)
	}
	return raw, nil
}

func isLoopbackHost(h string) bool {
	switch strings.ToLower(h) {
	case "localhost", "127.0.0.1", "::1":
		return true
	}
	return false
}

// loadPrivateKey reads the release private key from the environment or from a
// file.
//
// The environment is the preferred source: a value passed in argv lands in the
// process table and in the shell history of every host that ran it.
func loadPrivateKey(path, envName string) (ed25519.PrivateKey, error) {
	var raw string
	switch {
	case strings.TrimSpace(envName) != "":
		raw = os.Getenv(envName)
		if strings.TrimSpace(raw) == "" {
			return nil, fmt.Errorf("$%s is empty: the release signing key is not configured", envName)
		}
	case strings.TrimSpace(path) != "":
		b, err := os.ReadFile(path)
		if err != nil {
			return nil, fmt.Errorf("read the release key: %w", err)
		}
		raw = string(b)
	default:
		return nil, errors.New("no release key: pass -key-env (preferred) or -key-file")
	}
	return parsePrivateKey(raw)
}

// parsePrivateKey accepts the key as hex or base64, as a 32-byte seed or as the
// 64-byte private key ed25519.GenerateKey returns — someone will paste whichever
// their tooling produced, and refusing one of the two for no security reason
// only produces a support ticket.
func parsePrivateKey(s string) (ed25519.PrivateKey, error) {
	s = strings.TrimSpace(s)
	if s == "" {
		return nil, errors.New("the release private key is empty")
	}
	raw, err := hex.DecodeString(s)
	if err != nil {
		raw, err = base64.StdEncoding.DecodeString(s)
		if err != nil {
			raw, err = base64.RawStdEncoding.DecodeString(s)
		}
	}
	if err != nil {
		return nil, fmt.Errorf("the release private key is neither hex nor base64: %w", err)
	}
	switch len(raw) {
	case ed25519.SeedSize:
		return ed25519.NewKeyFromSeed(raw), nil
	case ed25519.PrivateKeySize:
		return ed25519.PrivateKey(raw), nil
	default:
		return nil, fmt.Errorf("the release private key is %d bytes, expected %d (a seed) or %d (seed and public key)",
			len(raw), ed25519.SeedSize, ed25519.PrivateKeySize)
	}
}

// parseExpectedKey parses a public key the pipeline was told to expect, as
// <id>:<hex> or bare hex/base64. It is the cross-check that catches a release
// signed with a key other than the one compiled into the build — the failure
// that would otherwise be discovered by every user at once.
func parseExpectedKey(spec string) (update.Key, error) {
	key, err := update.ParsePublicKey(spec)
	if err != nil {
		return update.Key{}, err
	}
	if key.ID == "" {
		key.ID = "expected"
	}
	return key, nil
}

// keySetOf builds the one-key set used for a self-check.
func keySetOf(key update.Key) update.KeySet { return update.KeySet{key} }

// samePublicKey reports whether two public keys are byte-identical. It is a
// plain comparison rather than a constant-time one: both values are public.
func samePublicKey(a, b ed25519.PublicKey) bool { return bytes.Equal(a, b) }
