// Package runners implements /api/v1/runners: pairing a desktop machine into an
// organization, minting its short-lived runner token, listing the organization's
// devices and revoking them (WP E4b, docs/design/desktop-runner-adr.md §9).
//
// Rules enforced here:
//
//   - A device in another organization, an unknown device, and a pool worker
//     (which belongs to no organization) are all 404; a member of the caller's own
//     organization whose role is too low gets 403. Authority is judged in the
//     organization named by the request, never in the caller's active one (D5).
//   - The pairing secret is single-use and short-lived: the 32-byte device code is
//     stored only as a SHA-256 hash, it expires in ten minutes, and the approved
//     result is handed to the device exactly once.
//   - A runner token carries no user identity, is scoped to one organization, and
//     is never minted for a revoked device.
package runners

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/handlers/orgs"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

const (
	// PairingTTL is the window a device code is valid for. Ten minutes is the ADR's
	// answer: long enough to walk to a browser, short enough to be useless later.
	PairingTTL = 10 * time.Minute
	// PairingInterval is the minimum gap between polls, advertised to the device.
	PairingInterval = 5 * time.Second
	// PairingSkew bounds the device's clock error against ours.
	PairingSkew = 120 * time.Second
	// NonceTTL is how long a (device, nonce) pair is remembered. It is double the
	// skew window, which is the whole span a replayed signature stays usable.
	NonceTTL = 240 * time.Second

	// userCodeAlphabet drops 0/O and 1/I, which users transcribe wrongly. It has
	// exactly 32 symbols, so one masked random byte indexes it uniformly.
	userCodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"
	userCodeLength   = 8

	maxDeviceNameLen = 200
	maxBodyBytes     = 8 << 10
	// codeAttempts bounds the retry loop around the partial unique index on a live
	// user code. A collision needs two of 2^40 codes to overlap at the same instant.
	codeAttempts = 5
)

// Handler holds the collaborators of the runner API; tests replace them.
type Handler struct {
	Store  services.RunnerStore
	Lookup services.MembershipLookup
	Mailer func() services.Mailer
	// Publish announces a revocation to the runner gateway. The production
	// implementation returns immediately and dials in the background, so an
	// unreachable broker can never delay a revocation.
	Publish func(services.RunnerDeviceRevokedEvent) error
	Now     func() time.Time
	// BaseURL returns the public origin the approval screen lives under.
	BaseURL func() string
	// GatewayURL is the wss:// endpoint handed to a paired device.
	GatewayURL func() string
	// Nonces is the replay cache for device signatures; one per process.
	Nonces *nonceCache
}

// New returns the production handler.
func New() *Handler {
	return &Handler{
		Store:      services.GormRunnerStore{},
		Lookup:     services.GormMembershipLookup{},
		Mailer:     services.DefaultMailer,
		Publish:    publishAsync,
		Now:        time.Now,
		BaseURL:    func() string { return config.Cfg.LinkBaseURL },
		GatewayURL: func() string { return config.Cfg.RunnerGatewayURL },
		Nonces:     newNonceCache(time.Now),
	}
}

// publishAsync is the production Publish: it dials RabbitMQ off the request path.
// The database write is the authority, so a failure is logged and nothing more —
// the gateway's 60-second revoked_at re-check covers it.
func publishAsync(evt services.RunnerDeviceRevokedEvent) error {
	go func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		if err := services.PublishRunnerDeviceRevoked(ctx, evt); err != nil {
			log.Printf("runners: revocation event for %s not published: %v", evt.DeviceID, err)
		}
	}()
	return nil
}

// RegisterPublicRoutes mounts the three endpoints a device reaches before it is
// trusted. They run without middleware.Auth by construction — the device has no
// session, and proving possession of its key is what mintToken is for.
func (h *Handler) RegisterPublicRoutes(r *mux.Router) {
	r.HandleFunc("/api/v1/runners/pair/start", h.startPair).Methods("POST")
	r.HandleFunc("/api/v1/runners/pair/poll", h.pollPair).Methods("POST")
	r.HandleFunc("/api/v1/runners/token", h.mintToken).Methods("POST")
}

