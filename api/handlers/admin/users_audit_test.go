package admin

import (
	"context"
	"crypto/rand"
	"database/sql"
	"database/sql/driver"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/golang-jwt/jwt/v5"
	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"

	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/services"
)

// memUser is the one application_users row the in-memory driver serves.
type memUser struct {
	id              uuid.UUID
	email           string
	displayName     string
	isAdmin         bool
	emailVerifiedAt *time.Time
	tokensInvalid   *time.Time
	mustChange      bool
}

type execRec struct {
	q    string
	args []driver.Value
}

// memDB is a tiny recording SQL backend: it answers user SELECTs from memUser, applies
// UPDATE "application_users" SET ... to it, records every Exec, and can fail the audit insert.
type memDB struct {
	mu        sync.Mutex
	user      memUser
	execs     []execRec
	failAudit bool
	// beforeReset runs when the password UPDATE arrives, to model a concurrent change.
	beforeReset func(*memUser)
	// beforeEmailUpdate runs when an email UPDATE arrives, to model a concurrent change.
	beforeEmailUpdate func(*memUser)
}

var whereEmail = regexp.MustCompile(`email = \$(\d+)`)

var (
	memDrivers   sync.Map
	memDriverSeq int
	memDriverMu  sync.Mutex
)

type memDriver struct{ db *memDB }

func (d memDriver) Open(string) (driver.Conn, error) { return memConn(d), nil }

type memConn struct{ db *memDB }

func (c memConn) Prepare(q string) (driver.Stmt, error) { return memStmt{db: c.db, q: q}, nil }
func (memConn) Close() error                            { return nil }
func (memConn) Begin() (driver.Tx, error)               { return lockTx{}, nil }

type memStmt struct {
	db *memDB
	q  string
}

var setCol = regexp.MustCompile(`"(\w+)"=\$(\d+)`)

func (memStmt) Close() error  { return nil }
func (memStmt) NumInput() int { return -1 }
func (s memStmt) Exec(args []driver.Value) (driver.Result, error) {
	s.db.mu.Lock()
	defer s.db.mu.Unlock()
	s.db.execs = append(s.db.execs, execRec{q: s.q, args: args})
	if strings.Contains(s.q, "admin_audit_events") && s.db.failAudit {
		return nil, io.ErrUnexpectedEOF
	}
	if strings.HasPrefix(s.q, `UPDATE "application_users" SET`) {
		set, where, _ := strings.Cut(s.q, " WHERE ")
		if strings.Contains(set, "password_hash") && s.db.beforeReset != nil {
			s.db.beforeReset(&s.db.user)
		}
		if strings.Contains(set, `"email"=`) && s.db.beforeEmailUpdate != nil {
			s.db.beforeEmailUpdate(&s.db.user)
		}
		// Honour the email-change guard: never taken over by its user.
		if strings.Contains(where, "email_verified_at IS NULL") &&
			(s.db.user.emailVerifiedAt != nil || (strings.Contains(where, "must_change_password") && !s.db.user.mustChange)) {
			return driver.RowsAffected(0), nil
		}
		// Honour the reset's guard: email = $n AND email_verified_at IS NOT NULL.
		if m := whereEmail.FindStringSubmatch(where); m != nil {
			i, _ := strconv.Atoi(m[1])
			if args[i-1] != s.db.user.email ||
				(strings.Contains(where, "email_verified_at IS NOT NULL") && s.db.user.emailVerifiedAt == nil) {
				return driver.RowsAffected(0), nil
			}
		}
		for _, m := range setCol.FindAllStringSubmatch(set, -1) {
			i, _ := strconv.Atoi(m[2])
			v := args[i-1]
			switch m[1] {
			case "email":
				s.db.user.email = v.(string)
			case "display_name":
				s.db.user.displayName = v.(string)
			case "is_admin":
				s.db.user.isAdmin = v.(bool)
			case "email_verified_at":
				if v == nil {
					s.db.user.emailVerifiedAt = nil
				} else {
					t := v.(time.Time)
					s.db.user.emailVerifiedAt = &t
				}
			case "tokens_invalidated_at":
				t := v.(time.Time)
				s.db.user.tokensInvalid = &t
			}
		}
	}
	return driver.RowsAffected(1), nil
}

