package telemetry

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"

	"go.opentelemetry.io/otel"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"
)

func clearEnv(t *testing.T) {
	t.Helper()
	for _, k := range []string{"OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"} {
		t.Setenv(k, "")
	}
}

func TestDisabledWithoutEndpoint(t *testing.T) {
	clearEnv(t)
	before := otel.GetTracerProvider()

	shutdown, enabled, err := Init(context.Background(), "test")
	if err != nil || enabled {
		t.Fatalf("Init = enabled %v, err %v; want disabled, nil", enabled, err)
	}
	if err := shutdown(context.Background()); err != nil {
		t.Fatal(err)
	}
	if otel.GetTracerProvider() != before {
		t.Fatal("global tracer provider changed with no endpoint set")
	}

	called := false
	h := http.HandlerFunc(func(http.ResponseWriter, *http.Request) { called = true })
	rec := httptest.NewRecorder()
	Wrap(h).ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), "GET", "/x", nil))
	if !called || rec.Code != http.StatusOK {
		t.Fatalf("handler not served unchanged: called=%v status=%d", called, rec.Code)
	}
}

func TestEnabledWithEndpoint(t *testing.T) {
	clearEnv(t)
	t.Setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1")
	if !Enabled() {
		t.Fatal("Enabled() = false with endpoint set")
	}
}

func TestTagOrganization(t *testing.T) {
	rec := tracetest.NewSpanRecorder()
	tp := sdktrace.NewTracerProvider(sdktrace.WithSpanProcessor(rec))
	ctx, span := tp.Tracer("t").Start(context.Background(), "s")
	TagOrganization(ctx, "org-1")
	TagOrganization(ctx, "")
	span.End()

	spans := rec.Ended()
	if len(spans) != 1 {
		t.Fatalf("spans = %d", len(spans))
	}
	found := false
	for _, a := range spans[0].Attributes() {
		if string(a.Key) == OrgIDKey && a.Value.AsString() == "org-1" {
			found = true
		}
	}
	if !found {
		t.Fatal("reelbolt.org_id not set")
	}
}
