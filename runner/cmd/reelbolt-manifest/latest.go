package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/update"
)

// The download page's contract (site/lib/runner-downloads.ts) is that every
// artifact it links is reachable at a STABLE URL under
// https://downloads.reelbolt.ai/runner/<channel>/latest/, and that the SHA-256
// it shows next to the link is the one the pipeline published. The signed
// manifest is per-version and is what a runner consumes; this document is the
// per-channel convenience the site reads.
//
// It is generated FROM the signed manifest, and only after the signature has
// been verified, so a latest.json can never describe artifacts the release key
// did not sign. It is deliberately NOT itself signed: nothing security-relevant
// reads it. A runner refuses to update unless the signed manifest verifies, and
// the SHA-256 on a download page is a checksum for someone typing it into
// shasum, not a trust anchor. Saying so is the point.
//
// The stable URLs are published as COPIES of the release's artifacts, not as
// redirects: object storage has no redirect that works on every path and host,
// and a URL that resolves to the file on the origin is a URL that cannot be
// wrong. The signed manifest keeps pointing at the immutable per-version paths,
// so the copy is a convenience for humans and never an input to an update.

// latestArtifact is one entry of latest.json.
type latestArtifact struct {
	// Platform is the download page's own vocabulary: windows-x64, linux-x64 or
	// linux-arm64. No macOS key exists, because D26 ships no macOS runner.
	Platform string `json:"platform"`
	OS       string `json:"os"`
	Arch     string `json:"arch"`
	Format   string `json:"format"`
	// Kind separates what a person downloads from the payload a runner applies
	// to an installation it already has. The download page must only ever offer
	// "installer": an "update-payload" entry is the raw executable behind the
	// manifest's tarball format, and handing someone that as a download gives
	// them a file with no bundled ffmpeg and no PATH entry.
	Kind string `json:"kind"`
	// Recommended marks the one artifact to lead with per platform.
	Recommended bool   `json:"recommended"`
	Label       string `json:"label"`
	URL         string `json:"url"`
	SHA256      string `json:"sha256"`
	Size        int64  `json:"size"`
}

// latestDocument is what "latest" writes.
type latestDocument struct {
	Channel      string `json:"channel"`
	Version      string `json:"version"`
	PublishedAt  string `json:"publishedAt,omitempty"`
	MinSupported string `json:"minSupported,omitempty"`
	Notes        string `json:"notes,omitempty"`
	// ManifestURL and ManifestSHA256 let anything that cares check that this
	// document still describes the signed manifest, rather than trusting a file
	// that nothing signs.
	ManifestURL    string `json:"manifestUrl"`
	ManifestSHA256 string `json:"manifestSha256"`
	// ChecksumsURL is the SHA256SUMS file published beside the installers
	// (sha256sum -c format), for someone verifying a download from a shell.
	// Optional: omitted when the release did not publish one.
	ChecksumsURL string           `json:"checksumsUrl,omitempty"`
	GeneratedAt  string           `json:"generatedAt"`
	Artifacts    []latestArtifact `json:"artifacts"`
}

// platformName maps a manifest (os, arch) onto the download page's vocabulary.
func platformName(os, arch string) string {
	switch os {
	case update.OSWindows:
		return "windows-x64"
	case update.OSLinux:
		if arch == "arm64" {
			return "linux-arm64"
		}
		return "linux-x64"
	default:
		return os + "-" + arch
	}
}

// artifactKind classifies a manifest format for a human reader.
func artifactKind(format string) string {
	switch format {
	case update.FormatTarGz:
		// The manifest's "tarball" format is the raw executable, not an archive:
		// runner/internal/update copies it byte for byte over the installed
		// binary. It is an update payload, never a download.
		return "update-payload"
	default:
		return "installer"
	}
}

// artifactLabel is the human name of a format.
func artifactLabel(os, format string) string {
	switch format {
	case update.FormatMSI:
		return "Windows installer (.msi)"
	case update.FormatExe:
		return "Windows executable (.exe)"
	case update.FormatAppImage:
		return "Linux AppImage"
	case update.FormatDeb:
		return "Linux package (.deb)"
	case update.FormatTarGz:
		return "Linux executable (update payload)"
	default:
		return os + " " + format
	}
}

// recommendedPerPlatform is the one artifact a page leads with: the MSI on
// Windows (it installs the tray, the daemon and both media tools, and sets PATH)
// and the AppImage on Linux (the one format that replaces itself in place).
func recommendedPerPlatform(platform, format string) bool {
	switch platform {
	case "windows-x64":
		return format == update.FormatMSI
	case "linux-x64", "linux-arm64":
		return format == update.FormatAppImage
	}
	return false
}