func (s memStmt) Query([]driver.Value) (driver.Rows, error) {
	s.db.mu.Lock()
	defer s.db.mu.Unlock()
	if strings.Contains(s.q, "count(*)") {
		return &lockRows{cols: []string{"count"}, vals: []driver.Value{int64(0)}}, nil
	}
	if strings.Contains(s.q, `FROM "application_users"`) {
		u := s.db.user
		var ev, ti driver.Value
		if u.emailVerifiedAt != nil {
			ev = *u.emailVerifiedAt
		}
		if u.tokensInvalid != nil {
			ti = *u.tokensInvalid
		}
		return &lockRows{
			cols: []string{"id", "email", "display_name", "is_admin", "email_verified_at", "tokens_invalidated_at", "is_service_account", "password_hash", "must_change_password"},
			vals: []driver.Value{u.id.String(), u.email, u.displayName, u.isAdmin, ev, ti, false, "secret-hash", u.mustChange},
		}, nil
	}
	return &lockRows{cols: []string{"id"}}, nil
}

func useMemDB(t *testing.T, u memUser) *memDB {
	t.Helper()
	db := &memDB{user: u}
	memDriverMu.Lock()
	memDriverSeq++
	name := "admin-audit-mem-" + strconv.Itoa(memDriverSeq)
	memDriverMu.Unlock()
	sql.Register(name, memDriver{db})
	memDrivers.Store(name, db)
	sqlDB, err := sql.Open(name, "")
	if err != nil {
		t.Fatal(err)
	}
	gdb, err := gorm.Open(postgres.New(postgres.Config{Conn: sqlDB}), database.GormConfig(io.Discard))
	if err != nil {
		t.Fatal(err)
	}
	prev := database.DB
	database.DB = gdb
	t.Cleanup(func() { database.DB = prev; _ = sqlDB.Close() })
	return db
}

func (db *memDB) audits() []execRec {
	db.mu.Lock()
	defer db.mu.Unlock()
	var out []execRec
	for _, e := range db.execs {
		if strings.Contains(e.q, "admin_audit_events") {
			out = append(out, e)
		}
	}
	return out
}

var actorID = uuid.MustParse("11111111-1111-1111-1111-111111111111")

// adminReq builds a request carrying the acting admin's context, client IP and user agent.
func adminReq(method, path, body string) *http.Request {
	r := httptest.NewRequestWithContext(context.Background(), method, path, strings.NewReader(body))
	r.Header.Set("X-Real-IP", "203.0.113.7")
	r.Header.Set("User-Agent", strings.Repeat("u", 600))
	ctx := context.WithValue(r.Context(), middleware.UserContextKey, middleware.UserContext{UserID: actorID.String(), IsAdmin: true, PlatformAdmin: true})
	return r.WithContext(ctx)
}

func serveAdmin(r *http.Request) *httptest.ResponseRecorder {
	router := mux.NewRouter()
	RegisterAdminRoutes(router)
	rec := httptest.NewRecorder()
	router.ServeHTTP(rec, r)
	return rec
}

// argsContain reports whether any argument stringifies to contain s.
func argsContain(args []driver.Value, s string) bool {
	for _, a := range args {
		if a == nil {
			continue
		}
		var str string
		switch v := a.(type) {
		case string:
			str = v
		case []byte:
			str = string(v)
		default:
			continue
		}
		if strings.Contains(str, s) {
			return true
		}
	}
	return false
}

