package admin

import (
	"bytes"
	"context"
	"database/sql"
	"database/sql/driver"
	"errors"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"gorm.io/driver/postgres"
	"gorm.io/gorm"

	"github.com/vecchiotom/reelbolt/database"
)

// failingDriver refuses every transaction with an error that echoes the address, the way a
// constraint violation detail can.
type failingDriver struct{}

func (failingDriver) Open(string) (driver.Conn, error) { return failingConn{}, nil }

type failingConn struct{}

func (failingConn) Prepare(string) (driver.Stmt, error) {
	return nil, errors.New("prepare unsupported")
}
func (failingConn) Close() error { return nil }
func (failingConn) Begin() (driver.Tx, error) {
	return nil, errors.New(`duplicate key: Key (email)=(alice@example.com) already exists`)
}
func (c failingConn) BeginTx(context.Context, driver.TxOptions) (driver.Tx, error) { return c.Begin() }

func TestCreateUserFailureLogsNoAddress(t *testing.T) {
	sql.Register("t030-admin-failing", failingDriver{})
	sqlDB, err := sql.Open("t030-admin-failing", "")
	if err != nil {
		t.Fatalf("sql.Open: %v", err)
	}
	defer func() { _ = sqlDB.Close() }()
	db, err := gorm.Open(postgres.New(postgres.Config{Conn: sqlDB}), database.GormConfig(io.Discard))
	if err != nil {
		t.Fatalf("gorm.Open: %v", err)
	}
	prevDB := database.DB
	database.DB = db
	defer func() { database.DB = prevDB }()

	var buf bytes.Buffer
	log.SetOutput(&buf)
	defer log.SetOutput(io.Discard)

	body := strings.NewReader(`{"email":"alice@example.com","displayName":"Alice"}`)
	rec := httptest.NewRecorder()
	handleCreateUser(rec, httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/users", body))

	if rec.Code != http.StatusInternalServerError {
		t.Fatalf("status = %d, want 500", rec.Code)
	}
	out := buf.String()
	if !strings.Contains(out, "failed to create user") {
		t.Fatalf("expected the failure to be logged, got %q", out)
	}
	if strings.Contains(out, "@") {
		t.Fatalf("log contains an address: %q", out)
	}
}
