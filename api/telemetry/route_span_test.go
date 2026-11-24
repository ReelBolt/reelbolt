package telemetry

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/gorilla/mux"
	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
	"go.opentelemetry.io/otel"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"
)

// API-GO-10 / ARCH-7: with export on, a secret carried in a path segment (an invitation token,
// a pairing code) appears nowhere in the exported span: not in its name, not in any attribute.
// The span is named after the route template instead.
func TestWrap_ExportsTheRouteTemplateNotThePath(t *testing.T) {
	clearEnv(t)
	t.Setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1") // never dialled: spans go to the recorder

	router := mux.NewRouter()
	ok := func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusNoContent) }
	router.HandleFunc("/api/v1/auth/invitations/{token}", ok).Methods("GET")
	router.HandleFunc("/api/v1/runners/pair/{userCode}/approve", ok).Methods("POST")

	for _, tc := range []struct {
		method, path, secret, wantName string
	}{
		{"GET", "/api/v1/auth/invitations/s3cr3t-invite-token", "s3cr3t-invite-token", "GET /api/v1/auth/invitations/{token}"},
		{"POST", "/api/v1/runners/pair/PAIR-C0DE/approve", "PAIR-C0DE", "POST /api/v1/runners/pair/{userCode}/approve"},
		// No route: still never the raw path.
		{"GET", "/api/v1/nothing/s3cr3t-unrouted", "s3cr3t-unrouted", "GET unmatched"},
	} {
		rec := tracetest.NewSpanRecorder()
		tp := sdktrace.NewTracerProvider(sdktrace.WithSpanProcessor(rec))
		// Wrap, with the spans sent to the recorder instead of the global provider.
		h := otelhttp.NewHandler(routeSpan(recoverErrors(router), router), "go-api",
			otelhttp.WithTracerProvider(tp),
			otelhttp.WithSpanNameFormatter(spanName))

		h.ServeHTTP(httptest.NewRecorder(), httptest.NewRequestWithContext(context.Background(), tc.method, tc.path, nil))

		spans := rec.Ended()
		if len(spans) != 1 {
			t.Fatalf("%s: spans = %d", tc.path, len(spans))
		}
		if spans[0].Name() != tc.wantName {
			t.Errorf("%s: span name %q, want %q", tc.path, spans[0].Name(), tc.wantName)
		}
		if strings.Contains(spans[0].Name(), tc.secret) {
			t.Errorf("%s: the span name carries the secret", tc.path)
		}
		for _, a := range spans[0].Attributes() {
			if strings.Contains(a.Value.String(), tc.secret) {
				t.Errorf("%s: attribute %s = %q carries the secret", tc.path, a.Key, a.Value.String())
			}
		}
	}
}

// Wrap itself, end to end: the production wrapper, through a mux router, with the global
// tracer provider swapped for a recorder. The span is named after the route template and its
// url.path attribute is the template, never the raw path carrying the token.
func TestWrap_UsesTheRouteSpan(t *testing.T) {
	clearEnv(t)
	t.Setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1") // Enabled(); never dialled

	rec := tracetest.NewSpanRecorder()
	tp := sdktrace.NewTracerProvider(sdktrace.WithSpanProcessor(rec))
	saved := otel.GetTracerProvider()
	otel.SetTracerProvider(tp)
	t.Cleanup(func() { otel.SetTracerProvider(saved) })

	router := mux.NewRouter()
	router.HandleFunc("/api/v1/auth/invitations/{token}", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusNoContent)
	}).Methods("GET")

	Wrap(router).ServeHTTP(httptest.NewRecorder(),
		httptest.NewRequestWithContext(context.Background(), "GET", "/api/v1/auth/invitations/s3cr3t-invite-token", nil))

	spans := rec.Ended()
	if len(spans) != 1 {
		t.Fatalf("spans = %d, want 1", len(spans))
	}
	const template = "/api/v1/auth/invitations/{token}"
	if got := spans[0].Name(); got != "GET "+template {
		t.Errorf("span name %q, want %q", got, "GET "+template)
	}
	var urlPath string
	for _, a := range spans[0].Attributes() {
		if strings.Contains(a.Value.String(), "s3cr3t-invite-token") {
			t.Errorf("attribute %s = %q carries the token", a.Key, a.Value.String())
		}
		if a.Key == "url.path" {
			urlPath = a.Value.String()
		}
	}
	if urlPath != template {
		t.Errorf("url.path = %q, want %q", urlPath, template)
	}
}
