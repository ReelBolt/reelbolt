package telemetry

import (
	"context"
	"database/sql"
	"testing"

	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/metric/metricdata"
)

func TestDBPoolMetricsReportPoolStats(t *testing.T) {
	reader := sdkmetric.NewManualReader()
	mp := sdkmetric.NewMeterProvider(sdkmetric.WithReader(reader))
	t.Cleanup(func() { _ = mp.Shutdown(context.Background()) })

	stats := sql.DBStats{MaxOpenConnections: 10, InUse: 3, Idle: 2, WaitCount: 7}
	if _, err := registerDBPoolMetrics(mp.Meter("t"), func() sql.DBStats { return stats }); err != nil {
		t.Fatalf("register: %v", err)
	}

	var rm metricdata.ResourceMetrics
	if err := reader.Collect(context.Background(), &rm); err != nil {
		t.Fatalf("collect: %v", err)
	}
	got := map[string]int64{}
	for _, sm := range rm.ScopeMetrics {
		for _, m := range sm.Metrics {
			var points []metricdata.DataPoint[int64]
			switch d := m.Data.(type) {
			case metricdata.Gauge[int64]:
				points = d.DataPoints
			case metricdata.Sum[int64]:
				if !d.IsMonotonic {
					t.Errorf("%s: want a monotonic sum", m.Name)
				}
				points = d.DataPoints
			default:
				t.Fatalf("%s: unexpected data %T", m.Name, m.Data)
			}
			for _, p := range points {
				key := m.Name
				if v, ok := p.Attributes.Value(dbConnectionStateKey); ok {
					key += "/" + v.AsString()
				}
				got[key] = p.Value
			}
		}
	}
	want := map[string]int64{
		"db.client.connection.count/used": 3,
		"db.client.connection.count/idle": 2,
		"db.client.connection.max":        10,
		"db.client.connection.wait_count": 7,
	}
	for k, v := range want {
		if got[k] != v {
			t.Errorf("%s = %d, want %d (all: %v)", k, got[k], v, got)
		}
	}
	if len(got) != len(want) {
		t.Errorf("series = %v, want exactly %v", got, want)
	}
}

func TestRegisterDBPoolMetricsIsANoOpWithoutExport(t *testing.T) {
	clearEnv(t)
	called := false
	if err := RegisterDBPoolMetrics(func() sql.DBStats { called = true; return sql.DBStats{} }); err != nil {
		t.Fatalf("RegisterDBPoolMetrics: %v", err)
	}
	if called {
		t.Fatal("stats read with telemetry off")
	}
}
