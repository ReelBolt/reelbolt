package update

import (
	"crypto/ed25519"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"os"
	"strings"
)

// The release public keys, stamped into the binary by the release pipeline:
//
//	go build -tags release -ldflags '-X ...ReleaseKeyCurrentHex=<id>:<hex>'
//
// E10b owns producing them. THIS REPOSITORY HOLDS NO KEY: both are empty in
// every build made from this source tree, and an empty key set means
// TrustedKeys returns ErrNoTrustedKey, which means the runner refuses to
// update. That is the intended state of the code today, not a stub — there is
// deliberately no fallback key, no "trust the host over HTTPS instead" path,
// and no default that verifies nothing.
//
// There are two slots because key rotation needs one: ship release N signed by
// CURRENT and carrying NEXT, and release N+1 signed by NEXT. A runner on either
// build accepts either signature, so a rotation never strands a machine.
var (
	// ReleaseKeyCurrentHex is the key this release is signed with.
	ReleaseKeyCurrentHex = ""
	// ReleaseKeyNextHex is the key the next release will be signed with.
	ReleaseKeyNextHex = ""
)

// DevTrustKeyEnv names an extra public key a NON-RELEASE build may trust.
//
// It exists because an update pipeline cannot be developed against a key that
// does not exist yet. A release build (-tags release) ignores the variable
// entirely — see trust_release.go — so this is not a way to point a shipped
// runner at a key of your choosing.
const DevTrustKeyEnv = "REELBOLT_UPDATE_TRUST_KEY"

// TrustedKeys returns the key set an update may be signed by.
//
// It fails closed: with no key compiled in (and, in a non-release build, no
// development key configured) it returns ErrNoTrustedKey rather than an empty
// set that would quietly verify nothing.
func TrustedKeys() (KeySet, error) {
	var keys KeySet
	for _, spec := range []struct{ id, raw string }{
		{"release-current", ReleaseKeyCurrentHex},
		{"release-next", ReleaseKeyNextHex},
	} {
		if strings.TrimSpace(spec.raw) == "" {
			continue
		}
		key, err := ParsePublicKey(spec.raw)
		if err != nil {
			return nil, fmt.Errorf("update: the compiled-in %s key is unusable: %w", spec.id, err)
		}
		key.ID = spec.id
		keys = append(keys, key)
	}
	if devTrustAllowed {
		if raw := strings.TrimSpace(os.Getenv(DevTrustKeyEnv)); raw != "" {
			key, err := ParsePublicKey(raw)
			if err != nil {
				return nil, fmt.Errorf("update: %s is unusable: %w", DevTrustKeyEnv, err)
			}
			key.ID = "dev"
			keys = append(keys, key)
		}
	}
	if err := keys.Validate(); err != nil {
		return nil, err
	}
	return keys, nil
}

// ParsePublicKey reads an Ed25519 public key written as "<id>:<hex>",
// "<id>:<base64>", or a bare hex/base64 32-byte key. The id is for the log.
func ParsePublicKey(spec string) (Key, error) {
	spec = strings.TrimSpace(spec)
	if spec == "" {
		return Key{}, fmt.Errorf("empty key")
	}
	id := ""
	if i := strings.Index(spec, ":"); i >= 0 {
		id, spec = strings.TrimSpace(spec[:i]), strings.TrimSpace(spec[i+1:])
	}
	raw, err := hex.DecodeString(spec)
	if err != nil {
		raw, err = base64.StdEncoding.DecodeString(spec)
		if err != nil {
			raw, err = base64.RawStdEncoding.DecodeString(spec)
		}
	}
	if err != nil {
		return Key{}, fmt.Errorf("not hex or base64: %w", err)
	}
	if len(raw) != ed25519.PublicKeySize {
		return Key{}, fmt.Errorf("is %d bytes, expected a %d-byte Ed25519 public key",
			len(raw), ed25519.PublicKeySize)
	}
	return Key{ID: id, Public: ed25519.PublicKey(raw)}, nil
}

// DescribeKeys names the trusted keys for `status` and `doctor`, never their
// bytes: a public key is not a secret, but printing it in a log invites someone
// to copy the wrong one.
func DescribeKeys(keys KeySet) string {
	if len(keys) == 0 {
		return "(none)"
	}
	names := make([]string, 0, len(keys))
	for _, k := range keys {
		id := k.ID
		if id == "" {
			id = "unnamed"
		}
		names = append(names, id)
	}
	return strings.Join(names, ", ")
}
