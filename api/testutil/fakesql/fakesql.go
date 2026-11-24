// Package fakesql is a recording database/sql driver for tests that need to see the SQL a GORM
// query produces without a database. A Handler answers each query with canned rows; every
// statement (query or exec) is recorded in order.
package fakesql

import (
	"context"
	"database/sql"
	"database/sql/driver"
	"fmt"
	"io"
	"strings"
	"sync"
	"sync/atomic"
	"testing"

	"gorm.io/driver/postgres"
	"gorm.io/gorm"

	"github.com/vecchiotom/reelbolt/database"
)

// Handler answers one statement. Returning nil columns means "no rows" for a query.
type Handler func(query string, args []driver.Value) (cols []string, rows [][]driver.Value)

// Stmt is one recorded statement.
type Stmt struct {
	Query string
	Args  []driver.Value
	Exec  bool
}

// DB is an opened fake database.
type DB struct {
	mu      sync.Mutex
	stmts   []Stmt
	handler Handler
}

// Statements returns a copy of what was recorded so far.
func (d *DB) Statements() []Stmt {
	d.mu.Lock()
	defer d.mu.Unlock()
	return append([]Stmt(nil), d.stmts...)
}

// Contains reports whether any recorded statement contains all the fragments (case-insensitive).
func (d *DB) Contains(frags ...string) bool {
	for _, s := range d.Statements() {
		q := strings.ToLower(s.Query)
		ok := true
		for _, f := range frags {
			if !strings.Contains(q, strings.ToLower(f)) {
				ok = false
				break
			}
		}
		if ok {
			return true
		}
	}
	return false
}

func (d *DB) record(s Stmt) {
	d.mu.Lock()
	d.stmts = append(d.stmts, s)
	d.mu.Unlock()
}

var (
	seq     atomic.Int64
	reg     sync.Map // dsn -> *DB
	regOnce sync.Once
)

type drv struct{}

func (drv) Open(dsn string) (driver.Conn, error) {
	v, ok := reg.Load(dsn)
	if !ok {
		return nil, fmt.Errorf("fakesql: unknown dsn %q", dsn)
	}
	return &conn{db: v.(*DB)}, nil
}

type conn struct{ db *DB }

func (c *conn) Prepare(q string) (driver.Stmt, error) { return &stmt{c: c, q: q}, nil }
func (c *conn) Close() error                          { return nil }
func (c *conn) Begin() (driver.Tx, error)             { return tx{}, nil }
func (c *conn) BeginTx(context.Context, driver.TxOptions) (driver.Tx, error) {
	return tx{}, nil
}

type tx struct{}

func (tx) Commit() error   { return nil }
func (tx) Rollback() error { return nil }

type stmt struct {
	c *conn
	q string
}

func (s *stmt) Close() error  { return nil }
func (s *stmt) NumInput() int { return -1 }
func (s *stmt) Exec(args []driver.Value) (driver.Result, error) {
	s.c.db.record(Stmt{Query: s.q, Args: args, Exec: true})
	return driver.RowsAffected(1), nil
}
func (s *stmt) Query(args []driver.Value) (driver.Rows, error) {
	s.c.db.record(Stmt{Query: s.q, Args: args})
	var cols []string
	var rows [][]driver.Value
	if s.c.db.handler != nil {
		cols, rows = s.c.db.handler(s.q, args)
	}
	return &rowset{cols: cols, rows: rows}, nil
}

type rowset struct {
	cols []string
	rows [][]driver.Value
	i    int
}

func (r *rowset) Columns() []string { return r.cols }
func (r *rowset) Close() error      { return nil }
func (r *rowset) Next(dest []driver.Value) error {
	if r.i >= len(r.rows) {
		return io.EOF
	}
	copy(dest, r.rows[r.i])
	r.i++
	return nil
}

// Install opens a GORM handle over the fake driver and makes it database.DB for the test.
func Install(t testing.TB, h Handler) *DB {
	t.Helper()
	regOnce.Do(func() { sql.Register("reelbolt-fakesql", drv{}) })
	dsn := fmt.Sprintf("fake-%d", seq.Add(1))
	fd := &DB{handler: h}
	reg.Store(dsn, fd)
	sqlDB, err := sql.Open("reelbolt-fakesql", dsn)
	if err != nil {
		t.Fatalf("fakesql open: %v", err)
	}
	db, err := gorm.Open(postgres.New(postgres.Config{Conn: sqlDB}), database.GormConfig(io.Discard))
	if err != nil {
		t.Fatalf("fakesql gorm: %v", err)
	}
	prev := database.DB
	database.DB = db
	t.Cleanup(func() {
		database.DB = prev
		_ = sqlDB.Close()
		reg.Delete(dsn)
	})
	return fd
}
