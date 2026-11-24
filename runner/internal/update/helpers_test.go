package update

import (
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
)

// A test release is a keypair plus a manifest over a real artifact, served by a
// local HTTP server. It is deliberately the same shape the release pipeline will
// publish: an Ed25519 key, a JSON manifest, a detached .sig file, and an
// artifact addressed by exact size and SHA-256.
type testRelease struct {
	Key      ed25519.PrivateKey
	KeySet   KeySet
	Manifest Manifest
	Raw      []byte
	Sig      []byte
	Artifact []byte
	Server   *httptest.Server
	// BaseURL is what a Host should be pointed at.
	BaseURL string
	// Host is the dev host the Host client must be told about.
	Host string
	// ArtifactRequests counts the times the artifact itself was fetched, so a
	// test can assert that a hold-back (or a refusal) downloaded nothing.
	ArtifactRequests *atomic.Int32
}

type releaseOptions struct {
	Version        string
	MinSupported   string
	Channel        string
	RolloutPercent int
	// ArtifactBytes defaults to a small fake binary.
	ArtifactBytes []byte
	// Tamper, when set, rewrites the served artifact AFTER the manifest was
	// signed, which is the attack the sha256 check exists to stop.
	Tamper bool
	// TamperManifest rewrites the served manifest after signing.
	TamperManifest bool
	// SignWith is a second key: the manifest is signed by it while the trusted
	// set holds the first, which is the "wrong key" case.
	SignWith ed25519.PrivateKey
	// OmitSignature serves a 404 for the .sig file.
	OmitSignature bool
	// ZeroRollout publishes a manifest that offers the release to nobody, for
	// the hold-back path. Without it the rollout defaults to 100.
	ZeroRollout bool
}

func newTestRelease(t *testing.T, o releaseOptions) *testRelease {
	t.Helper()
	if o.Version == "" {
		o.Version = "0.0.2"
	}
	if o.Channel == "" {
		o.Channel = ChannelStable
	}
	if o.ArtifactBytes == nil {
		o.ArtifactBytes = []byte("reelbolt-runner fake build 0.0.2")
	}
	if !o.ZeroRollout && o.RolloutPercent == 0 {
		o.RolloutPercent = 100
	}
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("generate a release key: %v", err)
	}

	sum := sha256.Sum256(o.ArtifactBytes)
	m := Manifest{
		Version:        o.Version,
		MinSupported:   o.MinSupported,
		Channel:        o.Channel,
		RolloutPercent: o.RolloutPercent,
		Artifacts: []Artifact{{
			OS:     "linux",
			Arch:   "amd64",
			Format: FormatTarGz,
			URL:    "",
			SHA256: hex.EncodeToString(sum[:]),
			Size:   int64(len(o.ArtifactBytes)),
		}},
	}
	rel := &testRelease{
		Key:              priv,
		KeySet:           KeySet{{ID: "test-release", Public: pub}},
		Manifest:         m,
		ArtifactRequests: &atomic.Int32{},
	}

	mux := http.NewServeMux()
	server := httptest.NewServer(mux)
	t.Cleanup(server.Close)
	rel.Server = server

	// The artifact URL is only knowable once the server is listening.
	m.Artifacts[0].URL = server.URL + "/runner/latest.tar.gz"
	rel.Manifest = m

	raw, err := json.Marshal(m)
	if err != nil {
		t.Fatalf("marshal the manifest: %v", err)
	}
	rel.Raw = raw
	signer := priv
	if len(o.SignWith) == ed25519.PrivateKeySize {
		signer = o.SignWith
	}
	rel.Sig = SignManifest(raw, signer)

	mux.HandleFunc("/"+o.Channel+"/manifest.json", func(w http.ResponseWriter, r *http.Request) {
		body := rel.Raw
		if o.TamperManifest {
			var tampered Manifest
			if err := json.Unmarshal(rel.Raw, &tampered); err != nil {
				t.Errorf("unmarshal the manifest: %v", err)
			}
			tampered.Version = "9.9.9"
			body, _ = json.Marshal(tampered)
		}
		_, _ = w.Write(body)
	})
	mux.HandleFunc("/"+o.Channel+"/manifest.json.sig", func(w http.ResponseWriter, r *http.Request) {
		if o.OmitSignature {
			http.NotFound(w, r)
			return
		}
		_, _ = w.Write([]byte(EncodeSignature(rel.Sig)))
	})
	mux.HandleFunc("/runner/latest.tar.gz", func(w http.ResponseWriter, r *http.Request) {
		rel.ArtifactRequests.Add(1)
		body := o.ArtifactBytes
		if o.Tamper {
			body = append([]byte("evil"), o.ArtifactBytes...)
		}
		w.Header().Set("Content-Length", itoa(len(body)))
		_, _ = w.Write(body)
	})

	rel.Artifact = o.ArtifactBytes
	rel.BaseURL = server.URL
	rel.Host = mustHost(t, server.URL)
	return rel
}

// hostFor points an update Host at a test release.
func (r *testRelease) newHost() *Host {
	return &Host{BaseURL: r.BaseURL, Channel: r.Manifest.Channel, DevHosts: []string{r.Host}}
}

func mustHost(t *testing.T, raw string) string {
	t.Helper()
	u, err := url.Parse(raw)
	if err != nil {
		t.Fatalf("parse %q: %v", raw, err)
	}
	return u.Host
}

// writeFakeBinary writes a stand-in for the installed runner and returns its
// path. The content is checked byte for byte after an update, so the tests never
// depend on a real binary.
func writeFakeBinary(t *testing.T, dir, name, body string) string {
	t.Helper()
	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, []byte(body), 0o755); err != nil {
		t.Fatalf("write %s: %v", path, err)
	}
	return path
}

// itoa is strconv.Itoa, kept local so the helper file has no more imports than
// it needs.
func itoa(n int) string { return strconv.Itoa(n) }

// marshalJSON is json.Marshal, named so a test reads as what it is doing.
func marshalJSON(v any) ([]byte, error) { return json.Marshal(v) }

func minimalManifestJSON() string {
	return minimalManifest() + strings.Repeat("a", 64) + manifestTail()
}

// The two halves are split only so a 64-character digest can be inserted
// without writing it out by hand.
func minimalManifest() string {
	return `{"version":"1.0.0","channel":"stable","rolloutPercent":100,` +
		`"artifacts":[{"os":"linux","arch":"amd64","format":"tarball",` +
		`"url":"https://downloads.reelbolt.ai/x","sha256":"`
}

func manifestTail() string { return `","size":1}]}` }

// shaOf is the hex SHA-256 of a byte slice, for building artifact entries.
func shaOf(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}

func hexOf(b []byte) string    { return hex.EncodeToString(b) }
func base64Of(b []byte) string { return base64.StdEncoding.EncodeToString(b) }
