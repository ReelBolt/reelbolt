package update

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// signedFixture returns a manifest, its bytes and a valid signature.
func signedFixture(t *testing.T) (Manifest, []byte, []byte, KeySet) {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("keygen: %v", err)
	}
	m := Manifest{
		Version:        "1.2.3",
		MinSupported:   "1.0.0",
		Channel:        ChannelStable,
		RolloutPercent: 100,
		Artifacts: []Artifact{{
			OS: OSLinux, Arch: "amd64", Format: FormatTarGz,
			URL:    "https://downloads.reelbolt.ai/runner/x.tar.gz",
			SHA256: strings.Repeat("a", 64), Size: 100,
		}},
	}
	raw, err := json.Marshal(m)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	return m, raw, SignManifest(raw, priv), KeySet{{ID: "current", Public: pub}}
}

func TestVerifyManifestAcceptsAGenuineSignature(t *testing.T) {
	_, raw, sig, keys := signedFixture(t)
	m, err := VerifyManifest(raw, sig, keys)
	if err != nil {
		t.Fatalf("VerifyManifest: %v", err)
	}
	if m.Version != "1.2.3" {
		t.Errorf("Version = %q", m.Version)
	}
	// The base64 form the release pipeline publishes must verify too.
	if _, err := VerifyManifest(raw, []byte(EncodeSignature(sig)), keys); err != nil {
		t.Fatalf("VerifyManifest with a base64 signature: %v", err)
	}
}

func TestVerifyManifestRejectsATamperedManifest(t *testing.T) {
	_, raw, sig, keys := signedFixture(t)
	// Flip one byte inside the version string: the signature covers the exact
	// bytes that were signed, not the document they parse to.
	tampered := append([]byte(nil), raw...)
	tampered[10] = '9'
	if _, err := VerifyManifest(tampered, sig, keys); !errors.Is(err, ErrSignature) {
		t.Fatalf("a tampered manifest was accepted or failed for the wrong reason: %v", err)
	}
	// Whitespace is a byte too.
	withNewline := append(append([]byte(nil), raw...), '\n')
	if _, err := VerifyManifest(withNewline, sig, keys); !errors.Is(err, ErrSignature) {
		t.Fatalf("a manifest with trailing bytes was accepted: %v", err)
	}
}

func TestVerifyManifestRejectsATamperedSignature(t *testing.T) {
	_, raw, sig, keys := signedFixture(t)
	bad := append([]byte(nil), sig...)
	bad[0] ^= 0xff
	if _, err := VerifyManifest(raw, bad, keys); !errors.Is(err, ErrSignature) {
		t.Fatalf("a corrupted signature was accepted: %v", err)
	}
	for _, truncated := range [][]byte{nil, sig[:32], []byte("not base64 at all")} {
		if _, err := VerifyManifest(raw, truncated, keys); err == nil {
			t.Fatalf("a malformed signature (%d bytes) was accepted", len(truncated))
		}
	}
}

func TestVerifyManifestRejectsTheWrongKey(t *testing.T) {
	_, raw, sig, _ := signedFixture(t)
	otherPub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("keygen: %v", err)
	}
	if _, err := VerifyManifest(raw, sig, KeySet{{ID: "other", Public: otherPub}}); !errors.Is(err, ErrSignature) {
		t.Fatalf("a manifest signed by an unknown key was accepted: %v", err)
	}
}

func TestVerifyManifestFailsClosedWithoutKeys(t *testing.T) {
	_, raw, sig, _ := signedFixture(t)
	for _, keys := range []KeySet{nil, {}} {
		if _, err := VerifyManifest(raw, sig, keys); !errors.Is(err, ErrNoTrustedKey) {
			t.Fatalf("an empty key set produced %v, want ErrNoTrustedKey", err)
		}
	}
	// A key of the wrong length is a misconfiguration, not a reason to compare
	// against something shorter.
	if _, err := VerifyManifest(raw, sig, KeySet{{ID: "short", Public: []byte("nope")}}); err == nil {
		t.Fatal("a malformed public key was accepted")
	}
}

func TestVerifiedManifestMustAlsoParse(t *testing.T) {
	// A correctly signed document that is not a valid manifest is refused: the
	// signature proves who wrote it, not that it is usable.
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("keygen: %v", err)
	}
	raw := []byte(`{"version":"not-a-version","rolloutPercent":100,"artifacts":[]}`)
	sig := SignManifest(raw, priv)
	if _, err := VerifyManifest(raw, sig, KeySet{{Public: pub}}); err == nil {
		t.Fatal("a signed but invalid manifest was accepted")
	}
}

