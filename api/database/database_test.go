package database

import (
	"bytes"
	"context"
	"database/sql"
	"database/sql/driver"
	"errors"
	"io"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/config"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"
	"gorm.io/gorm/logger"
)

// emptyDriver is a database/sql driver that answers every query with zero rows, or with an error
// when the SQL mentions "boom" — enough to drive GORM's not-found and error log paths without a
// live Postgres.
type emptyDriver struct{}

func (emptyDriver) Open(string) (driver.Conn, error) { return emptyConn{}, nil }

type emptyConn struct{}

func (emptyConn) Prepare(string) (driver.Stmt, error) { return nil, errors.New("prepare unsupported") }
func (emptyConn) Close() error                        { return nil }
func (emptyConn) Begin() (driver.Tx, error)           { return nil, errors.New("tx unsupported") }
func (emptyConn) QueryContext(_ context.Context, query string, _ []driver.NamedValue) (driver.Rows, error) {
	if strings.Contains(query, "boom") {
		return nil, errors.New("relation does not exist")
	}
	return &emptyRows{}, nil
}

type emptyRows struct{}

func (*emptyRows) Columns() []string         { return []string{"id", "email"} }
func (*emptyRows) Close() error              { return nil }
func (*emptyRows) Next([]driver.Value) error { return io.EOF }

var registerOnce sync.Once

type account struct {
	ID    uint
	Email string
}

func (account) TableName() string { return "users" }

type boomAccount struct {
	ID    uint
	Email string
}

func (boomAccount) TableName() string { return "boom" }

func openWithLogBuffer(t *testing.T) (*gorm.DB, *bytes.Buffer) {
	t.Helper()
	registerOnce.Do(func() { sql.Register("t030-empty", emptyDriver{}) })
	sqlDB, err := sql.Open("t030-empty", "")
	if err != nil {
		t.Fatalf("sql.Open: %v", err)
	}
	t.Cleanup(func() { _ = sqlDB.Close() })
	var buf bytes.Buffer
	db, err := gorm.Open(postgres.New(postgres.Config{Conn: sqlDB}), GormConfig(&buf))
	if err != nil {
		t.Fatalf("gorm.Open: %v", err)
	}
	return db, &buf
}

func TestNotFoundLookupByEmailLogsNoAddress(t *testing.T) {
	db, buf := openWithLogBuffer(t)
	var a account
	err := db.Where("email = ?", "alice@example.com").First(&a).Error
	if !errors.Is(err, gorm.ErrRecordNotFound) {
		t.Fatalf("want ErrRecordNotFound, got %v", err)
	}
	if strings.Contains(buf.String(), "@") {
		t.Fatalf("log contains an address: %q", buf.String())
	}
}

func TestFailedQueryLogsSQLWithoutBoundValues(t *testing.T) {
	db, buf := openWithLogBuffer(t)
	var a boomAccount
	if err := db.Where("email = ?", "alice@example.com").First(&a).Error; err == nil {
		t.Fatal("want an error from the boom table")
	}
	out := buf.String()
	if !strings.Contains(out, "boom") {
		t.Fatalf("error path should still log the statement, got %q", out)
	}
	if strings.Contains(out, "@") {
		t.Fatalf("log contains an address: %q", out)
	}
}

func TestGormConfigKeepsLevelAndSlowThreshold(t *testing.T) {
	cfg := loggerConfig()
	if cfg.LogLevel != logger.Warn {
		t.Errorf("LogLevel = %v, want Warn", cfg.LogLevel)
	}
	if cfg.SlowThreshold != 200*time.Millisecond {
		t.Errorf("SlowThreshold = %v, want 200ms", cfg.SlowThreshold)
	}
	if !cfg.ParameterizedQueries {
		t.Error("ParameterizedQueries must be true")
	}
	if !cfg.IgnoreRecordNotFoundError {
		t.Error("IgnoreRecordNotFoundError must be true")
	}
}

// TestInitializeOpensWithGormConfig pins that Initialize opens GORM with GormConfig: reverting it to
// &gorm.Config{} swaps in GORM's default logger, which writes bound values to stdout and logs
// not-found lookups, so the failed query never reaches logOutput and this test fails.
func TestInitializeOpensWithGormConfig(t *testing.T) {
	registerOnce.Do(func() { sql.Register("t030-empty", emptyDriver{}) })
	sqlDB, err := sql.Open("t030-empty", "")
	if err != nil {
		t.Fatalf("sql.Open: %v", err)
	}
	t.Cleanup(func() { _ = sqlDB.Close() })

	var buf bytes.Buffer
	prevOut, prevDB := logOutput, DB
	logOutput = &buf
	t.Cleanup(func() { logOutput, DB = prevOut, prevDB })

	db, err := initializeWith(postgres.New(postgres.Config{Conn: sqlDB}))
	if err != nil {
		t.Fatalf("initializeWith: %v", err)
	}
	if DB != db {
		t.Fatal("initializeWith must set the package DB")
	}

	var a account
	if err := db.Where("email = ?", "alice@example.com").First(&a).Error; !errors.Is(err, gorm.ErrRecordNotFound) {
		t.Fatalf("want ErrRecordNotFound, got %v", err)
	}
	if buf.Len() != 0 {
		t.Fatalf("not-found lookup must not be logged, got %q", buf.String())
	}

	var b boomAccount
	if err := db.Where("email = ?", "alice@example.com").First(&b).Error; err == nil {
		t.Fatal("want an error from the boom table")
	}
	out := buf.String()
	if !strings.Contains(out, "boom") {
		t.Fatalf("Initialize's logger must write failed statements to logOutput, got %q", out)
	}
	if strings.Contains(out, "@") {
		t.Fatalf("log contains an address: %q", out)
	}
}

func TestApplyPoolLimitsSetsTheSQLPool(t *testing.T) {
	db, _ := openWithLogBuffer(t)
	pool := config.DBPool{MaxOpenConns: 7, MaxIdleConns: 3, ConnMaxLifetime: time.Minute}
	if err := ApplyPoolLimits(db, pool); err != nil {
		t.Fatalf("ApplyPoolLimits: %v", err)
	}
	sqlDB, err := db.DB()
	if err != nil {
		t.Fatalf("db.DB: %v", err)
	}
	if got := sqlDB.Stats().MaxOpenConnections; got != 7 {
		t.Fatalf("MaxOpenConnections = %d, want 7", got)
	}
}