// RegisterRoutes mounts the authenticated surface on a router that already runs
// middleware.Auth.
func (h *Handler) RegisterRoutes(r *mux.Router) {
	// Registered before /{id} so "pair" is never read as a device id.
	r.HandleFunc("/api/v1/runners/pair/{userCode}", h.getPair).Methods("GET")
	r.HandleFunc("/api/v1/runners/pair/{userCode}/approve", h.approvePair).Methods("POST")
	r.HandleFunc("/api/v1/runners/pair/{userCode}/deny", h.denyPair).Methods("POST")
	r.HandleFunc("/api/v1/runners", h.listRunners).Methods("GET")
	r.HandleFunc("/api/v1/runners/{id}", h.revokeRunner).Methods("DELETE")
}

// ---- plumbing ----

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func fail(w http.ResponseWriter, status int, msg string) {
	writeJSON(w, status, map[string]string{"error": msg})
}

func serverError(w http.ResponseWriter, what string, err error) {
	log.Printf("runners: %s: %v", what, err)
	fail(w, http.StatusInternalServerError, what)
}

func decodeBody(w http.ResponseWriter, r *http.Request, v any) bool {
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxBodyBytes)).Decode(v); err != nil {
		fail(w, http.StatusBadRequest, "invalid_body")
		return false
	}
	return true
}

// ---- codes ----

// newUserCode returns an 8-character code. userCodeAlphabet is 32 symbols, so the
// low five bits of a random byte index the alphabet with no modulo bias.
func newUserCode() (string, error) {
	buf := make([]byte, userCodeLength)
	if _, err := rand.Read(buf); err != nil {
		return "", err
	}
	out := make([]byte, userCodeLength)
	for i, b := range buf {
		out[i] = userCodeAlphabet[int(b)&0x1f]
	}
	return string(out), nil
}

// FormatUserCode renders a stored code the way the user sees it: XXXX-XXXX.
func FormatUserCode(code string) string {
	if len(code) != userCodeLength {
		return code
	}
	return code[:4] + "-" + code[4:]
}

// NormalizeUserCode accepts what a user actually types: any case, with or without
// the separating dash or spaces. It reports false for anything that is not eight
// characters of the alphabet, so a typo is refused rather than looked up.
func NormalizeUserCode(in string) (string, bool) {
	var b strings.Builder
	for _, r := range strings.ToUpper(strings.TrimSpace(in)) {
		switch {
		case r == '-' || r == ' ':
			continue
		case !strings.ContainsRune(userCodeAlphabet, r):
			return "", false
		}
		b.WriteRune(r)
	}
	s := b.String()
	if len(s) != userCodeLength {
		return "", false
	}
	return s, true
}

// newDeviceCode returns a 32-byte code as base64url. Only its hash is stored.
func newDeviceCode() (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(b), nil
}

// hashSecret is the SHA-256 (hex) a bearer secret is stored under.
func hashSecret(secret string) string {
	sum := sha256.Sum256([]byte(secret))
	return hex.EncodeToString(sum[:])
}

// normalizePublicKey decodes an Ed25519 public key and returns its canonical base64
// form plus its hex SHA-256 fingerprint. It reports false for anything that is not
// exactly 32 bytes, so a malformed key never reaches the database.
func normalizePublicKey(raw string) (key, fingerprint string, ok bool) {
	decoded, err := base64.StdEncoding.DecodeString(raw)
	if err != nil {
		decoded, err = base64.RawStdEncoding.DecodeString(raw)
		if err != nil {
			return "", "", false
		}
	}
	if len(decoded) != ed25519.PublicKeySize {
		return "", "", false
	}
	sum := sha256.Sum256(decoded)
	return base64.StdEncoding.EncodeToString(decoded), hex.EncodeToString(sum[:]), true
}

// verifyDeviceSignature checks sig over the literal "deviceId|ts|nonce" the request
// carried. The ts is verified as sent, never re-formatted: the device signed bytes,
// so the bytes are what must be signed back.
//
// This framing deliberately differs from the gateway's hello signature
// (docs/runner-protocol.md prefixes it with reelbolt-runner-hello-v1 and binds the
// gateway host). Two endpoints, two replay surfaces, two signed strings.
func verifyDeviceSignature(pubKeyB64, deviceID, ts, nonce, sigB64 string) bool {
	key, err := base64.StdEncoding.DecodeString(pubKeyB64)
	if err != nil || len(key) != ed25519.PublicKeySize {
		return false
	}
	sig, err := base64.RawURLEncoding.DecodeString(strings.TrimRight(sigB64, "="))
	if err != nil || len(sig) != ed25519.SignatureSize {
		return false
	}
	return ed25519.Verify(ed25519.PublicKey(key), []byte(deviceID+"|"+ts+"|"+nonce), sig)
}