// checkAudit asserts exactly one audit row with the given action/target and returns its changed_fields.
func checkAudit(t *testing.T, db *memDB, action string, target uuid.UUID) map[string]any {
	t.Helper()
	a := db.audits()
	if len(a) != 1 {
		t.Fatalf("audit inserts = %d, want 1", len(a))
	}
	args := a[0].args
	if !argsContain(args, actorID.String()) || !argsContain(args, target.String()) {
		t.Fatalf("audit args missing actor/target: %v", args)
	}
	if !argsContain(args, action) || !argsContain(args, "203.0.113.7") {
		t.Fatalf("audit args missing action/ip: %v", args)
	}
	for _, v := range args {
		if s, ok := v.(string); ok && strings.HasPrefix(s, "uuu") && len(s) != 512 {
			t.Fatalf("user agent not truncated to 512: %d", len(s))
		}
	}
	for _, secret := range []string{"password_hash", "secret-hash", "otp", "token"} {
		if argsContain(args, secret) {
			t.Fatalf("audit row carries a secret (%q): %v", secret, args)
		}
	}
	var changed map[string]any
	for _, v := range args {
		var s string
		switch x := v.(type) {
		case string:
			s = x
		case []byte:
			s = string(x)
		}
		if strings.HasPrefix(s, "{") {
			if err := json.Unmarshal([]byte(s), &changed); err != nil {
				t.Fatalf("changed_fields not JSON: %v", err)
			}
		}
	}
	return changed
}

func setJWT(t *testing.T) {
	t.Helper()
	old := config.Cfg
	key := make([]byte, 32) // generated per run: no key literal in source
	if _, err := rand.Read(key); err != nil {
		t.Fatal(err)
	}
	config.Cfg.JWTSigningKey = hex.EncodeToString(key)
	config.Cfg.JWTIssuer = "reelbolt-api"
	config.Cfg.JWTAudience = "reelbolt-inference"
	t.Cleanup(func() { config.Cfg = old })
}

func oldToken(t *testing.T, id uuid.UUID) string {
	t.Helper()
	past := time.Now().Add(-time.Minute)
	c := services.TokenClaims{
		UserID: id.String(), PlatformAdmin: true, IsAdmin: true,
		RegisteredClaims: jwt.RegisteredClaims{
			Issuer: config.Cfg.JWTIssuer, Audience: jwt.ClaimStrings{config.Cfg.JWTAudience},
			Subject: id.String(), IssuedAt: jwt.NewNumericDate(past), ExpiresAt: jwt.NewNumericDate(past.Add(time.Hour)),
		},
	}
	s, err := jwt.NewWithClaims(jwt.SigningMethodHS256, c).SignedString([]byte(config.Cfg.JWTSigningKey))
	if err != nil {
		t.Fatal(err)
	}
	return s
}

type noMembership struct{}

func (noMembership) GetMembership(context.Context, uuid.UUID, uuid.UUID) (*services.ActiveMembership, error) {
	return nil, nil
}

func TestAdminDemotionEndsSessionAndAudits(t *testing.T) {
	setJWT(t)
	target := uuid.New()
	now := time.Now().UTC()
	db := useMemDB(t, memUser{id: target, email: "a@example.com", displayName: "A", isAdmin: true, emailVerifiedAt: &now})

	auth := middleware.NewAuthMiddleware(noMembership{}, time.Minute).WithSessionLookup(services.GormSessionLookup{})
	ok := auth.Handler(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusOK) }))
	tok := oldToken(t, target)
	call := func() int {
		r := httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/x", nil)
		r.Header.Set("Authorization", "Bearer "+tok)
		rec := httptest.NewRecorder()
		ok.ServeHTTP(rec, r)
		return rec.Code
	}
	if c := call(); c != http.StatusOK {
		t.Fatalf("token before demotion = %d, want 200", c)
	}
	auth.InvalidateSession(target) // the test's own middleware instance caches like production

	rec := serveAdmin(adminReq(http.MethodPut, "/users/"+target.String(), `{"isPlatformAdmin":false}`))
	if rec.Code != http.StatusOK {
		t.Fatalf("demote = %d %s", rec.Code, rec.Body.String())
	}
	if db.user.tokensInvalid == nil {
		t.Fatal("demotion did not set tokens_invalidated_at")
	}
	auth.InvalidateSession(target)
	if c := call(); c != http.StatusUnauthorized {
		t.Fatalf("old token after demotion = %d, want 401", c)
	}
	changed := checkAudit(t, db, "user.update", target)
	isAdmin, _ := changed["is_admin"].(map[string]any)
	if isAdmin["old"] != true || isAdmin["new"] != false {
		t.Fatalf("changed_fields.is_admin = %v", changed)
	}
}

