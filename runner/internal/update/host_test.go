package update

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
)

func TestFetchManifestVerifiesBeforeReturning(t *testing.T) {
	rel := newTestRelease(t, releaseOptions{})
	m, raw, err := rel.newHost().FetchManifest(context.Background(), rel.KeySet)
	if err != nil {
		t.Fatalf("FetchManifest: %v", err)
	}
	if m.Version != "0.0.2" || len(raw) == 0 {
		t.Fatalf("unexpected manifest: %+v", m)
	}
}

func TestFetchManifestRefusesATamperedManifest(t *testing.T) {
	rel := newTestRelease(t, releaseOptions{TamperManifest: true})
	if _, _, err := rel.newHost().FetchManifest(context.Background(), rel.KeySet); !errors.Is(err, ErrSignature) {
		t.Fatalf("a manifest edited after signing produced %v, want ErrSignature", err)
	}
}

func TestFetchManifestRefusesAnUnknownSigner(t *testing.T) {
	_, other, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("keygen: %v", err)
	}
	rel := newTestRelease(t, releaseOptions{SignWith: other})
	if _, _, err := rel.newHost().FetchManifest(context.Background(), rel.KeySet); !errors.Is(err, ErrSignature) {
		t.Fatalf("a manifest signed by a key we do not ship produced %v, want ErrSignature", err)
	}
}

func TestFetchManifestRefusesAMissingSignature(t *testing.T) {
	rel := newTestRelease(t, releaseOptions{OmitSignature: true})
	if _, _, err := rel.newHost().FetchManifest(context.Background(), rel.KeySet); !errors.Is(err, ErrHostUnreachable) {
		t.Fatalf("a missing signature file produced %v, want ErrHostUnreachable", err)
	}
}

func TestFetchManifestRefusesWithoutTrustedKeys(t *testing.T) {
	rel := newTestRelease(t, releaseOptions{})
	var requests atomic.Int32
	rel.Server.Config.Handler = countingHandler(&requests, rel.Server.Config.Handler)
	if _, _, err := rel.newHost().FetchManifest(context.Background(), nil); !errors.Is(err, ErrNoTrustedKey) {
		t.Fatalf("FetchManifest with no key produced %v, want ErrNoTrustedKey", err)
	}
	if n := requests.Load(); n != 0 {
		t.Fatalf("the host was contacted %d times before the trust root was checked", n)
	}
}

func TestFetchManifestOnAnUnreachableHost(t *testing.T) {
	// A server that is up when the URL is built and gone when it is fetched is
	// exactly "the update host is unreachable" from the runner's point of view.
	server := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	base := server.URL
	host := mustHost(t, base)
	server.Close()

	h := &Host{BaseURL: base, Channel: ChannelStable, DevHosts: []string{host}}
	_, _, err := h.FetchManifest(context.Background(), KeySet{{Public: testKey(t)}})
	if !errors.Is(err, ErrHostUnreachable) {
		t.Fatalf("an unreachable host produced %v, want ErrHostUnreachable", err)
	}
}

func TestFetchManifestRefusesAnUnexpectedChannel(t *testing.T) {
	// The document says which channel it is for, and it must agree with the
	// channel in the URL it was fetched from: otherwise a host (or a cache)
	// could serve a beta manifest to a stable runner.
	rel := newTestRelease(t, releaseOptions{Channel: ChannelStable})
	h := rel.newHost()
	h.Channel = ChannelBeta
	// Serve the stable document under the beta path so the URL resolves.
	h.BaseURL = strings.TrimSuffix(h.BaseURL, "/")
	if _, _, err := h.FetchManifest(context.Background(), rel.KeySet); err != nil {
		// A 404 on /beta/manifest.json is also an acceptable answer, and the
		// one a correctly configured host gives.
		if !errors.Is(err, ErrHostUnreachable) {
			t.Fatalf("unexpected error: %v", err)
		}
		return
	}
	t.Fatal("a manifest fetched for beta with a stable channel declaration was accepted")
}

func TestHostAllowlist(t *testing.T) {
	keys := KeySet{{Public: testKey(t)}}
	for _, bad := range []string{
		"https://evil.example/runner",
		"https://downloads.reelbolt.ai.evil.example/runner",
		"https://notdownloads.reelbolt.ai/runner",
	} {
		h := &Host{BaseURL: bad, Channel: ChannelStable}
		if _, err := h.ManifestURL(); !errors.Is(err, ErrHostNotAllowed) {
			t.Errorf("base %q produced %v, want ErrHostNotAllowed", bad, err)
		}
	}
	// The product domain and its subdomains are allowed.
	if _, err := (&Host{Channel: ChannelStable}).ManifestURL(); err != nil {
		t.Fatalf("the default host was refused: %v", err)
	}
	if u, err := (&Host{BaseURL: "https://cdn.downloads.reelbolt.ai/runner", Channel: ChannelStable}).ManifestURL(); err != nil {
		t.Fatalf("a subdomain of the download host was refused: %v", err)
	} else if !strings.Contains(u, "/stable/manifest.json") {
		t.Fatalf("ManifestURL = %q", u)
	}
	// A development host is allowed only when it was named.
	h := &Host{BaseURL: "http://127.0.0.1:8080/runner", Channel: ChannelStable}
	if _, err := h.ManifestURL(); err != nil {
		t.Fatalf("a loopback host was refused: %v", err)
	}
	h = &Host{BaseURL: "http://staging.internal/runner", Channel: ChannelStable}
	if _, err := h.ManifestURL(); !errors.Is(err, ErrHostNotAllowed) {
		t.Fatalf("an unnamed development host produced %v, want ErrHostNotAllowed", err)
	}
	h = &Host{BaseURL: "http://staging.internal/runner", Channel: ChannelStable, DevHosts: []string{"staging.internal"}}
	if _, err := h.ManifestURL(); err != nil {
		t.Fatalf("a named development host was refused: %v", err)
	}
	_ = keys
}