// runnerPlatformAllowed reports whether this OS/arch is a runner target. Decision
// D26 locks the set to Windows x64 and Linux x64/arm64 — macOS is not supported
// anywhere, so a macOS device is refused at pairing rather than paired into a
// product that has nothing to run it.
func runnerPlatformAllowed(osName, arch string) bool {
	switch osName {
	case "windows":
		return arch == "amd64"
	case "linux":
		return arch == "amd64" || arch == "arm64"
	default:
		return false
	}
}

// ---- replay cache ----

type nonceKey struct{ device, nonce string }

// nonceCache remembers (device, nonce) pairs for NonceTTL, so a signature captured
// off the wire cannot be replayed inside the skew window it is still valid for.
// It is per-process: a deployment with several API replicas bounds a replay to the
// replica the attacker does not hit, and the gateway — one socket per device, one
// process — is the authoritative replay boundary.
type nonceCache struct {
	mu   sync.Mutex
	now  func() time.Time
	seen map[nonceKey]time.Time
}

func newNonceCache(now func() time.Time) *nonceCache {
	return &nonceCache{now: now, seen: map[nonceKey]time.Time{}}
}

// remember records the pair and reports whether it was new. Only a verified
// signature is ever recorded, so an attacker cannot poison the cache.
func (c *nonceCache) remember(device, nonce string) bool {
	if c == nil {
		// No cache configured: the signature and skew checks still stand, and the
		// gateway remains the authoritative replay boundary.
		return true
	}
	now := c.now()
	c.mu.Lock()
	defer c.mu.Unlock()

	for k, at := range c.seen {
		if now.Sub(at) > NonceTTL {
			delete(c.seen, k)
		}
	}

	k := nonceKey{device: device, nonce: nonce}
	if _, ok := c.seen[k]; ok {
		return false
	}
	c.seen[k] = now
	return true
}

// ---- callers ----

type caller struct {
	userID    uuid.UUID
	activeOrg uuid.UUID
	role      string
}

func callerOf(w http.ResponseWriter, r *http.Request) (caller, bool) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		fail(w, http.StatusUnauthorized, "unauthorized")
		return caller{}, false
	}
	id, err := uuid.Parse(uc.UserID)
	if err != nil {
		fail(w, http.StatusUnauthorized, "unauthorized")
		return caller{}, false
	}
	return caller{userID: id, activeOrg: uc.OrganizationID, role: uc.OrgRole}, true
}

func (h *Handler) now() time.Time { return h.Now() }

// optional returns a pointer to s, or nil for a blank string, matching the nullable
// columns the schema uses for device metadata the runner need not send.
func optional(s string) *string {
	s = strings.TrimSpace(s)
	if s == "" {
		return nil
	}
	return &s
}

// ---- POST /api/v1/runners/pair/start (unauthenticated) ----