func TestSignatureIsDomainSeparated(t *testing.T) {
	// The signed message carries a context prefix, so a signature produced over
	// the same bytes for any other purpose by the same key cannot be replayed
	// as a manifest signature.
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("keygen: %v", err)
	}
	raw := []byte(minimalManifestJSON())
	naive := ed25519.Sign(priv, raw)
	if _, err := VerifyManifest(raw, naive, KeySet{{Public: pub}}); !errors.Is(err, ErrSignature) {
		t.Fatalf("a signature over the bare bytes was accepted: %v", err)
	}
	if _, err := VerifyManifest(raw, SignManifest(raw, priv), KeySet{{Public: pub}}); err != nil {
		t.Fatalf("the domain-separated signature was refused: %v", err)
	}
}

func TestTwoKeysAllowRotation(t *testing.T) {
	pubOld, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("keygen: %v", err)
	}
	pubNew, privNew, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("keygen: %v", err)
	}
	raw := []byte(minimalManifestJSON())
	keys := KeySet{{ID: "current", Public: pubOld}, {ID: "next", Public: pubNew}}
	if _, err := VerifyManifest(raw, SignManifest(raw, privNew), keys); err != nil {
		t.Fatalf("a manifest signed by the next key was refused: %v", err)
	}
}

func TestParsePublicKeyFormats(t *testing.T) {
	pub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("keygen: %v", err)
	}
	hexKey := hexOf(pub)
	for _, spec := range []string{hexKey, "release-current:" + hexKey, base64Of(pub)} {
		key, err := ParsePublicKey(spec)
		if err != nil {
			t.Fatalf("ParsePublicKey(%q): %v", spec, err)
		}
		if !key.Public.Equal(pub) {
			t.Errorf("ParsePublicKey(%q) round-tripped to a different key", spec)
		}
	}
	for _, bad := range []string{"", "zzzz", "abcd"} {
		if _, err := ParsePublicKey(bad); err == nil {
			t.Errorf("ParsePublicKey(%q) was accepted", bad)
		}
	}
}

func TestTrustedKeysFailsClosedInThisBuild(t *testing.T) {
	// This repository holds no release key, so the shipped trust root is empty
	// and every update is refused. The test asserts the failure is explicit
	// rather than an empty key set that would verify nothing.
	if ReleaseKeyCurrentHex != "" || ReleaseKeyNextHex != "" {
		t.Skip("this build was given a release key")
	}
	if _, err := TrustedKeys(); !errors.Is(err, ErrNoTrustedKey) {
		t.Fatalf("TrustedKeys = %v, want ErrNoTrustedKey", err)
	}
}

func TestVerifyArtifactFile(t *testing.T) {
	dir := t.TempDir()
	body := []byte("a verified artifact")
	path := writeFakeBinary(t, dir, "artifact", string(body))
	good := Artifact{
		OS: OSLinux, Arch: "amd64", Format: FormatTarGz,
		URL: "https://downloads.reelbolt.ai/x", SHA256: shaOf(body), Size: int64(len(body)),
	}
	if err := VerifyArtifactFile(path, good); err != nil {
		t.Fatalf("VerifyArtifactFile on a matching file: %v", err)
	}

	// One byte different is a different artifact.
	if err := os.WriteFile(path, []byte("a verified artifacT"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := VerifyArtifactFile(path, good); !errors.Is(err, ErrDigest) {
		t.Fatalf("a tampered artifact produced %v, want ErrDigest", err)
	}

	// Truncation is the common accident and is caught by the size check.
	if err := os.WriteFile(path, body[:5], 0o644); err != nil {
		t.Fatal(err)
	}
	if err := VerifyArtifactFile(path, good); !errors.Is(err, ErrDigest) {
		t.Fatalf("a truncated artifact produced %v, want ErrDigest", err)
	}

	// A file that is longer than the manifest says is a mismatch too, even when
	// it starts with the right bytes.
	if err := os.WriteFile(path, append(append([]byte(nil), body...), '!'), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := VerifyArtifactFile(path, good); !errors.Is(err, ErrDigest) {
		t.Fatalf("an extended artifact produced %v, want ErrDigest", err)
	}

	if err := VerifyArtifactFile(filepath.Join(dir, "missing"), good); err == nil {
		t.Fatal("a missing artifact verified")
	}
	if err := os.Mkdir(filepath.Join(dir, "adir"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := VerifyArtifactFile(filepath.Join(dir, "adir"), good); err == nil {
		t.Fatal("a directory verified as an artifact")
	}
}