func TestAdminDisplayNameOnlyKeepsSession(t *testing.T) {
	target := uuid.New()
	now := time.Now().UTC()
	db := useMemDB(t, memUser{id: target, email: "a@example.com", displayName: "A", emailVerifiedAt: &now})
	rec := serveAdmin(adminReq(http.MethodPut, "/users/"+target.String(), `{"displayName":"B"}`))
	if rec.Code != http.StatusOK {
		t.Fatalf("update = %d %s", rec.Code, rec.Body.String())
	}
	if db.user.tokensInvalid != nil {
		t.Fatal("display-name change must not end sessions")
	}
	changed := checkAudit(t, db, "user.update", target)
	if dn, _ := changed["display_name"].(map[string]any); dn["old"] != "A" || dn["new"] != "B" {
		t.Fatalf("changed_fields = %v", changed)
	}
}

func TestAdminResetPasswordAudited(t *testing.T) {
	target := uuid.New()
	now := time.Now().UTC()
	db := useMemDB(t, memUser{id: target, email: "a@example.com", displayName: "A", emailVerifiedAt: &now})
	rec := serveAdmin(adminReq(http.MethodPut, "/users/"+target.String(), `{"resetPassword":true}`))
	if rec.Code != http.StatusOK {
		t.Fatalf("reset = %d %s", rec.Code, rec.Body.String())
	}
	changed := checkAudit(t, db, "user.reset_password", target)
	if _, ok := changed["password"]; ok {
		t.Fatal("password must not be in changed_fields")
	}
}

func TestAdminDeleteAudited(t *testing.T) {
	target := uuid.New()
	db := useMemDB(t, memUser{id: target, email: "a@example.com", displayName: "A"})
	rec := serveAdmin(adminReq(http.MethodDelete, "/users/"+target.String(), ""))
	if rec.Code != http.StatusNoContent {
		t.Fatalf("delete = %d %s", rec.Code, rec.Body.String())
	}
	checkAudit(t, db, "user.delete", target)
}

func TestAdminCreateAudited(t *testing.T) {
	db := useMemDB(t, memUser{id: uuid.New()})
	rec := serveAdmin(adminReq(http.MethodPost, "/users", `{"email":"n@example.com","displayName":"N","isPlatformAdmin":true}`))
	if rec.Code != http.StatusCreated {
		t.Fatalf("create = %d %s", rec.Code, rec.Body.String())
	}
	var resp CreateUserResponse
	_ = json.Unmarshal(rec.Body.Bytes(), &resp)
	changed := checkAudit(t, db, "user.create", resp.User.ID)
	if ia, _ := changed["is_admin"].(map[string]any); ia["new"] != true {
		t.Fatalf("changed_fields = %v", changed)
	}
}

func TestAdminAuditFailureFailsRequest(t *testing.T) {
	target := uuid.New()
	now := time.Now().UTC()
	db := useMemDB(t, memUser{id: target, email: "a@example.com", displayName: "A", isAdmin: true, emailVerifiedAt: &now})
	db.failAudit = true
	rec := serveAdmin(adminReq(http.MethodPut, "/users/"+target.String(), `{"isPlatformAdmin":false}`))
	if rec.Code != http.StatusInternalServerError {
		t.Fatalf("update with failing audit = %d, want 500", rec.Code)
	}
}

// TOCTOU: the address changes between the handler's check and the reset UPDATE. The guarded
// UPDATE matches no row, the request is refused and nothing is audited.
func TestAdminResetRefusedWhenEmailChangedConcurrently(t *testing.T) {
	target := uuid.New()
	now := time.Now().UTC()
	db := useMemDB(t, memUser{id: target, email: "a@example.com", displayName: "A", emailVerifiedAt: &now})
	db.beforeReset = func(u *memUser) { u.email = "other@example.com"; u.emailVerifiedAt = nil }
	rec := serveAdmin(adminReq(http.MethodPut, "/users/"+target.String(), `{"resetPassword":true}`))
	if rec.Code != http.StatusConflict {
		t.Fatalf("reset after concurrent email change = %d %s, want 409", rec.Code, rec.Body.String())
	}
	if n := len(db.audits()); n != 0 {
		t.Fatalf("audit rows = %d, want 0", n)
	}
}

