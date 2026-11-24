package database

import (
	"fmt"
	"io"
	"log"
	"os"
	"time"

	"github.com/vecchiotom/reelbolt/config"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"
	"gorm.io/gorm/logger"
)

var DB *gorm.DB

// loggerConfig keeps GORM's default level (Warn) and slow threshold (200ms), but logs statements
// with placeholders instead of bound values and stays quiet on ErrRecordNotFound: a lookup by email
// that misses is normal flow (signup, forgot-password, resend) and must not write the address (F-4).
func loggerConfig() logger.Config {
	return logger.Config{
		SlowThreshold:             200 * time.Millisecond,
		LogLevel:                  logger.Warn,
		Colorful:                  true,
		ParameterizedQueries:      true,
		IgnoreRecordNotFoundError: true,
	}
}

// GormConfig returns the GORM configuration with its logger writing to w.
func GormConfig(w io.Writer) *gorm.Config {
	return &gorm.Config{Logger: logger.New(log.New(w, "\r\n", log.LstdFlags), loggerConfig())}
}

// logOutput is where Initialize's GORM logger writes; tests swap it to capture the log.
var logOutput io.Writer = os.Stdout

func Initialize(dsn string) (*gorm.DB, error) {
	return initializeWith(postgres.Open(dsn))
}

// initializeWith opens GORM on dialector with GormConfig(logOutput) and stores it in DB; it is
// Initialize's body, split out so a test can drive it with a fake connection.
func initializeWith(dialector gorm.Dialector) (*gorm.DB, error) {
	db, err := gorm.Open(dialector, GormConfig(logOutput))
	if err != nil {
		return nil, err
	}
	DB = db
	return db, nil
}

// ApplyPoolLimits caps the database/sql pool under db. database/sql defaults to an unbounded number
// of open connections and a lifetime of forever; on a Postgres shared by every service that is a
// way to exhaust max_connections, so the Go API always runs with explicit limits.
func ApplyPoolLimits(db *gorm.DB, pool config.DBPool) error {
	sqlDB, err := db.DB()
	if err != nil {
		return fmt.Errorf("database handle: %w", err)
	}
	sqlDB.SetMaxOpenConns(pool.MaxOpenConns)
	sqlDB.SetMaxIdleConns(pool.MaxIdleConns)
	sqlDB.SetConnMaxLifetime(pool.ConnMaxLifetime)
	return nil
}
