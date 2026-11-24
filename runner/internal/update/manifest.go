package update

import (
	"bytes"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strconv"
	"strings"
)

// Limits on anything that arrives from the update host. A manifest is a small
// document and an artifact is a runner build; a value outside these bounds is
// taken as a malformed manifest rather than an instruction to allocate.
const (
	// MaxManifestBytes bounds the manifest document itself.
	MaxManifestBytes = 1 << 20 // 1 MiB
	// MaxSignatureBytes bounds the detached signature file.
	MaxSignatureBytes = 4 << 10 // 4 KiB
	// MaxArtifactBytes bounds a downloadable artifact. The runner is a Go
	// binary plus a bundled ffmpeg; 512 MiB is generous and still finite.
	MaxArtifactBytes = 512 << 20
	// MaxArtifacts bounds the artifact list, so a hostile manifest cannot make
	// validation quadratic.
	MaxArtifacts = 32
)

// Platform identifiers used in the manifest's artifacts.
const (
	OSWindows = "windows"
	OSLinux   = "linux"
	OSDarwin  = "darwin" // never published: D26 ships no macOS runner.
)

// Artifact formats, which decide how an update is applied.
const (
	FormatExe      = "exe"      // a bare Windows executable
	FormatMSI      = "msi"      // a Windows installer
	FormatAppImage = "appimage" // a Linux AppImage
	FormatDeb      = "deb"      // a Debian package: verified, then notified, never self-installed
	// FormatTarGz is the manifest format string for a plain, manual or archive
	// install. The ARTIFACT it names is the RAW EXECUTABLE, not a tar.gz:
	// applyReplace byte-copies the staged file over the installed binary, so the
	// bytes named here have to be the binary itself. A human-readable .tar.gz is
	// published alongside it as a separate, non-manifest file.
	FormatTarGz = "tarball"
)

// Artifact is one downloadable build.
type Artifact struct {
	OS     string `json:"os"`
	Arch   string `json:"arch"`
	Format string `json:"format"`
	URL    string `json:"url"`
	SHA256 string `json:"sha256"`
	Size   int64  `json:"size"`
}

// Key identifies an artifact in a manifest.
func (a Artifact) Key() string { return a.OS + "/" + a.Arch + "/" + a.Format }

// Manifest is the signed document published at
// <base>/<channel>/manifest.json (see README.md).
type Manifest struct {
	// Version is the build the artifacts are, as a dotted version ("1.4.2" or
	// "v1.4.2", optionally with a "-beta.1" pre-release part).
	Version string `json:"version"`
	// MinSupported is the oldest version still supported by the platform. A
	// runner below it must stop taking jobs and tell the user to update. It is
	// the offline twin of the gateway's welcome.minAgentVersion, which is what
	// the agent library enforces while connected.
	MinSupported string `json:"minSupported,omitempty"`
	// Channel is the channel this manifest was published to. It is advisory:
	// the channel actually used is the one in the URL the runner fetched, and a
	// mismatch is refused rather than believed.
	Channel string `json:"channel,omitempty"`
	// RolloutPercent is how much of the fleet this release is offered to, 0
	// through 100. The bucket is hash(deviceId) % 100.
	RolloutPercent int `json:"rolloutPercent"`
	// PublishedAt is an RFC 3339 timestamp, for the log and the UI.
	PublishedAt string `json:"publishedAt,omitempty"`
	// Notes is free text shown to a user who asks what changed.
	Notes string `json:"notes,omitempty"`
	// Artifacts are the builds. At least one is required.
	Artifacts []Artifact `json:"artifacts"`
}

// ParseManifest parses and validates an unsigned manifest document. It does NOT
// check a signature: only VerifyManifest does that, and nothing else in this
// package calls this on host data.
func ParseManifest(raw []byte) (*Manifest, error) {
	if len(raw) == 0 {
		return nil, errors.New("update: the manifest is empty")
	}
	if len(raw) > MaxManifestBytes {
		return nil, fmt.Errorf("update: the manifest is %d bytes, over the %d-byte limit", len(raw), MaxManifestBytes)
	}
	dec := json.NewDecoder(bytes.NewReader(raw))
	// A manifest is a closed document: an unknown field is a format change this
	// build does not understand, and guessing at it would be guessing at a
	// field that may be a security decision.
	dec.DisallowUnknownFields()
	var m Manifest
	if err := dec.Decode(&m); err != nil {
		return nil, fmt.Errorf("update: parse the manifest: %w", err)
	}
	if _, err := dec.Token(); !errors.Is(err, io.EOF) {
		return nil, errors.New("update: the manifest carries trailing data")
	}
	if err := m.Validate(); err != nil {
		return nil, err
	}
	return &m, nil
}