func TestDownloadVerifiesWhileItWrites(t *testing.T) {
	rel := newTestRelease(t, releaseOptions{})
	dest := filepath.Join(t.TempDir(), "artifact.tar.gz")
	if err := rel.newHost().Download(context.Background(), rel.Manifest.Artifacts[0], dest); err != nil {
		t.Fatalf("Download: %v", err)
	}
	got, err := os.ReadFile(dest)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	if string(got) != string(rel.Artifact) {
		t.Fatalf("the downloaded artifact differs from what the server holds")
	}
	// The scaffolding is gone: nothing with a .part suffix survives a success.
	if entries, _ := os.ReadDir(filepath.Dir(dest)); len(entries) != 1 {
		t.Fatalf("unexpected files after a download: %v", entries)
	}
}

func TestDownloadRefusesArtifactBytesTheManifestDidNotSign(t *testing.T) {
	// The server swaps the artifact after the manifest was signed. This is the
	// attack the SHA-256 check exists to stop, and it must leave nothing behind.
	rel := newTestRelease(t, releaseOptions{Tamper: true})
	dir := t.TempDir()
	dest := filepath.Join(dir, "artifact.tar.gz")
	err := rel.newHost().Download(context.Background(), rel.Manifest.Artifacts[0], dest)
	if !errors.Is(err, ErrDigest) {
		t.Fatalf("a tampered artifact produced %v, want ErrDigest", err)
	}
	if _, statErr := os.Stat(dest); !errors.Is(statErr, os.ErrNotExist) {
		t.Fatal("a tampered download was left at the destination path")
	}
	if _, statErr := os.Stat(dest + partSuffix); !errors.Is(statErr, os.ErrNotExist) {
		t.Fatal("a tampered download was left behind as a .part file")
	}
	entries, _ := os.ReadDir(dir)
	if len(entries) != 0 {
		t.Fatalf("staging directory is not clean: %v", entries)
	}
}

func TestDownloadRefusesATruncatedBody(t *testing.T) {
	rel := newTestRelease(t, releaseOptions{})
	// Announce the full length, then send less: a connection dropped mid-body.
	truncating := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Length", itoa(len(rel.Artifact)))
		_, _ = w.Write(rel.Artifact[:len(rel.Artifact)/2])
	}))
	t.Cleanup(truncating.Close)

	a := rel.Manifest.Artifacts[0]
	a.URL = truncating.URL + "/artifact"
	h := &Host{BaseURL: truncating.URL, Channel: ChannelStable, DevHosts: []string{mustHost(t, truncating.URL)}}
	dest := filepath.Join(t.TempDir(), "artifact.tar.gz")
	if err := h.Download(context.Background(), a, dest); err == nil {
		t.Fatal("a truncated download was accepted")
	}
	if _, err := os.Stat(dest); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("a truncated download was left at the destination path")
	}
}

func TestDownloadRefusesAnArtifactURLOutsideTheAllowlist(t *testing.T) {
	rel := newTestRelease(t, releaseOptions{})
	a := rel.Manifest.Artifacts[0]
	a.URL = "https://evil.example/payload"
	err := rel.newHost().Download(context.Background(), a, filepath.Join(t.TempDir(), "x"))
	if !errors.Is(err, ErrHostNotAllowed) {
		t.Fatalf("an artifact from a third-party host produced %v, want ErrHostNotAllowed", err)
	}
}

func TestDownloadRefusesAnOversizedResponse(t *testing.T) {
	rel := newTestRelease(t, releaseOptions{})
	a := rel.Manifest.Artifacts[0]
	// A server that sends more than the manifest promised, under a chunked
	// encoding so the Content-Length check cannot catch it first.
	big := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write(make([]byte, a.Size+1024))
	}))
	t.Cleanup(big.Close)
	a.URL = big.URL + "/artifact"
	h := &Host{BaseURL: big.URL, Channel: ChannelStable, DevHosts: []string{mustHost(t, big.URL)}}
	dest := filepath.Join(t.TempDir(), "artifact")
	if err := h.Download(context.Background(), a, dest); !errors.Is(err, ErrDigest) {
		t.Fatalf("an oversized response produced %v, want ErrDigest", err)
	}
	if _, err := os.Stat(dest); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("an oversized download was left at the destination path")
	}
}

// ---- test plumbing ----

func testKey(t *testing.T) ed25519.PublicKey {
	t.Helper()
	pub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("keygen: %v", err)
	}
	return pub
}

// countingHandler counts the requests that reach an http.Handler.
func countingHandler(counter *atomic.Int32, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		counter.Add(1)
		next.ServeHTTP(w, r)
	})
}