func (h *Handler) startPair(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	now := h.now()

	// No authentication here at all, so the per-IP cap is the only bound on how
	// fast one host can fill runner_pairing_requests.
	if ip := middleware.ClientIP(r); ip != "" && !middleware.NewRunnerPairStartLimiter(h.Store).Allow(ip, now) {
		fail(w, http.StatusTooManyRequests, "too_many_requests")
		return
	}

	var req PairStartRequest
	if !decodeBody(w, r, &req) {
		return
	}

	name := strings.TrimSpace(req.Name)
	if name == "" || len(name) > maxDeviceNameLen {
		fail(w, http.StatusBadRequest, "invalid_name")
		return
	}
	if !runnerPlatformAllowed(req.Os, req.Arch) {
		fail(w, http.StatusBadRequest, "unsupported_platform")
		return
	}
	publicKey, fingerprint, ok := normalizePublicKey(req.PublicKey)
	if !ok {
		fail(w, http.StatusBadRequest, "invalid_public_key")
		return
	}

	// One key, one device. Refusing here means the user is never shown a code that
	// could only fail at approval time.
	existing, err := h.Store.FindDeviceByFingerprint(ctx, fingerprint)
	if err != nil {
		serverError(w, "lookup device", err)
		return
	}
	if existing != nil {
		fail(w, http.StatusConflict, "already_paired")
		return
	}

	// Settle every request whose window closed, so the partial unique index on the
	// user code constrains the live set rather than every code ever issued.
	if err := h.Store.SweepExpiredPairingRequests(ctx, now); err != nil {
		serverError(w, "sweep pairing requests", err)
		return
	}

	expires := now.Add(PairingTTL)
	for attempt := 0; attempt < codeAttempts; attempt++ {
		deviceCode, genErr := newDeviceCode()
		if genErr != nil {
			serverError(w, "generate device code", genErr)
			return
		}
		userCode, genErr := newUserCode()
		if genErr != nil {
			serverError(w, "generate user code", genErr)
			return
		}

		pairing := &models.RunnerPairingRequest{
			ID:                   uuid.New(),
			DeviceCodeHash:       hashSecret(deviceCode),
			UserCode:             userCode,
			PublicKey:            publicKey,
			PublicKeyFingerprint: fingerprint,
			Name:                 name,
			Os:                   optional(req.Os),
			Arch:                 optional(req.Arch),
			RunnerVersion:        optional(req.Version),
			RequestIP:            optional(middleware.ClientIP(r)),
			CreatedAt:            now,
			ExpiresAt:            expires,
			Status:               models.RunnerPairingPending,
		}

		err = h.Store.CreatePairingRequest(ctx, pairing)
		if err == nil {
			writeJSON(w, http.StatusOK, PairStartResponse{
				DeviceCode:      deviceCode,
				UserCode:        FormatUserCode(userCode),
				VerificationURI: strings.TrimRight(h.BaseURL(), "/") + "/app/runners/pair",
				ExpiresIn:       int(PairingTTL / time.Second),
				Interval:        int(PairingInterval / time.Second),
			})
			return
		}
		if !services.IsUniqueViolation(err) {
			serverError(w, "create pairing request", err)
			return
		}
		// Both the device code and the user code are unique: retry with fresh ones.
	}
	serverError(w, "create pairing request", errors.New("no free user code"))
}

// ---- POST /api/v1/runners/pair/poll (unauthenticated) ----

func (h *Handler) pollPair(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	now := h.now()

	var req PairPollRequest
	if !decodeBody(w, r, &req) {
		return
	}
	if req.DeviceCode == "" {
		fail(w, http.StatusBadRequest, "invalid_request")
		return
	}

	pairing, err := h.Store.FindPairingRequestByDeviceCodeHash(ctx, hashSecret(req.DeviceCode))
	if err != nil {
		serverError(w, "lookup pairing request", err)
		return
	}
	// An unknown device code and a spent one answer the same way, so this endpoint
	// never becomes an oracle for which codes exist.
	if pairing == nil {
		fail(w, http.StatusBadRequest, "expired_token")
		return
	}

	switch pairing.Status {
	case models.RunnerPairingPending:
		if !now.Before(pairing.ExpiresAt) {
			if err := h.Store.SweepExpiredPairingRequests(ctx, now); err != nil {
				serverError(w, "expire pairing request", err)
				return
			}
			fail(w, http.StatusBadRequest, "expired_token")
			return
		}
		// slow_down does not extend the window: a device that polls too fast never
		// advances its own last-polled clock, which is the point of the signal.
		if pairing.LastPolledAt != nil && now.Sub(*pairing.LastPolledAt) < PairingInterval {
			fail(w, http.StatusBadRequest, "slow_down")
			return
		}
		if err := h.Store.TouchPairingPoll(ctx, pairing.ID, now); err != nil {
			serverError(w, "record poll", err)
			return
		}
		fail(w, http.StatusBadRequest, "authorization_pending")
		return

	case models.RunnerPairingDenied:
		fail(w, http.StatusBadRequest, "access_denied")
		return

	case models.RunnerPairingApproved:
		if pairing.ConsumedAt != nil || pairing.ApprovedDeviceID == nil {
			fail(w, http.StatusBadRequest, "expired_token")
			return
		}
		device, devErr := h.Store.FindDeviceByID(ctx, *pairing.ApprovedDeviceID)
		if devErr != nil {
			serverError(w, "lookup device", devErr)
			return
		}
		if device == nil || device.OrganizationID == nil {
			fail(w, http.StatusBadRequest, "expired_token")
			return
		}
		// The pairing secret is single-use: whoever wins this conditional update
		// gets the only delivery of the approved result.
		consumed, err := h.Store.MarkPairingConsumed(ctx, pairing.ID, now)
		if err != nil {
			serverError(w, "consume pairing request", err)
			return
		}
		if !consumed {
			fail(w, http.StatusBadRequest, "expired_token")
			return
		}
		writeJSON(w, http.StatusOK, PairPollResponse{
			DeviceID:       device.ID.String(),
			OrganizationID: device.OrganizationID.String(),
			GatewayURL:     h.GatewayURL(),
		})
		return

	default: // RunnerPairingExpired
		fail(w, http.StatusBadRequest, "expired_token")
	}
}

