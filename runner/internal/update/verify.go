package update

import (
	"crypto/ed25519"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"
)

// Errors a caller distinguishes. Every one of them means "the update was not
// applied"; none of them means "the runner is broken".
var (
	// ErrNoTrustedKey means this build carries no release public key, so it
	// cannot verify anything and refuses to update. It is the state of every
	// build produced before the release pipeline (E10b) provisions a key.
	ErrNoTrustedKey = errors.New("update: this build has no release public key compiled in, so it refuses to update")
	// ErrSignature means the detached signature did not verify against any
	// trusted key.
	ErrSignature = errors.New("update: the manifest signature does not verify")
	// ErrDigest means the artifact on disk is not the artifact the manifest
	// describes.
	ErrDigest = errors.New("update: the artifact does not match the signed manifest")
)

// signatureContext is mixed into every signed message. Signing raw manifest
// bytes alone would let a signature produced for some other purpose by the same
// key be replayed as a manifest; the domain prefix makes the two domains
// disjoint.
const signatureContext = "reelbolt-runner-manifest-v1"

// signedMessage is exactly what the release pipeline signs.
func signedMessage(raw []byte) []byte {
	msg := make([]byte, 0, len(signatureContext)+1+len(raw))
	msg = append(msg, signatureContext...)
	msg = append(msg, 0)
	return append(msg, raw...)
}

// Key is one trusted release public key. ID is for the log ("which key signed
// this") and never participates in verification.
type Key struct {
	ID     string
	Public ed25519.PublicKey
}

// KeySet is the set of keys an update is allowed to be signed by: the current
// release key and the next one, so a rotation can be staged by shipping the next
// key one release before it is used. Any key in the set is sufficient; all of
// them are tried, and none of them is optional.
type KeySet []Key

// Validate rejects a key set that could not verify anything, so the failure is
// "no trusted key" rather than a signature mismatch that looks like tampering.
func (k KeySet) Validate() error {
	if len(k) == 0 {
		return ErrNoTrustedKey
	}
	for i, key := range k {
		if len(key.Public) != ed25519.PublicKeySize {
			return fmt.Errorf("update: trusted key %d (%s) is %d bytes, expected %d",
				i, key.ID, len(key.Public), ed25519.PublicKeySize)
		}
	}
	return nil
}

// VerifyManifest verifies the detached signature and then parses the manifest.
// It is the only way a manifest enters this package's apply path.
//
// Order matters: the signature is checked over the exact bytes that are then
// parsed, so a document cannot be signed in one shape and parsed in another.
func VerifyManifest(raw, sig []byte, keys KeySet) (*Manifest, error) {
	if err := keys.Validate(); err != nil {
		return nil, err
	}
	decoded, err := decodeSignature(sig)
	if err != nil {
		return nil, err
	}
	for _, key := range keys {
		if ed25519.Verify(key.Public, signedMessage(raw), decoded) {
			m, err := ParseManifest(raw)
			if err != nil {
				return nil, err
			}
			return m, nil
		}
	}
	// Deliberately one error for "wrong key", "tampered manifest" and "signed by
	// a key we do not ship": the runner has nothing useful to tell them apart
	// with, and telling an attacker which half they got right is a gift.
	return nil, fmt.Errorf("%w (%d trusted key(s) tried)", ErrSignature, len(keys))
}

// decodeSignature accepts the detached signature file as base64 (standard or
// raw, padded or not, surrounding whitespace ignored) or as raw bytes when the
// file is exactly one signature long. Anything else is refused.
func decodeSignature(sig []byte) ([]byte, error) {
	if len(sig) == 0 {
		return nil, errors.New("update: the signature file is empty")
	}
	if len(sig) > MaxSignatureBytes {
		return nil, fmt.Errorf("update: the signature is %d bytes, over the %d-byte limit", len(sig), MaxSignatureBytes)
	}
	if len(sig) == ed25519.SignatureSize {
		return sig, nil
	}
	trimmed := strings.TrimSpace(string(sig))
	for _, enc := range []*base64.Encoding{base64.StdEncoding, base64.RawStdEncoding} {
		if raw, err := enc.DecodeString(trimmed); err == nil {
			if len(raw) != ed25519.SignatureSize {
				return nil, fmt.Errorf("update: the signature decodes to %d bytes, expected %d",
					len(raw), ed25519.SignatureSize)
			}
			return raw, nil
		}
	}
	return nil, errors.New("update: the signature is neither base64 nor a raw 64-byte Ed25519 signature")
}

// EncodeSignature is the inverse of decodeSignature, for the release pipeline
// and for tests.
func EncodeSignature(sig []byte) string {
	return base64.StdEncoding.EncodeToString(sig) + "\n"
}

// SignManifest produces the detached signature a release publishes. The private
// key is offline (or held by the release environment); nothing in the shipped
// runner holds it, and this function exists so the pipeline and the tests use
// one definition of what is signed.
func SignManifest(raw []byte, priv ed25519.PrivateKey) []byte {
	return ed25519.Sign(priv, signedMessage(raw))
}

// HashFile returns the lowercase hex SHA-256 of a file.
func HashFile(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", fmt.Errorf("update: open %s: %w", path, err)
	}
	defer func() { _ = f.Close() }()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", fmt.Errorf("update: read %s: %w", path, err)
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// VerifyArtifactFile checks a file on disk against the manifest's entry for it:
// size first (cheap, and a truncated download is the common accident), then the
// SHA-256. Both must match exactly; a mismatch is ErrDigest and the file must be
// deleted rather than retried from where it stopped.
func VerifyArtifactFile(path string, a Artifact) error {
	if err := a.validate(); err != nil {
		return fmt.Errorf("update: artifact %s: %w", a.Key(), err)
	}
	info, err := os.Stat(path)
	if err != nil {
		return fmt.Errorf("update: %w", err)
	}
	if !info.Mode().IsRegular() {
		return fmt.Errorf("update: %s is not a regular file", path)
	}
	if info.Size() != a.Size {
		return fmt.Errorf("%w: %s is %d bytes, the manifest says %d",
			ErrDigest, path, info.Size(), a.Size)
	}
	sum, err := HashFile(path)
	if err != nil {
		return err
	}
	if !equalHex(sum, a.SHA256) {
		return fmt.Errorf("%w: %s hashes to %s, the manifest says %s",
			ErrDigest, path, sum, strings.ToLower(a.SHA256))
	}
	return nil
}

// equalHex compares two hex digests without leaking where they differ.
func equalHex(a, b string) bool {
	a = strings.ToLower(strings.TrimSpace(a))
	b = strings.ToLower(strings.TrimSpace(b))
	return subtle.ConstantTimeCompare([]byte(a), []byte(b)) == 1
}
