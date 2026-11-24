package telemetry

import (
	"context"
	"database/sql"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/metric"
)

// Connection-pool instruments, named after the OpenTelemetry database client semantic
// conventions so the Go API's series line up with the .NET services' Npgsql meter:
// db.client.connection.count{db.client.connection.state=used|idle}, db.client.connection.max,
// and db.client.connection.wait_count (cumulative number of times a caller waited for a
// connection because the pool was at its cap — the early sign that the cap is too low).
const (
	dbConnectionCount     = "db.client.connection.count"
	dbConnectionMax       = "db.client.connection.max"
	dbConnectionWaitCount = "db.client.connection.wait_count"
	dbConnectionStateKey  = "db.client.connection.state"
)

var (
	dbStateUsed = metric.WithAttributeSet(attribute.NewSet(attribute.String(dbConnectionStateKey, "used")))
	dbStateIdle = metric.WithAttributeSet(attribute.NewSet(attribute.String(dbConnectionStateKey, "idle")))
)

// RegisterDBPoolMetrics publishes the pool statistics of the Go API's database handle. It is a
// no-op when export is off (Enabled is false); call it once, after the database is open. The
// values are read from stats on each collection, so nothing runs between exports.
func RegisterDBPoolMetrics(stats func() sql.DBStats) error {
	if !Enabled() {
		return nil
	}
	_, err := registerDBPoolMetrics(otel.Meter(ServiceName), stats)
	return err
}

func registerDBPoolMetrics(meter metric.Meter, stats func() sql.DBStats) (metric.Registration, error) {
	count, err := meter.Int64ObservableGauge(dbConnectionCount,
		metric.WithDescription("Connections in the Go API's database pool, by state (used or idle)."),
		metric.WithUnit("{connection}"))
	if err != nil {
		return nil, err
	}
	maxConns, err := meter.Int64ObservableGauge(dbConnectionMax,
		metric.WithDescription("The most connections the Go API's database pool may open (DB_MAX_OPEN_CONNS)."),
		metric.WithUnit("{connection}"))
	if err != nil {
		return nil, err
	}
	waits, err := meter.Int64ObservableCounter(dbConnectionWaitCount,
		metric.WithDescription("Times a caller waited for a connection because the Go API's pool was full."),
		metric.WithUnit("{wait}"))
	if err != nil {
		return nil, err
	}
	return meter.RegisterCallback(func(_ context.Context, o metric.Observer) error {
		s := stats()
		o.ObserveInt64(count, int64(s.InUse), dbStateUsed)
		o.ObserveInt64(count, int64(s.Idle), dbStateIdle)
		o.ObserveInt64(maxConns, int64(s.MaxOpenConnections))
		o.ObserveInt64(waits, s.WaitCount)
		return nil
	}, count, maxConns, waits)
}
