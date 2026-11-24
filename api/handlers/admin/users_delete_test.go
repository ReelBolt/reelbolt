package admin

import (
	"context"
	"database/sql"
	"database/sql/driver"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/gorilla/mux"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"

	"github.com/vecchiotom/reelbolt/database"
)

// lockDriver answers the lock-check count with a configured value (or an error) and
// records whether any delete statement ran.
type lockDriver struct {
	count   int64
	readErr bool
	deleted *bool
}

func (d lockDriver) Open(string) (driver.Conn, error) { return lockConn(d), nil }

type lockConn lockDriver

func (c lockConn) Prepare(q string) (driver.Stmt, error) { return lockStmt{c: c, q: q}, nil }
func (lockConn) Close() error                            { return nil }
func (lockConn) Begin() (driver.Tx, error)               { return lockTx{}, nil }

type lockTx struct{}

func (lockTx) Commit() error   { return nil }
func (lockTx) Rollback() error { return nil }

type lockStmt struct {
	c lockConn
	q string
}

func (lockStmt) Close() error  { return nil }
func (lockStmt) NumInput() int { return -1 }
func (s lockStmt) Exec([]driver.Value) (driver.Result, error) {
	if strings.Contains(strings.ToUpper(s.q), "DELETE") {
		*s.c.deleted = true
	}
	return driver.RowsAffected(1), nil
}
func (s lockStmt) Query([]driver.Value) (driver.Rows, error) {
	if strings.Contains(s.q, "count(*)") && strings.Contains(s.q, "is_service_account") {
		// The "is this a service account" check: an ordinary user.
		return &lockRows{cols: []string{"count"}, vals: []driver.Value{int64(0)}}, nil
	}
	if strings.Contains(s.q, "count(*)") {
		if s.c.readErr {
			return nil, errors.New("read failed")
		}
		return &lockRows{cols: []string{"count"}, vals: []driver.Value{s.c.count}}, nil
	}
	return &lockRows{cols: []string{"id"}}, nil
}

type lockRows struct {
	cols []string
	vals []driver.Value
	done bool
}

func (r *lockRows) Columns() []string { return r.cols }
func (r *lockRows) Close() error      { return nil }
func (r *lockRows) Next(dest []driver.Value) error {
	if r.done || r.vals == nil {
		return io.EOF
	}
	r.done = true
	copy(dest, r.vals)
	return nil
}

var lockDriverSeq int

func runDelete(t *testing.T, d lockDriver) (int, string, bool) {
	t.Helper()
	deleted := false
	d.deleted = &deleted
	lockDriverSeq++
	name := "t009-lock-" + string(rune('a'+lockDriverSeq))
	sql.Register(name, d)
	sqlDB, err := sql.Open(name, "")
	if err != nil {
		t.Fatalf("sql.Open: %v", err)
	}
	defer func() { _ = sqlDB.Close() }()
	db, err := gorm.Open(postgres.New(postgres.Config{Conn: sqlDB}), database.GormConfig(io.Discard))
	if err != nil {
		t.Fatalf("gorm.Open: %v", err)
	}
	prev := database.DB
	database.DB = db
	defer func() { database.DB = prev }()

	req := httptest.NewRequestWithContext(context.Background(), http.MethodDelete,
		"/users/00000000-0000-0000-0000-000000000001", nil)
	req = mux.SetURLVars(req, map[string]string{"id": "00000000-0000-0000-0000-000000000001"})
	rec := httptest.NewRecorder()
	handleDeleteUser(rec, req)
	return rec.Code, rec.Body.String(), deleted
}

func TestDeleteUserRefusedWhenProjectLocked(t *testing.T) {
	// The count covers both the project marker and a flagged file row.
	code, body, deleted := runDelete(t, lockDriver{count: 1})
	if code != http.StatusConflict || deleted {
		t.Fatalf("code=%d deleted=%v, want 409 and no delete", code, deleted)
	}
	if !strings.Contains(body, "This account cannot be deleted right now.") {
		t.Fatalf("body = %q", body)
	}
}

func TestDeleteUserRefusedOnReadError(t *testing.T) {
	code, _, deleted := runDelete(t, lockDriver{readErr: true})
	if code != http.StatusConflict || deleted {
		t.Fatalf("code=%d deleted=%v, want 409 and no delete", code, deleted)
	}
}

func TestDeleteUserAllowedWhenNothingLocked(t *testing.T) {
	code, _, deleted := runDelete(t, lockDriver{})
	if code != http.StatusNoContent || !deleted {
		t.Fatalf("code=%d deleted=%v, want 204 and a delete", code, deleted)
	}
}
