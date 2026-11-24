package protocol

import (
	"crypto/ed25519"
	"crypto/x509"
	"encoding/base64"
	"encoding/pem"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"
)

// HelloSignaturePrefix is the domain separation prefix of the hello signature.
// The signature covers:
//
//	reelbolt-runner-hello-v1|<deviceId>|<ts>|<nonce>|<gatewayHost>
//
// Binding the gateway host stops a hostile gateway replaying the hello at
// another one (docs/design/runner-protocol-adr.md section 5). The construction
// is shared with the pairing endpoint's token issue (E4b), so there is one
// routine to get right.
const HelloSignaturePrefix = "reelbolt-runner-hello-v1"

// HelloSigningString builds the exact UTF-8 string signed in hello.auth.sig.
// gatewayHost must already be normalised (see NormalizeGatewayHost).
func HelloSigningString(deviceID, ts, nonce, gatewayHost string) string {
	return strings.Join([]string{HelloSignaturePrefix, deviceID, ts, nonce, gatewayHost}, "|")
}

// SignHello signs the hello construction with a device's private key, for the
// runner side (the worker library, the desktop app and tests).
func SignHello(priv ed25519.PrivateKey, deviceID, ts, nonce, gatewayHost string) string {
	return base64.RawURLEncoding.EncodeToString(
		ed25519.Sign(priv, []byte(HelloSigningString(deviceID, ts, nonce, gatewayHost))))
}

// EncodeDevicePublicKey returns the canonical encoding of a device public key
// for runner_devices.public_key: the 32 raw Ed25519 bytes in standard base64,
// matching the column's documented shape ("Base64 Ed25519 public key registered
// at pairing"). The pairing flow (E4a/E4b) stores this form, and
// public_key_fingerprint is the SHA-256 of whatever string it stores.
func EncodeDevicePublicKey(pub ed25519.PublicKey) string {
	return base64.StdEncoding.EncodeToString(pub)
}

// ParseDevicePublicKey decodes runner_devices.public_key.
//
// The canonical form is EncodeDevicePublicKey's, but a pairing endpoint written
// in another runtime may store standard base64, padded base64 or a PEM
// SubjectPublicKeyInfo block instead. All four are accepted so the gateway does
// not silently reject every device of a deployment whose pairing code chose a
// different spelling; nothing else about the key changes.
func ParseDevicePublicKey(encoded string) (ed25519.PublicKey, error) {
	encoded = strings.TrimSpace(encoded)
	if encoded == "" {
		return nil, errors.New("protocol: empty device public key")
	}
	if block, _ := pem.Decode([]byte(encoded)); block != nil {
		parsed, err := x509.ParsePKIXPublicKey(block.Bytes)
		if err != nil {
			return nil, fmt.Errorf("protocol: parse device public key PEM: %w", err)
		}
		pub, ok := parsed.(ed25519.PublicKey)
		if !ok {
			return nil, fmt.Errorf("protocol: device public key is %T, not Ed25519", parsed)
		}
		return pub, nil
	}
	for _, enc := range []*base64.Encoding{
		base64.RawURLEncoding, base64.URLEncoding, base64.RawStdEncoding, base64.StdEncoding,
	} {
		raw, err := enc.DecodeString(encoded)
		if err != nil {
			continue
		}
		if len(raw) != ed25519.PublicKeySize {
			return nil, fmt.Errorf("protocol: device public key is %d bytes, want %d", len(raw), ed25519.PublicKeySize)
		}
		return ed25519.PublicKey(raw), nil
	}
	return nil, errors.New("protocol: device public key is not valid base64")
}

// VerifyHelloSignature checks a hello signature against the device's registered
// public key. The signature is base64url (the specification's encoding); padded
// and standard base64 are accepted for the same reason as above.
func VerifyHelloSignature(publicKey, deviceID, ts, nonce, gatewayHost, sig string) error {
	pub, err := ParseDevicePublicKey(publicKey)
	if err != nil {
		return err
	}
	raw, err := decodeSignature(sig)
	if err != nil {
		return err
	}
	if !ed25519.Verify(pub, []byte(HelloSigningString(deviceID, ts, nonce, gatewayHost)), raw) {
		return errors.New("protocol: hello signature does not verify")
	}
	return nil
}

func decodeSignature(sig string) ([]byte, error) {
	for _, enc := range []*base64.Encoding{
		base64.RawURLEncoding, base64.URLEncoding, base64.RawStdEncoding, base64.StdEncoding,
	} {
		if raw, err := enc.DecodeString(strings.TrimSpace(sig)); err == nil {
			if len(raw) != ed25519.SignatureSize {
				return nil, fmt.Errorf("protocol: signature is %d bytes, want %d", len(raw), ed25519.SignatureSize)
			}
			return raw, nil
		}
	}
	return nil, errors.New("protocol: signature is not valid base64")
}

// HelloSkew reports whether a hello timestamp is inside the accepted window.
// now and ts are both UTC.
func HelloSkew(now time.Time, ts time.Time) time.Duration {
	return now.Sub(ts)
}

// NegotiateVersion reports the version the gateway answers with for a peer's
// hello.protocolVersion, and whether the peer is compatible at all.
//
// The envelope's major version is the compatibility boundary: a peer whose major
// differs is closed with 4400. Within a major, the answer is the highest minor
// both sides support ("Versioning and compatibility").
func NegotiateVersion(peer string) (string, bool) {
	major, minor, ok := parseVersion(peer)
	if !ok || major != Version {
		return "", false
	}
	_, gwMinor, ok := parseVersion(ProtocolVersion)
	if !ok {
		return ProtocolVersion, true
	}
	if minor < gwMinor {
		return strconv.Itoa(major) + "." + strconv.Itoa(minor), true
	}
	return ProtocolVersion, true
}

func parseVersion(v string) (major, minor int, ok bool) {
	v = strings.TrimSpace(v)
	if v == "" {
		return 0, 0, false
	}
	parts := strings.SplitN(v, ".", 3)
	major, err := strconv.Atoi(parts[0])
	if err != nil || major < 0 {
		return 0, 0, false
	}
	if len(parts) == 1 {
		return major, 0, true
	}
	minor, err = strconv.Atoi(parts[1])
	if err != nil || minor < 0 {
		return 0, 0, false
	}
	return major, minor, true
}
