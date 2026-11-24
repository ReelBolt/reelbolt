package runners

import (
	"encoding/json"
	"time"
)

// ---- pairing (unauthenticated, the device's side) ----

// PairStartRequest opens a pairing. All four metadata fields are required: the
// approval screen shows them to the user who is deciding whether to trust the
// machine, and os/arch are checked against the supported runner targets (D26).
type PairStartRequest struct {
	PublicKey string `json:"publicKey"`
	Name      string `json:"name"`
	Os        string `json:"os"`
	Arch      string `json:"arch"`
	Version   string `json:"version"`
}

// PairStartResponse is the RFC 8628 device authorization response. deviceCode is
// the 32-byte bearer secret, returned exactly once and stored only as a hash;
// userCode is the short code the user reads off the screen and types in.
type PairStartResponse struct {
	DeviceCode      string `json:"deviceCode"`
	UserCode        string `json:"userCode"`
	VerificationURI string `json:"verificationUri"`
	ExpiresIn       int    `json:"expiresIn"`
	Interval        int    `json:"interval"`
}

// PairPollRequest carries the device code back.
type PairPollRequest struct {
	DeviceCode string `json:"deviceCode"`
}

// PairPollResponse is the success case. Every other outcome is an RFC 8628 error
// code in the standard {"error": "..."} envelope.
type PairPollResponse struct {
	DeviceID       string `json:"deviceId"`
	OrganizationID string `json:"organizationId"`
	GatewayURL     string `json:"gatewayUrl"`
}

// ---- approval (authenticated, the user's side) ----

// PairApprovalResponse is what the approval screen shows. It deliberately carries
// nothing about which organization the device would land in — the approver chooses
// that, and the choice is checked against their own memberships.
type PairApprovalResponse struct {
	UserCode string `json:"userCode"`
	Name     string `json:"name"`
	Os       string `json:"os,omitempty"`
	Arch     string `json:"arch,omitempty"`
	Version  string `json:"version,omitempty"`
	// RequestedFromIP is the address nginx saw. Coarse geolocation is deliberately
	// not attempted: it would need a GeoIP database or a vendor lookup, and this
	// deployment ships neither.
	RequestedFromIP string    `json:"requestedFromIp,omitempty"`
	RequestedAt     time.Time `json:"requestedAt"`
	ExpiresAt       time.Time `json:"expiresAt"`
}

// ApprovePairRequest names the workspace the device is being approved into. The
// approver's authority is judged in THAT organization, not in their active one.
type ApprovePairRequest struct {
	OrganizationID string `json:"organizationId"`
}

// ApprovePairResponse confirms the device that approval created.
type ApprovePairResponse struct {
	DeviceID       string `json:"deviceId"`
	OrganizationID string `json:"organizationId"`
}

// ---- devices ----

// DeviceResponse is one paired machine. Capabilities stays raw JSON: it is written
// by the gateway from the runner's own hello and this service only relays it.
type DeviceResponse struct {
	ID            string          `json:"id"`
	Name          string          `json:"name"`
	Os            string          `json:"os,omitempty"`
	Arch          string          `json:"arch,omitempty"`
	RunnerVersion string          `json:"runnerVersion,omitempty"`
	CreatedAt     time.Time       `json:"createdAt"`
	LastSeenAt    *time.Time      `json:"lastSeenAt,omitempty"`
	RevokedAt     *time.Time      `json:"revokedAt,omitempty"`
	PairedBy      string          `json:"pairedByUserId,omitempty"`
	Capabilities  json.RawMessage `json:"capabilities,omitempty"`
}

// RevokeResponse is returned by DELETE /api/v1/runners/{id}. Revoking twice is not
// an error; AlreadyRevoked says whether this call was the one that did it.
type RevokeResponse struct {
	ID             string    `json:"id"`
	RevokedAt      time.Time `json:"revokedAt"`
	AlreadyRevoked bool      `json:"alreadyRevoked"`
}

// ---- runner token (unauthenticated, the device proving possession) ----

// RunnerTokenRequest is the device's proof of possession. ts is the runner's own
// clock as RFC 3339 and sig is Ed25519 over the exact string
// "deviceId|ts|nonce" — the literal ts and deviceId the body carries, never a
// re-formatted copy.
type RunnerTokenRequest struct {
	DeviceID string `json:"deviceId"`
	Ts       string `json:"ts"`
	Nonce    string `json:"nonce"`
	Sig      string `json:"sig"`
}

// RunnerTokenResponse is the short-lived credential the gateway accepts.
type RunnerTokenResponse struct {
	Token          string `json:"token"`
	TokenType      string `json:"tokenType"`
	ExpiresIn      int    `json:"expiresIn"`
	OrganizationID string `json:"organizationId"`
	GatewayURL     string `json:"gatewayUrl"`
}