// No admin changes a user's email: moving the address to a mailbox the admin controls and then
// using a public verify/forgot-password flow (verify signs the user in) hands over the account.
// Every account kind is refused with zero writes; a typo at creation means delete and recreate.
func TestAdminCannotChangeAnyEmail(t *testing.T) {
	now := time.Now().UTC()
	cases := map[string]memUser{
		"verified":                 {email: "victim@example.com", emailVerifiedAt: &now},
		"unverified self-signup":   {email: "self@example.com"},
		"admin-created unused":     {email: "typo@exmaple.com", emailVerifiedAt: &now, mustChange: true},
		"admin-created unverified": {email: "typo2@exmaple.com", mustChange: true},
	}
	bodies := []string{
		`{"email":"attacker@example.com"}`,
		`{"email":"attacker@example.com","displayName":"X","isPlatformAdmin":false}`,
		`{"email":"attacker@example.com","resetPassword":true}`,
	}
	for name, u := range cases {
		for _, body := range bodies {
			u.id = uuid.New()
			u.displayName = "U"
			db := useMemDB(t, u)
			rec := serveAdmin(adminReq(http.MethodPut, "/users/"+u.id.String(), body))
			if rec.Code != http.StatusConflict || !strings.Contains(rec.Body.String(), "EMAIL_CHANGE_REQUIRES_USER") {
				t.Fatalf("%s %s = %d %s, want 409 EMAIL_CHANGE_REQUIRES_USER", name, body, rec.Code, rec.Body.String())
			}
			if len(db.execs) != 0 || db.user.email != u.email {
				t.Fatalf("%s %s: refused email change wrote %d statements", name, body, len(db.execs))
			}
		}
	}
}

// The admin form always sends the email; an unchanged one passes with the other edits.
func TestAdminUnchangedEmailPasses(t *testing.T) {
	target := uuid.New()
	now := time.Now().UTC()
	db := useMemDB(t, memUser{id: target, email: "a@example.com", displayName: "A", emailVerifiedAt: &now})
	rec := serveAdmin(adminReq(http.MethodPut, "/users/"+target.String(), `{"email":"a@example.com","displayName":"B"}`))
	if rec.Code != http.StatusOK {
		t.Fatalf("unchanged email + display name = %d %s", rec.Code, rec.Body.String())
	}
	if db.user.displayName != "B" || db.user.email != "a@example.com" || db.user.emailVerifiedAt == nil || db.user.tokensInvalid != nil {
		t.Fatalf("unexpected state: %+v", db.user)
	}
	changed := checkAudit(t, db, "user.update", target)
	if _, ok := changed["email"]; ok {
		t.Fatalf("unchanged email audited: %v", changed)
	}
}

// A reset to an unverified address is refused (409, nothing written); once verified it passes.
func TestAdminResetRequiresVerifiedAddress(t *testing.T) {
	target := uuid.New()
	db := useMemDB(t, memUser{id: target, email: "a@example.com", displayName: "A"})
	rec := serveAdmin(adminReq(http.MethodPut, "/users/"+target.String(), `{"resetPassword":true}`))
	if rec.Code != http.StatusConflict || len(db.execs) != 0 {
		t.Fatalf("reset to unverified address = %d (%d writes), want 409 and none", rec.Code, len(db.execs))
	}
	now := time.Now().UTC()
	db.user.emailVerifiedAt = &now
	rec = serveAdmin(adminReq(http.MethodPut, "/users/"+target.String(), `{"resetPassword":true}`))
	if rec.Code != http.StatusOK {
		t.Fatalf("reset after verification = %d %s, want 200", rec.Code, rec.Body.String())
	}
	if db.user.tokensInvalid == nil {
		t.Fatal("reset did not end sessions")
	}
}
