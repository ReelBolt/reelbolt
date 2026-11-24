package main

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"regexp"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// On-demand pool nodes. The pool waker (sandbox/cmd/reelbolt-scaler) boots a
// cloud VM and needs the gateway to know that VM's name before the worker
// connects. It asks this gateway for a node token over the internal API; the
// gateway enrols the runner_devices row (the same rules as
// sandbox/cmd/reelbolt-worker/enroll.go) and returns a short-lived credential
// bound to that name. The VM never holds a database URL or the internal token.

const (
	// nodeTokenPrefix marks a credential as a node token rather than one of the
	// static RUNNER_POOL_CREDENTIALS.
	nodeTokenPrefix = "rbn1."

	// minPoolTokenKeyBytes is the shortest RUNNER_POOL_TOKEN_KEY accepted.
	minPoolTokenKeyBytes = 32

	nodeTTLMin     = 60
	nodeTTLMax     = 86400
	nodeTTLDefault = 14400

	// nodeTokenSkew is the slack allowed past exp; the waker and the gateway
	// share a clock only approximately.
	nodeTokenSkew = 5 * time.Second

	// poolBodyLimit bounds every pool route's request body.
	poolBodyLimit = 4 << 10

	nodeVersion = "pool-waker"
)

var nodeNamePattern = regexp.MustCompile(`^rb-pool-[a-z0-9-]{1,40}$`)

// validNodeName reports whether a name may be enrolled by the waker.
func validNodeName(name string) bool { return nodeNamePattern.MatchString(name) }

// ErrNodeRevoked is returned by Enroll for a name whose row is revoked.
var ErrNodeRevoked = errors.New("gateway: pool node is revoked")

// nodeClaims is the signed payload of a node token.
type nodeClaims struct {
	Name string `json:"name"`
	Exp  int64  `json:"exp"`
}

var errBadNodeToken = errors.New("node token invalid")

// mintNodeToken returns rbn1.<b64url(payload)>.<b64url(hmac)>.
func mintNodeToken(key []byte, name string, exp time.Time) (string, error) {
	payload, err := json.Marshal(nodeClaims{Name: name, Exp: exp.Unix()})
	if err != nil {
		return "", err
	}
	return nodeTokenPrefix + base64.RawURLEncoding.EncodeToString(payload) + "." +
		base64.RawURLEncoding.EncodeToString(nodeTokenMAC(key, payload)), nil
}

func nodeTokenMAC(key, payload []byte) []byte {
	mac := hmac.New(sha256.New, key)
	mac.Write(payload)
	return mac.Sum(nil)
}

// verifyNodeToken checks signature (constant time), expiry and the bound name.
// It returns only a generic error: the caller must not learn which check failed.
func verifyNodeToken(key []byte, token, wantName string, now time.Time) error {
	if len(key) < minPoolTokenKeyBytes || !strings.HasPrefix(token, nodeTokenPrefix) {
		return errBadNodeToken
	}
	body := strings.TrimPrefix(token, nodeTokenPrefix)
	parts := strings.Split(body, ".")
	if len(parts) != 2 {
		return errBadNodeToken
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil {
		return errBadNodeToken
	}
	sig, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return errBadNodeToken
	}
	if !hmac.Equal(sig, nodeTokenMAC(key, payload)) {
		return errBadNodeToken
	}
	var claims nodeClaims
	if err := json.Unmarshal(payload, &claims); err != nil {
		return errBadNodeToken
	}
	if claims.Name == "" || claims.Name != wantName {
		return errBadNodeToken
	}
	if now.After(time.Unix(claims.Exp, 0).Add(nodeTokenSkew)) {
		return errBadNodeToken
	}
	return nil
}

// ---- store ---------------------------------------------------------------

// PoolNodeStore enrols and revokes waker-managed CloudPool rows.
type PoolNodeStore interface {
	// Enroll creates or refreshes the row; ErrNodeRevoked if it is revoked.
	Enroll(ctx context.Context, name string) (id string, err error)
	// Revoke sets revoked_at; idempotent, and a missing row is not an error.
	Revoke(ctx context.Context, name string) error
}

// WakerRecord is the stored waker heartbeat.
type WakerRecord struct {
	Healthy   bool
	Detail    wakerDetail
	UpdatedAt time.Time
	// ServerNow is the store's clock at read time, so the age is measured on one
	// clock whichever replica answers.
	ServerNow time.Time
}

// wakerDetail is the JSON kept in platform_settings.reason.
type wakerDetail struct {
	Reason        string  `json:"reason"`
	Nodes         int     `json:"nodes"`
	MaxNodes      int     `json:"maxNodes"`
	Capped        bool    `json:"capped"`
	SpendTodayEUR float64 `json:"spendTodayEur"`
	Instance      string  `json:"instance"`
}