// cmdLatest writes the per-channel latest.json and the list of objects the
// pipeline must copy to the stable URLs it names.
func cmdLatest(args []string, stdout io.Writer) error {
	var (
		manifestPath string
		sigPath      string
		publicKey    string
		urlBase      string
		manifestURL  string
		outPath      string
		uploadsOut   string
		dist         string
		checksumsURL string
	)
	fs := newFlagSet("latest",
		"Write the per-channel latest.json the download page reads.\n\n"+
			"It is generated from a VERIFIED manifest, so it can never describe an\n"+
			"artifact the release key did not sign. It is not itself signed: a runner\n"+
			"reads the signed manifest, and a checksum on a download page is a\n"+
			"convenience, not a trust anchor.", stdout)
	fs.StringVar(&manifestPath, "manifest", "", "the signed manifest document (required)")
	fs.StringVar(&sigPath, "sig", "", "the detached signature; default <manifest>.sig")
	fs.StringVar(&publicKey, "public-key", "", "the release public key, as <id>:<hex> (required)")
	fs.StringVar(&urlBase, "url-base", "", "the stable per-channel base the artifacts are also published under, e.g. https://downloads.reelbolt.ai/runner/beta/latest (required)")
	fs.StringVar(&manifestURL, "manifest-url", "", "the URL the signed manifest is published at, recorded in the document")
	fs.StringVar(&outPath, "out", "latest.json", "where to write the document")
	fs.StringVar(&uploadsOut, "uploads-out", "", "also write the local-path and object-key pairs the stable URLs need, one per line")
	fs.StringVar(&dist, "dist", "", "directory the artifacts are staged in; a manifest URL's basename is matched against it")
	fs.StringVar(&checksumsURL, "checksums-url", "", "the URL of the published SHA256SUMS file, recorded in the document when set")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if strings.TrimSpace(manifestPath) == "" {
		return errors.New("-manifest is required")
	}
	if sigPath == "" {
		sigPath = manifestPath + ".sig"
	}
	if err := validateURLBase(urlBase); err != nil {
		return err
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
		return fmt.Errorf("refusing to describe an unverified manifest: %w", err)
	}
	sum, err := update.HashFile(manifestPath)
	if err != nil {
		return err
	}

	doc := latestDocument{
		Channel:        m.Channel,
		Version:        m.Version,
		PublishedAt:    m.PublishedAt,
		MinSupported:   m.MinSupported,
		Notes:          m.Notes,
		ManifestURL:    manifestURL,
		ChecksumsURL:   checksumsURL,
		ManifestSHA256: sum,
		GeneratedAt:    time.Now().UTC().Format(time.RFC3339),
	}
	type upload struct{ path, key string }
	var uploads []upload
	taken := map[string]bool{}
	for _, a := range m.Artifacts {
		name := filepath.Base(a.URL)
		local := ""
		if dist != "" {
			local = filepath.Join(dist, name)
			if _, err := os.Stat(local); err != nil {
				return fmt.Errorf("the manifest names %s but it is not in %s: a stable URL cannot be published for a file that was not built", name, dist)
			}
		}
		platform := platformName(a.OS, a.Arch)
		kind := artifactKind(a.Format)
		recommended := kind == "installer" && !taken[platform] && recommendedPerPlatform(platform, a.Format)
		if recommended {
			taken[platform] = true
		}
		doc.Artifacts = append(doc.Artifacts, latestArtifact{
			Platform:    platform,
			OS:          a.OS,
			Arch:        a.Arch,
			Format:      a.Format,
			Kind:        kind,
			Recommended: recommended,
			Label:       artifactLabel(a.OS, a.Format),
			URL:         strings.TrimSuffix(urlBase, "/") + "/" + name,
			SHA256:      strings.ToLower(a.SHA256),
			Size:        a.Size,
		})
		if local != "" {
			uploads = append(uploads, upload{path: local, key: name})
		}
	}

	encoded, err := json.MarshalIndent(doc, "", "  ")
	if err != nil {
		return fmt.Errorf("encode the document: %w", err)
	}
	encoded = append(encoded, '\n')
	if err := os.WriteFile(outPath, encoded, 0o644); err != nil {
		return fmt.Errorf("write %s: %w", outPath, err)
	}

	if uploadsOut != "" {
		var b strings.Builder
		for _, u := range uploads {
			b.WriteString(u.path + "\t" + u.key + "\n")
		}
		if err := os.WriteFile(uploadsOut, []byte(b.String()), 0o644); err != nil {
			return fmt.Errorf("write %s: %w", uploadsOut, err)
		}
	}

	fmt.Fprintf(stdout, "wrote %s: version %s channel %s, %d artifact(s)\n", outPath, m.Version, m.Channel, len(doc.Artifacts))
	for _, a := range doc.Artifacts {
		mark := " "
		if a.Recommended {
			mark = "*"
		}
		fmt.Fprintf(stdout, " %s %-12s %-15s %-8s %s\n", mark, a.Platform, a.Kind, a.Format, a.URL)
	}
	if uploadsOut != "" {
		fmt.Fprintf(stdout, "\nthe stable URLs need these objects copied to the latest/ prefix (%s)\n", uploadsOut)
	}
	fmt.Fprint(stdout, "\nThe download page reads this document from the published latest.json URL; see runner/build/README.md.\n")
	return nil
}
