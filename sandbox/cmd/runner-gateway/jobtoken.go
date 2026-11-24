package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// Job tokens ("Job token" in docs/runner-protocol.md): an HS256 JWT signed by a
// gateway-held key, claims {jobId, sessionId?, epoch, orgId, exp}, required on
// every job-scoped runner message. It is the second half of fencing: the epoch
// says which assignment a message belongs to, and the token says the sender was
// given that assignment.
//
// It is a plain compact JWS rather than a JWT library dependency, because the
// claim set is fixed by the protocol and the gateway is both the only minter and
// the only verifier.
//
// Least authority: a stolen job token covers one job at one epoch. It carries no
// organisation secret and no provider credential, and exp is no later than the
// job timeout plus five minutes.

// jobTokenHeader is the fixed JOSE header. The algorithm is part of the signed
// input, so it cannot be downgraded: verify refuses anything but HS256.
var jobTokenHeader = base64.RawURLEncoding.EncodeToString([]byte(`{"alg":"HS256","typ":"JWT"}`))

// ErrJobTokenInvalid covers every rejection: a malformed token, a bad
// signature, an expired token and a token that is not HS256. They are one error
// because the caller treats them identically — stop the runner's job with
// cancel{reason:"lease_lost"} — and because distinguishing them would tell a
// hostile runner which part of a forged token to fix.
var ErrJobTokenInvalid = errors.New("gateway: job token is invalid or expired")

// jobTokenSigner mints and verifies job tokens.
type jobTokenSigner struct {
	key []byte
}

// newJobTokenSigner builds a signer. An empty key is refused: a gateway that
// cannot sign must not hand out assignments it cannot authenticate.
func newJobTokenSigner(key []byte) (*jobTokenSigner, error) {
	if len(key) == 0 {
		return nil, errors.New("gateway: job token key is empty")
	}
	return &jobTokenSigner{key: key}, nil
}

// LoadJobTokenKey reads the configured key. When none is configured the gateway
// generates one for this process, which is correct for a single replica and
// logged as a warning otherwise: with several replicas a token minted by one is
// not accepted by another, and a runner reconnecting to a different replica
// would have to be told its lease was lost.
func LoadJobTokenKey(configured string) (key []byte, ephemeral bool, err error) {
	configured = strings.TrimSpace(configured)
	if configured == "" {
		key = make([]byte, 32)
		if _, err := rand.Read(key); err != nil {
			return nil, false, fmt.Errorf("generate job token key: %w", err)
		}
		return key, true, nil
	}
	// Accept standard base64 (with or without padding) and base64url, because
	// the key travels through a secrets manager and a deployment should not have
	// to care which alphabet it used.
	for _, enc := range []*base64.Encoding{
		base64.StdEncoding, base64.RawStdEncoding, base64.URLEncoding, base64.RawURLEncoding,
	} {
		if decoded, err := enc.DecodeString(configured); err == nil && len(decoded) >= 16 {
			return decoded, false, nil
		}
	}
	return nil, false, errors.New("gateway: RUNNER_JOB_TOKEN_KEY must be at least 16 bytes of base64")
}

// mint signs a claim set. Exp is set by the caller (no later than the job
// timeout plus five minutes).
func (s *jobTokenSigner) mint(claims protocol.JobTokenClaims) (string, error) {
	if claims.JobID == "" || claims.OrgID == "" || claims.Exp <= 0 {
		return "", errors.New("gateway: job token needs jobId, orgId and exp")
	}
	payload, err := json.Marshal(claims)
	if err != nil {
		return "", fmt.Errorf("marshal job token claims: %w", err)
	}
	signingInput := jobTokenHeader + "." + base64.RawURLEncoding.EncodeToString(payload)
	return signingInput + "." + base64.RawURLEncoding.EncodeToString(s.sign(signingInput)), nil
}

// verify checks the signature and the expiry and returns the claims.
func (s *jobTokenSigner) verify(token string, now time.Time) (protocol.JobTokenClaims, error) {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return protocol.JobTokenClaims{}, ErrJobTokenInvalid
	}
	if subtle.ConstantTimeCompare([]byte(parts[0]), []byte(jobTokenHeader)) != 1 {
		return protocol.JobTokenClaims{}, ErrJobTokenInvalid
	}
	signature, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil {
		return protocol.JobTokenClaims{}, ErrJobTokenInvalid
	}
	if !hmac.Equal(signature, s.sign(parts[0]+"."+parts[1])) {
		return protocol.JobTokenClaims{}, ErrJobTokenInvalid
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return protocol.JobTokenClaims{}, ErrJobTokenInvalid
	}
	var claims protocol.JobTokenClaims
	if err := json.Unmarshal(payload, &claims); err != nil {
		return protocol.JobTokenClaims{}, ErrJobTokenInvalid
	}
	if claims.Exp <= now.Unix() {
		return protocol.JobTokenClaims{}, ErrJobTokenInvalid
	}
	if claims.JobID == "" || claims.OrgID == "" {
		return protocol.JobTokenClaims{}, ErrJobTokenInvalid
	}
	return claims, nil
}

// sign computes the HS256 MAC over the signing input.
func (s *jobTokenSigner) sign(signingInput string) []byte {
	mac := hmac.New(sha256.New, s.key)
	mac.Write([]byte(signingInput))
	return mac.Sum(nil)
}