// WakerStore keeps the waker heartbeat.
type WakerStore interface {
	PutWaker(ctx context.Context, healthy bool, detail wakerDetail) error
	// GetWaker returns found=false when no heartbeat was ever stored.
	GetWaker(ctx context.Context) (rec WakerRecord, found bool, err error)
}

// SQL shared with sandbox/cmd/reelbolt-worker/enroll.go; pool_nodes_test.go
// fails when the two drift.
const (
	enrollLockSQL   = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))"
	enrollLockKey   = "reelbolt-pool-enroll:"
	enrollSelectSQL = "SELECT id::text, revoked_at FROM runner_devices WHERE kind = $1 AND name = $2 ORDER BY created_at, id"
	enrollInsertSQL = `INSERT INTO runner_devices (id, organization_id, kind, name, os, arch, runner_version, created_at)
				 VALUES ($1::uuid, NULL, $2, $3, $4, $5, $6, now())`
	enrollUpdateSQL = `UPDATE runner_devices
				    SET runner_version = $2, os = $3, arch = $4,
				        revoked_at = CASE WHEN $5 THEN NULL ELSE revoked_at END,
				        revoked_by_user_id = CASE WHEN $5 THEN NULL ELSE revoked_by_user_id END
				  WHERE id = $1::uuid`
)

// PostgresPoolStore implements PoolNodeStore and WakerStore over a shared pool.
type PostgresPoolStore struct {
	pool *pgxpool.Pool
}

// NewPostgresPoolStore wraps an existing pool (the device registry's).
func NewPostgresPoolStore(pool *pgxpool.Pool) *PostgresPoolStore {
	return &PostgresPoolStore{pool: pool}
}

// Enroll mirrors enrollPoolWorker (reinstate=false): one transaction, advisory
// lock on the name, sticky revocation, refusing ambiguous duplicate rows.
func (s *PostgresPoolStore) Enroll(ctx context.Context, name string) (string, error) {
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return "", fmt.Errorf("begin: %w", err)
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err := tx.Exec(ctx, enrollLockSQL, enrollLockKey+name); err != nil {
		return "", fmt.Errorf("lock: %w", err)
	}
	rows, err := tx.Query(ctx, enrollSelectSQL, protocol.KindCloudPool, name)
	if err != nil {
		return "", fmt.Errorf("read runner_devices: %w", err)
	}
	var (
		ids     []string
		revoked []bool
	)
	for rows.Next() {
		var (
			id string
			at *time.Time
		)
		if err := rows.Scan(&id, &at); err != nil {
			rows.Close()
			return "", fmt.Errorf("scan runner_devices: %w", err)
		}
		ids = append(ids, id)
		revoked = append(revoked, at != nil)
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return "", fmt.Errorf("read runner_devices: %w", err)
	}
	if len(ids) > 1 {
		return "", fmt.Errorf("%d CloudPool rows are named %q", len(ids), name)
	}
	var id string
	if len(ids) == 0 {
		id = uuid.NewString()
		if _, err := tx.Exec(ctx, enrollInsertSQL, id, protocol.KindCloudPool, name, "linux", "amd64", nodeVersion); err != nil {
			return "", fmt.Errorf("insert runner_devices: %w", err)
		}
	} else {
		id = ids[0]
		if revoked[0] {
			return id, ErrNodeRevoked
		}
		if _, err := tx.Exec(ctx, enrollUpdateSQL, id, nodeVersion, "linux", "amd64", false); err != nil {
			return "", fmt.Errorf("update runner_devices: %w", err)
		}
	}
	if err := tx.Commit(ctx); err != nil {
		return "", fmt.Errorf("commit: %w", err)
	}
	return id, nil
}

// Revoke sets revoked_at on the named CloudPool row if it is not already set.
func (s *PostgresPoolStore) Revoke(ctx context.Context, name string) error {
	_, err := s.pool.Exec(ctx,
		"UPDATE runner_devices SET revoked_at = now() WHERE kind = $1 AND name = $2 AND revoked_at IS NULL",
		protocol.KindCloudPool, name)
	if err != nil {
		return fmt.Errorf("revoke runner_devices: %w", err)
	}
	return nil
}

const wakerSettingsKey = "waker"

