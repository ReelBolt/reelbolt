// Package deviceapi is the desktop app's client for the Inference API's device
// routes (/api/v1/device/**), the server half of computer folder sync
// (docs/design/local-folder-sync-and-storage.md, "Device authentication" and
// "Device API").
//
// There is no bearer token. Every request is signed with the device's Ed25519
// key (internal/identity) over a string that binds the device, a timestamp, a
// single-use nonce, the method, the exact path and query sent, and the SHA-256
// of the body. The server refuses a timestamp more than 120 s away, a nonce it
// has seen, and anything whose signature does not verify against the public key
// it stored at pairing.
//
// Two things are never logged and never put into an error: a signature, and the
// query string of a presigned storage URL (which IS a credential). Errors name
// the route, the status and the server's machine code only.
package deviceapi

import (
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// Header names, as the server reads them.
const (
	HeaderDevice    = "X-ReelBolt-Device"
	HeaderTimestamp = "X-ReelBolt-Timestamp"
	HeaderNonce     = "X-ReelBolt-Nonce"
	HeaderSignature = "X-ReelBolt-Signature"
)

// SignaturePrefix versions the signed string. A server that changes the
// construction changes this prefix, so an old client fails closed.
const SignaturePrefix = "reelbolt-device-request-v1"

// nonceBytes is the nonce's entropy. The server requires at least 16.
const nonceBytes = 16

// SigningString is the exact UTF-8 string a request's signature covers:
//
//	reelbolt-device-request-v1|<deviceId>|<timestamp>|<nonce>|<METHOD>|<path?query>|<hex sha256(body)>
//
// pathAndQuery must be what goes on the wire (url.URL.RequestURI()), and an
// empty body hashes as SHA-256 of "".
func SigningString(deviceID, timestamp, nonce, method, pathAndQuery string, body []byte) string {
	sum := sha256.Sum256(body)
	return strings.Join([]string{
		SignaturePrefix,
		deviceID,
		timestamp,
		nonce,
		strings.ToUpper(method),
		pathAndQuery,
		hex.EncodeToString(sum[:]),
	}, "|")
}

// Signer signs requests as one device.
type Signer struct {
	DeviceID string
	Key      ed25519.PrivateKey
	// Now and Rand are injectable so a test can produce a golden vector.
	Now  func() time.Time
	Rand io.Reader
}

// Headers computes the four authentication headers for one request.
func (s *Signer) Headers(method, pathAndQuery string, body []byte) (map[string]string, error) {
	if s.DeviceID == "" {
		return nil, fmt.Errorf("deviceapi: no device id")
	}
	if len(s.Key) != ed25519.PrivateKeySize {
		return nil, fmt.Errorf("deviceapi: no device key")
	}
	now := time.Now
	if s.Now != nil {
		now = s.Now
	}
	rnd := rand.Reader
	if s.Rand != nil {
		rnd = s.Rand
	}
	raw := make([]byte, nonceBytes)
	if _, err := io.ReadFull(rnd, raw); err != nil {
		return nil, fmt.Errorf("deviceapi: nonce: %w", err)
	}
	ts := now().UTC().Truncate(time.Second).Format(time.RFC3339)
	nonce := base64.RawURLEncoding.EncodeToString(raw)
	msg := SigningString(s.DeviceID, ts, nonce, method, pathAndQuery, body)
	sig := ed25519.Sign(s.Key, []byte(msg))
	return map[string]string{
		HeaderDevice:    s.DeviceID,
		HeaderTimestamp: ts,
		HeaderNonce:     nonce,
		HeaderSignature: base64.RawURLEncoding.EncodeToString(sig),
	}, nil
}

// Sign sets the headers on a request whose body is body. The path and query
// signed are the request's own RequestURI, which is what net/http sends.
func (s *Signer) Sign(req *http.Request, body []byte) error {
	h, err := s.Headers(req.Method, req.URL.RequestURI(), body)
	if err != nil {
		return err
	}
	for k, v := range h {
		req.Header.Set(k, v)
	}
	return nil
}

// Verify checks a signature the way the server does. The desktop app never
// needs it; the tests and the golden vector do.
func Verify(pub ed25519.PublicKey, deviceID, timestamp, nonce, method, pathAndQuery string, body []byte, signature string) bool {
	sig, err := base64.RawURLEncoding.DecodeString(strings.TrimRight(signature, "="))
	if err != nil {
		return false
	}
	return ed25519.Verify(pub, []byte(SigningString(deviceID, timestamp, nonce, method, pathAndQuery, body)), sig)
}