// ---- GET /api/v1/runners/pair/{userCode} (authenticated) ----

// findPendingPairing resolves a user-typed code to a live request, or writes 404.
// An unknown code, a malformed one, an expired one and one that already settled are
// all the same answer.
func (h *Handler) findPendingPairing(w http.ResponseWriter, r *http.Request) (*models.RunnerPairingRequest, bool) {
	code, ok := NormalizeUserCode(mux.Vars(r)["userCode"])
	if !ok {
		fail(w, http.StatusNotFound, "pairing request not found")
		return nil, false
	}
	pairing, err := h.Store.FindPairingRequestByUserCode(r.Context(), code)
	if err != nil {
		serverError(w, "lookup pairing request", err)
		return nil, false
	}
	if pairing == nil || !h.now().Before(pairing.ExpiresAt) {
		fail(w, http.StatusNotFound, "pairing request not found")
		return nil, false
	}
	return pairing, true
}

func (h *Handler) getPair(w http.ResponseWriter, r *http.Request) {
	pairing, ok := h.findPendingPairing(w, r)
	if !ok {
		return
	}
	resp := PairApprovalResponse{
		UserCode:        FormatUserCode(pairing.UserCode),
		Name:            pairing.Name,
		RequestedAt:     pairing.CreatedAt,
		ExpiresAt:       pairing.ExpiresAt,
		RequestedFromIP: deref(pairing.RequestIP),
		Os:              deref(pairing.Os),
		Arch:            deref(pairing.Arch),
		Version:         deref(pairing.RunnerVersion),
	}
	writeJSON(w, http.StatusOK, resp)
}