// PutWaker upserts the heartbeat row. paused=!healthy; updated_at is the
// heartbeat and is the database's clock.
func (s *PostgresPoolStore) PutWaker(ctx context.Context, healthy bool, detail wakerDetail) error {
	raw, err := json.Marshal(detail)
	if err != nil {
		return err
	}
	updatedBy := "pool-waker:" + detail.Instance
	if len(updatedBy) > 300 {
		updatedBy = updatedBy[:300]
	}
	_, err = s.pool.Exec(ctx,
		`INSERT INTO platform_settings (key, paused, reason, updated_at, updated_by)
		 VALUES ($1, $2, $3, now(), $4)
		 ON CONFLICT (key) DO UPDATE
		   SET paused = EXCLUDED.paused, reason = EXCLUDED.reason,
		       updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by`,
		wakerSettingsKey, !healthy, string(raw), updatedBy)
	if err != nil {
		return fmt.Errorf("write platform_settings: %w", err)
	}
	return nil
}

// GetWaker reads the heartbeat row and the database clock.
func (s *PostgresPoolStore) GetWaker(ctx context.Context) (WakerRecord, bool, error) {
	var (
		paused bool
		reason string
		at     time.Time
		now    time.Time
	)
	err := s.pool.QueryRow(ctx,
		"SELECT paused, coalesce(reason, ''), updated_at, now() FROM platform_settings WHERE key = $1",
		wakerSettingsKey).Scan(&paused, &reason, &at, &now)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return WakerRecord{}, false, nil
		}
		return WakerRecord{}, false, fmt.Errorf("read platform_settings: %w", err)
	}
	rec := WakerRecord{Healthy: !paused, UpdatedAt: at, ServerNow: now}
	if err := json.Unmarshal([]byte(reason), &rec.Detail); err != nil {
		// Free text written by an operator: keep it as the reason.
		rec.Detail = wakerDetail{Reason: reason}
	}
	return rec, true, nil
}

// ---- wiring --------------------------------------------------------------

// attachPool enables pool node tokens and the waker heartbeat. A nil or short
// key leaves the three pool routes answering 503 and rbn1. credentials refused.
func (g *gateway) attachPool(key []byte, nodes PoolNodeStore, waker WakerStore) {
	g.poolKey = key
	g.poolNodes = nodes
	g.waker = waker
}

func (g *gateway) poolTokensEnabled() bool {
	return len(g.poolKey) >= minPoolTokenKeyBytes && g.poolNodes != nil
}

// nodeTokenAccepted verifies an rbn1. credential against the hello name.
func (g *gateway) nodeTokenAccepted(credential, workerName string) bool {
	if len(g.poolKey) < minPoolTokenKeyBytes {
		return false
	}
	return verifyNodeToken(g.poolKey, credential, workerName, g.now()) == nil
}

func (g *gateway) registerPoolRoutes(internal *mux.Router) {
	internal.HandleFunc("/pool/nodes", g.handleEnrollNode).Methods(http.MethodPost)
	internal.HandleFunc("/pool/nodes/{name}", g.handleRevokeNode).Methods(http.MethodDelete)
	internal.HandleFunc("/pool/waker", g.handlePutWaker).Methods(http.MethodPut)
}

func writePoolDisabled(w http.ResponseWriter) {
	writeJSON(w, http.StatusServiceUnavailable, map[string]any{
		"error":   "pool_tokens_disabled",
		"message": "RUNNER_POOL_TOKEN_KEY is not configured on this gateway",
	})
}

func writeBadRequest(w http.ResponseWriter, msg string) {
	writeJSON(w, http.StatusBadRequest, map[string]any{"error": "bad_request", "message": msg})
}

// decodeStrict reads one size-limited JSON value, rejecting unknown fields and
// trailing data.
func decodeStrict(w http.ResponseWriter, r *http.Request, into any) error {
	dec := json.NewDecoder(http.MaxBytesReader(w, r.Body, poolBodyLimit))
	dec.DisallowUnknownFields()
	if err := dec.Decode(into); err != nil {
		return err
	}
	if dec.More() {
		return errors.New("unexpected trailing data")
	}
	return nil
}

type enrollNodeRequest struct {
	Name       string `json:"name"`
	TTLSeconds int    `json:"ttlSeconds"`
}

func clampTTL(seconds int) int {
	switch {
	case seconds == 0:
		return nodeTTLDefault
	case seconds < nodeTTLMin:
		return nodeTTLMin
	case seconds > nodeTTLMax:
		return nodeTTLMax
	}
	return seconds
}