// Validate rejects a manifest this runner must not act on.
func (m *Manifest) Validate() error {
	if _, err := ParseVersion(m.Version); err != nil {
		return fmt.Errorf("update: manifest version %q: %w", m.Version, err)
	}
	if m.MinSupported != "" {
		if _, err := ParseVersion(m.MinSupported); err != nil {
			return fmt.Errorf("update: manifest minSupported %q: %w", m.MinSupported, err)
		}
	}
	if m.RolloutPercent < 0 || m.RolloutPercent > 100 {
		return fmt.Errorf("update: rolloutPercent %d is outside 0-100", m.RolloutPercent)
	}
	if len(m.Artifacts) == 0 {
		return errors.New("update: the manifest lists no artifacts")
	}
	if len(m.Artifacts) > MaxArtifacts {
		return fmt.Errorf("update: the manifest lists %d artifacts, over the %d limit", len(m.Artifacts), MaxArtifacts)
	}
	seen := map[string]bool{}
	for i, a := range m.Artifacts {
		if err := a.validate(); err != nil {
			return fmt.Errorf("update: artifact %d (%s): %w", i, a.Key(), err)
		}
		if seen[a.Key()] {
			return fmt.Errorf("update: artifact %s is listed twice", a.Key())
		}
		seen[a.Key()] = true
	}
	return nil
}

func (a Artifact) validate() error {
	switch a.OS {
	case OSWindows, OSLinux:
	case OSDarwin:
		// D26 ships no macOS runner. A darwin artifact in a manifest is a
		// manifest this build does not understand rather than one it acts on.
		return errors.New("macOS artifacts are not published (D26) and are not accepted")
	default:
		return fmt.Errorf("unknown os %q", a.OS)
	}
	switch a.Arch {
	case "amd64", "arm64":
	case "386", "arm", "riscv64":
		return fmt.Errorf("unsupported arch %q", a.Arch)
	default:
		return fmt.Errorf("unknown arch %q", a.Arch)
	}
	switch a.Format {
	case FormatExe, FormatMSI:
		if a.OS != OSWindows {
			return fmt.Errorf("format %q is Windows-only", a.Format)
		}
	case FormatAppImage, FormatDeb, FormatTarGz:
		if a.OS != OSLinux {
			return fmt.Errorf("format %q is Linux-only", a.Format)
		}
	default:
		return fmt.Errorf("unknown format %q", a.Format)
	}
	if a.URL == "" {
		return errors.New("no url")
	}
	if !strings.HasPrefix(a.URL, "https://") && !strings.HasPrefix(a.URL, "http://") {
		return fmt.Errorf("url %q is not http(s)", a.URL)
	}
	if err := validateSHA256(a.SHA256); err != nil {
		return err
	}
	if a.Size <= 0 {
		return fmt.Errorf("size %d is not a positive number of bytes", a.Size)
	}
	if a.Size > MaxArtifactBytes {
		return fmt.Errorf("size %d is over the %d-byte limit", a.Size, MaxArtifactBytes)
	}
	return nil
}

// validateSHA256 checks a lowercase-or-uppercase hex digest of the right length.
func validateSHA256(sum string) error {
	if len(sum) != 64 {
		return fmt.Errorf("sha256 %q is not 64 hex characters", sum)
	}
	if _, err := hex.DecodeString(sum); err != nil {
		return fmt.Errorf("sha256 %q is not hex: %w", sum, err)
	}
	return nil
}