func deref(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

// ---- POST /api/v1/runners/pair/{userCode}/approve (authenticated) ----

func (h *Handler) approvePair(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	now := h.now()

	c, ok := callerOf(w, r)
	if !ok {
		return
	}
	pairing, ok := h.findPendingPairing(w, r)
	if !ok {
		return
	}

	var req ApprovePairRequest
	if !decodeBody(w, r, &req) {
		return
	}
	orgID, err := uuid.Parse(strings.TrimSpace(req.OrganizationID))
	if err != nil {
		fail(w, http.StatusBadRequest, "invalid_organization")
		return
	}

	// D5: authority is judged in the organization the approver NAMED, not in their
	// active one. A non-member, an unknown org and the Platform org are all 404, so
	// this never becomes an existence oracle for another tenant's ids.
	m, err := h.Lookup.GetMembership(ctx, c.userID, orgID)
	if err != nil {
		serverError(w, "membership lookup", err)
		return
	}
	if m == nil || m.OrgKind == models.OrgKindPlatform {
		fail(w, http.StatusNotFound, "organization not found")
		return
	}
	// Owner or Admin of a Team; anyone in their own Personal workspace.
	if m.OrgKind != models.OrgKindPersonal && !orgs.CanManageMembers(m.Role) {
		fail(w, http.StatusForbidden, "insufficient_role")
		return
	}

	// Re-check against runner_devices as well: a key may have been paired between
	// the start of this flow and now. ApprovePairing also maps the unique index.
	existing, err := h.Store.FindDeviceByFingerprint(ctx, pairing.PublicKeyFingerprint)
	if err != nil {
		serverError(w, "lookup device", err)
		return
	}
	if existing != nil {
		fail(w, http.StatusConflict, "already_paired")
		return
	}

	device := &models.RunnerDevice{
		ID:                   uuid.New(),
		OrganizationID:       &orgID,
		Kind:                 models.RunnerKindDesktop,
		Name:                 pairing.Name,
		PublicKey:            &pairing.PublicKey,
		PublicKeyFingerprint: &pairing.PublicKeyFingerprint,
		Os:                   pairing.Os,
		Arch:                 pairing.Arch,
		RunnerVersion:        pairing.RunnerVersion,
		CreatedByUserID:      &c.userID,
		PairedByUserID:       &c.userID,
		CreatedAt:            now,
	}

	err = h.Store.ApprovePairing(ctx, pairing, device, now)
	switch {
	case errors.Is(err, services.ErrPairingNotPending):
		fail(w, http.StatusConflict, "pairing_request_settled")
		return
	case errors.Is(err, services.ErrKeyAlreadyPaired):
		fail(w, http.StatusConflict, "already_paired")
		return
	case err != nil:
		serverError(w, "approve pairing", err)
		return
	}

	// The anti-phishing control (E4b step 6): the main abuse of a device flow is
	// getting a victim to approve the attacker's machine, which then receives the
	// victim's media. Every Owner is told. Best effort, like every other mail here.
	h.notifyOwners(ctx, orgID, m.OrgName, device.Name)

	writeJSON(w, http.StatusOK, ApprovePairResponse{
		DeviceID:       device.ID.String(),
		OrganizationID: orgID.String(),
	})
}

func (h *Handler) notifyOwners(ctx context.Context, orgID uuid.UUID, orgName, deviceName string) {
	emails, err := h.Store.ListOrgOwnerEmails(ctx, orgID)
	if err != nil {
		log.Printf("runners: owner list for org %s failed: %v", orgID, err)
		return
	}
	link := strings.TrimRight(h.BaseURL(), "/") + "/app/runners"
	for _, email := range emails {
		if err := h.Mailer().SendTemplate(email, services.TemplateRunnerPaired, services.MailData{
			Link:       link,
			OrgName:    orgName,
			DeviceName: deviceName,
			Locale:     services.UserLocaleByEmail(email),
		}); err != nil {
			log.Printf("runners: pairing notification to %s failed: %s", services.MailLogRef(email), services.SanitizeMailError(err))
		}
	}
}

// ---- POST /api/v1/runners/pair/{userCode}/deny (authenticated) ----

func (h *Handler) denyPair(w http.ResponseWriter, r *http.Request) {
	pairing, ok := h.findPendingPairing(w, r)
	if !ok {
		return
	}
	settled, err := h.Store.MarkPairingDenied(r.Context(), pairing.ID, h.now())
	if err != nil {
		serverError(w, "deny pairing", err)
		return
	}
	if !settled {
		fail(w, http.StatusConflict, "pairing_request_settled")
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// ---- GET /api/v1/runners (authenticated, org-scoped) ----

func (h *Handler) listRunners(w http.ResponseWriter, r *http.Request) {
	c, ok := callerOf(w, r)
	if !ok {
		return
	}
	if c.activeOrg == uuid.Nil {
		fail(w, http.StatusForbidden, "no_active_organization")
		return
	}
	devices, err := h.Store.ListActiveDevices(r.Context(), c.activeOrg)
	if err != nil {
		serverError(w, "list devices", err)
		return
	}
	// Revoked devices are excluded: the list is "what can run work right now".
	out := make([]DeviceResponse, 0, len(devices))
	for _, d := range devices {
		out = append(out, toDeviceResponse(d))
	}
	writeJSON(w, http.StatusOK, out)
}

func toDeviceResponse(d models.RunnerDevice) DeviceResponse {
	resp := DeviceResponse{
		ID:            d.ID.String(),
		Name:          d.Name,
		Os:            deref(d.Os),
		Arch:          deref(d.Arch),
		RunnerVersion: deref(d.RunnerVersion),
		CreatedAt:     d.CreatedAt,
		LastSeenAt:    d.LastSeenAt,
		RevokedAt:     d.RevokedAt,
		Capabilities:  capabilitiesOf(d),
	}
	if d.PairedByUserID != nil {
		resp.PairedBy = d.PairedByUserID.String()
	}
	return resp
}

// capabilitiesOf relays the runner's own hello payload. A malformed blob is
// dropped rather than failing the whole list — it is untrusted, self-reported data.
func capabilitiesOf(d models.RunnerDevice) json.RawMessage {
	if d.CapabilitiesJSON == nil {
		return nil
	}
	raw := json.RawMessage(*d.CapabilitiesJSON)
	if !json.Valid(raw) {
		return nil
	}
	return raw
}

// ---- DELETE /api/v1/runners/{id} (authenticated, Owner/Admin or the pairer) ----

func (h *Handler) revokeRunner(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	now := h.now()

	c, ok := callerOf(w, r)
	if !ok {
		return
	}
	deviceID, err := uuid.Parse(mux.Vars(r)["id"])
	if err != nil {
		fail(w, http.StatusNotFound, "device not found")
		return
	}

	// Looked up WITHOUT an organization filter, then compared: that is what lets a
	// device in another org — and a pool worker, which belongs to no org — answer
	// 404 exactly like an unknown id, instead of leaking which ids exist (D5).
	device, err := h.Store.FindDeviceByID(ctx, deviceID)
	if err != nil {
		serverError(w, "lookup device", err)
		return
	}
	if device == nil || device.OrganizationID == nil || *device.OrganizationID != c.activeOrg {
		fail(w, http.StatusNotFound, "device not found")
		return
	}

	// Owner/Admin of the owning org, or the user who paired this device.
	paired := device.PairedByUserID != nil && *device.PairedByUserID == c.userID
	if !orgs.CanManageMembers(c.role) && !paired {
		fail(w, http.StatusForbidden, "insufficient_role")
		return
	}

	revoked, err := h.Store.RevokeDevice(ctx, device.ID, c.userID, now)
	if err != nil {
		serverError(w, "revoke device", err)
		return
	}
	if revoked && h.Publish != nil {
		// Best effort: the database is the authority and the gateway re-checks
		// revoked_at on a 60-second timer, so a broker outage delays the socket
		// teardown rather than losing the revocation.
		if err := h.Publish(services.RunnerDeviceRevokedEvent{
			DeviceID:        device.ID.String(),
			OrganizationID:  device.OrganizationID.String(),
			RevokedByUserID: c.userID.String(),
			RevokedAt:       now,
		}); err != nil {
			log.Printf("runners: revocation event for %s not published: %v", device.ID, err)
		}
	}
	writeJSON(w, http.StatusOK, RevokeResponse{
		ID:             device.ID.String(),
		RevokedAt:      now,
		AlreadyRevoked: !revoked,
	})
}

// ---- POST /api/v1/runners/token (unauthenticated) ----

func (h *Handler) mintToken(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	now := h.now()

	var req RunnerTokenRequest
	if !decodeBody(w, r, &req) {
		return
	}
	deviceID, err := uuid.Parse(strings.TrimSpace(req.DeviceID))
	if err != nil || req.Ts == "" || req.Nonce == "" || req.Sig == "" {
		fail(w, http.StatusBadRequest, "invalid_request")
		return
	}

	ts, err := time.Parse(time.RFC3339, req.Ts)
	if err != nil {
		fail(w, http.StatusBadRequest, "invalid_timestamp")
		return
	}
	if d := now.Sub(ts); d > PairingSkew || d < -PairingSkew {
		fail(w, http.StatusUnauthorized, "clock_skew")
		return
	}
	nonce, err := base64.RawURLEncoding.DecodeString(strings.TrimRight(req.Nonce, "="))
	if err != nil || len(nonce) < 16 {
		fail(w, http.StatusBadRequest, "invalid_nonce")
		return
	}

	device, err := h.Store.FindDeviceByID(ctx, deviceID)
	if err != nil {
		serverError(w, "lookup device", err)
		return
	}
	// An unknown device, a server-side pool worker and a revoked device answer
	// identically: none of them may mint a runner token.
	if device == nil || device.Kind != models.RunnerKindDesktop || device.RevokedAt != nil ||
		device.PublicKey == nil || device.OrganizationID == nil {
		fail(w, http.StatusUnauthorized, "invalid_device")
		return
	}

	// Verified against the literal deviceId/ts the body carried, never a
	// re-serialized copy, so the bytes the device signed are the bytes checked.
	if !verifyDeviceSignature(*device.PublicKey, req.DeviceID, req.Ts, req.Nonce, req.Sig) {
		fail(w, http.StatusUnauthorized, "invalid_signature")
		return
	}
	// Recorded only after the signature holds, so an unsigned request can never
	// burn a nonce the real device is about to use.
	if !h.Nonces.remember(req.DeviceID, req.Nonce) {
		fail(w, http.StatusUnauthorized, "replayed_nonce")
		return
	}

	token, expires, err := services.GenerateRunnerToken(device.ID, *device.OrganizationID)
	if err != nil {
		serverError(w, "mint runner token", err)
		return
	}

	writeJSON(w, http.StatusOK, RunnerTokenResponse{
		Token:          token,
		TokenType:      "Bearer",
		ExpiresIn:      int(time.Until(expires) / time.Second),
		OrganizationID: device.OrganizationID.String(),
		GatewayURL:     h.GatewayURL(),
	})
}