func (g *gateway) handleEnrollNode(w http.ResponseWriter, r *http.Request) {
	if !g.poolTokensEnabled() {
		writePoolDisabled(w)
		return
	}
	var req enrollNodeRequest
	if err := decodeStrict(w, r, &req); err != nil {
		writeBadRequest(w, err.Error())
		return
	}
	if !validNodeName(req.Name) {
		writeBadRequest(w, "name must match ^rb-pool-[a-z0-9-]{1,40}$")
		return
	}
	if _, err := g.poolNodes.Enroll(r.Context(), req.Name); err != nil {
		if errors.Is(err, ErrNodeRevoked) {
			log.Printf("pool node %s: enrol refused, revoked", req.Name)
			writeJSON(w, http.StatusConflict, map[string]any{"error": "node_revoked"})
			return
		}
		log.Printf("pool node %s: enrol failed: %v", req.Name, err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}
	exp := g.now().Add(time.Duration(clampTTL(req.TTLSeconds)) * time.Second).UTC()
	token, err := mintNodeToken(g.poolKey, req.Name, exp)
	if err != nil {
		log.Printf("pool node %s: minting failed", req.Name)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}
	log.Printf("pool node %s: enrolled, token issued", req.Name)
	writeJSON(w, http.StatusCreated, map[string]any{
		"name": req.Name, "token": token, "expiresAt": exp.Format(time.RFC3339),
	})
}

func (g *gateway) handleRevokeNode(w http.ResponseWriter, r *http.Request) {
	if !g.poolTokensEnabled() {
		writePoolDisabled(w)
		return
	}
	name := mux.Vars(r)["name"]
	if !validNodeName(name) {
		writeBadRequest(w, "name must match ^rb-pool-[a-z0-9-]{1,40}$")
		return
	}
	if err := g.poolNodes.Revoke(r.Context(), name); err != nil {
		log.Printf("pool node %s: revoke failed: %v", name, err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}
	log.Printf("pool node %s: revoked", name)
	w.WriteHeader(http.StatusNoContent)
}

type putWakerRequest struct {
	Healthy       bool    `json:"healthy"`
	Reason        string  `json:"reason"`
	Nodes         int     `json:"nodes"`
	MaxNodes      int     `json:"maxNodes"`
	Capped        bool    `json:"capped"`
	SpendTodayEUR float64 `json:"spendTodayEur"`
	Instance      string  `json:"instance"`
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return strings.ToValidUTF8(s[:n], "")
}

func (g *gateway) handlePutWaker(w http.ResponseWriter, r *http.Request) {
	if !g.poolTokensEnabled() || g.waker == nil {
		writePoolDisabled(w)
		return
	}
	var req putWakerRequest
	if err := decodeStrict(w, r, &req); err != nil {
		writeBadRequest(w, err.Error())
		return
	}
	if req.Nodes < 0 || req.MaxNodes < 0 || req.SpendTodayEUR < 0 || req.SpendTodayEUR > 1e9 {
		writeBadRequest(w, "nodes, maxNodes and spendTodayEur must be non-negative")
		return
	}
	detail := wakerDetail{
		Reason: truncate(req.Reason, 200), Nodes: req.Nodes, MaxNodes: req.MaxNodes,
		Capped: req.Capped, SpendTodayEUR: req.SpendTodayEUR, Instance: truncate(req.Instance, 100),
	}
	if err := g.waker.PutWaker(r.Context(), req.Healthy, detail); err != nil {
		log.Printf("pool waker heartbeat: store failed: %v", err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "internal"})
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// wakerFreshness is the longest heartbeat age still counted as alive.
const wakerFreshness = 45 * time.Second

// poolView is the "pool" object of GET /internal/v1/runners.
type poolView struct {
	WakerHealthy    bool     `json:"wakerHealthy"`
	WakerAgeSeconds *float64 `json:"wakerAgeSeconds"`
	Reason          string   `json:"reason"`
	Capped          bool     `json:"capped"`
}

// poolStatus computes the pool view from the stored heartbeat, so any replica
// answers the same.
func (g *gateway) poolStatus(ctx context.Context) poolView {
	if g.waker == nil {
		return poolView{Reason: "waker_unavailable"}
	}
	rec, found, err := g.waker.GetWaker(ctx)
	if err != nil {
		log.Printf("pool waker status: store failed: %v", err)
		return poolView{Reason: "waker_unavailable"}
	}
	if !found {
		return poolView{Reason: "no_heartbeat"}
	}
	return poolViewFrom(rec)
}

func poolViewFrom(rec WakerRecord) poolView {
	age := rec.ServerNow.Sub(rec.UpdatedAt)
	if age < 0 {
		age = 0
	}
	secs := age.Seconds()
	return poolView{
		WakerHealthy:    age <= wakerFreshness && rec.Healthy && !rec.Detail.Capped,
		WakerAgeSeconds: &secs,
		Reason:          rec.Detail.Reason,
		Capped:          rec.Detail.Capped,
	}
}

// NewPostgresSwitchStoreFromPool wraps an existing pool; used by tests that
// share one database connection across stores.
func NewPostgresSwitchStoreFromPool(pool *pgxpool.Pool) *PostgresSwitchStore {
	return &PostgresSwitchStore{pool: pool}
}