// Artifact returns the build for one platform. The format is matched against
// the preferred list; a platform with no artifact is not an error here — the
// caller decides what to say about it.
func (m *Manifest) Artifact(goos, goarch string, preferred ...string) (Artifact, bool) {
	for _, format := range preferred {
		for _, a := range m.Artifacts {
			if a.OS == goos && a.Arch == goarch && a.Format == format {
				return a, true
			}
		}
	}
	for _, a := range m.Artifacts {
		if a.OS == goos && a.Arch == goarch {
			return a, true
		}
	}
	return Artifact{}, false
}

// ---- versions ----

// Version is a parsed dotted version.
type Version struct {
	Numbers []int
	Pre     string
}

// ParseVersion parses "1.2.3", "v1.2.3" and "1.2-beta.1". It is deliberately
// small: the runner's own version and the manifest's are the only inputs, and an
// unparseable version is a refusal rather than a guess.
func ParseVersion(s string) (Version, error) {
	s = strings.TrimSpace(s)
	if s == "" {
		return Version{}, errors.New("empty version")
	}
	s = strings.TrimPrefix(s, "v")
	var pre string
	if i := strings.IndexAny(s, "-+"); i >= 0 {
		pre = s[i+1:]
		s = s[:i]
	}
	parts := strings.Split(s, ".")
	if len(parts) == 0 || len(parts) > 4 {
		return Version{}, fmt.Errorf("%q is not a dotted version", s)
	}
	v := Version{Pre: pre}
	for _, p := range parts {
		if p == "" {
			return Version{}, fmt.Errorf("%q has an empty part", s)
		}
		n, err := strconv.Atoi(p)
		if err != nil || n < 0 {
			return Version{}, fmt.Errorf("%q is not a numeric version", s)
		}
		v.Numbers = append(v.Numbers, n)
	}
	return v, nil
}

// CompareVersions returns -1, 0 or 1 as a is older than, equal to, or newer
// than b. A pre-release sorts before its release: 1.2.3-beta.1 < 1.2.3.
func CompareVersions(a, b string) (int, error) {
	av, err := ParseVersion(a)
	if err != nil {
		return 0, err
	}
	bv, err := ParseVersion(b)
	if err != nil {
		return 0, err
	}
	return av.Compare(bv), nil
}

// Compare orders two parsed versions.
func (v Version) Compare(o Version) int {
	n := len(v.Numbers)
	if len(o.Numbers) > n {
		n = len(o.Numbers)
	}
	for i := 0; i < n; i++ {
		var a, b int
		if i < len(v.Numbers) {
			a = v.Numbers[i]
		}
		if i < len(o.Numbers) {
			b = o.Numbers[i]
		}
		if a != b {
			if a < b {
				return -1
			}
			return 1
		}
	}
	switch {
	case v.Pre == o.Pre:
		return 0
	case v.Pre == "":
		return 1
	case o.Pre == "":
		return -1
	case v.Pre < o.Pre:
		return -1
	default:
		return 1
	}
}

// Newer reports whether candidate is a strictly newer version than current.
// Both must parse; that is the caller's job to have established.
func Newer(candidate, current string) (bool, error) {
	c, err := CompareVersions(candidate, current)
	if err != nil {
		return false, err
	}
	return c > 0, nil
}

// ---- rollout ----

// Bucket is the rollout bucket of a device: hash(deviceId) % 100, which the
// plan specifies. It is stable for a device across releases, so a rollout that
// admits bucket 0-9 admits exactly the same machines every time — a device
// cannot be flipped in and out of a rollout by retrying.
func Bucket(deviceID string) int {
	// The prefix is domain separation and is 0-terminated, so no device id can
	// be shifted into it: two different ids always hash different inputs.
	h := sha256.New()
	h.Write([]byte("reelbolt-runner-rollout"))
	h.Write([]byte{0})
	h.Write([]byte(deviceID))
	var sum [sha256.Size]byte
	copy(sum[:], h.Sum(nil))
	return int(binary.BigEndian.Uint64(sum[:8]) % 100)
}

// Admitted reports whether this device is inside a rollout percentage.
func Admitted(deviceID string, rolloutPercent int) bool {
	if rolloutPercent <= 0 {
		return false
	}
	if rolloutPercent >= 100 {
		return true
	}
	return Bucket(deviceID) < rolloutPercent
}
