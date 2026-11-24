// Package telemetry wires OpenTelemetry for the Go API (WP F9).
//
// Everything is gated on OTEL_EXPORTER_OTLP_ENDPOINT (or the per-signal
// _TRACES_/_METRICS_ variants): when none is set, Init installs nothing, the
// global tracer/meter providers stay the no-op defaults, and Wrap returns the
// handler unchanged, so a deployment that has not opted in behaves exactly as
// before. The exporters read the standard OTEL_EXPORTER_OTLP_* variables
// (endpoint, headers, protocol) themselves, so the Grafana Cloud auth header
// lives in OTEL_EXPORTER_OTLP_HEADERS and never in application config.
package telemetry

import (
	"context"
	"fmt"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/gorilla/mux"
	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetrichttp"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracehttp"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/propagation"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/trace"
)

// The closed set of error kinds (alpha-launch T-018). A metric dimension must be bounded, so
// RecordError collapses anything else to ErrorKindOther.
const (
	ErrorKindUnhandled  = "unhandled"
	ErrorKindHandled    = "handled"
	ErrorKindUpstream   = "upstream"
	ErrorKindValidation = "validation"
	ErrorKindOther      = "other"
)

// errorCount is the error-tracking counter. It is created in Init, once the meter provider is
// installed; until then (and when export is disabled) it is nil and RecordError only touches the
// span, which is a no-op without a provider.
var errorCount metric.Int64Counter

// FunnelEventSignup is the alpha funnel's signup event (alpha-launch T-018). The Go API emits it,
// not the browser: the signup endpoint answers a uniform 202 whether or not the address already had
// an account, so the client genuinely cannot know an account was created — and firing on the 202
// would count an address that already had one. The other three funnel events come from the browser
// through the Inference API's ingest endpoint.
const FunnelEventSignup = "alpha.signup"

// funnelCount is the alpha funnel counter, created in Init beside errorCount.
var funnelCount metric.Int64Counter

// OrgIDKey is the span attribute carrying the tenant. Spans only: metrics must
// never carry it (the usage ledger holds per-org numbers).
const OrgIDKey = "reelbolt.org_id"

// ServiceName is the default resource service.name.
const ServiceName = "ReelBolt.GoApi"

// Enabled reports whether the operator pointed the process at an OTLP collector.
func Enabled() bool {
	for _, k := range []string{
		"OTEL_EXPORTER_OTLP_ENDPOINT",
		"OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
		"OTEL_EXPORTER_OTLP_METRICS_ENDPOINT",
	} {
		if strings.TrimSpace(os.Getenv(k)) != "" {
			return true
		}
	}
	return false
}

// Init installs the global providers when export is enabled and returns a
// shutdown function (always non-nil, a no-op when disabled).
func Init(ctx context.Context, version string) (shutdown func(context.Context) error, enabled bool, err error) {
	if !Enabled() {
		return func(context.Context) error { return nil }, false, nil
	}

	env := firstNonEmpty(os.Getenv("OTEL_DEPLOYMENT_ENVIRONMENT"), os.Getenv("ENVIRONMENT"), "production")
	res, err := resource.New(ctx,
		resource.WithAttributes(
			attribute.String("service.name", ServiceName),
			attribute.String("service.version", version),
			attribute.String("deployment.environment", env),
		),
		resource.WithFromEnv(), // OTEL_RESOURCE_ATTRIBUTES / OTEL_SERVICE_NAME win over the defaults
	)
	if err != nil {
		return nil, false, err
	}

	traceExp, err := otlptracehttp.New(ctx)
	if err != nil {
		return nil, false, err
	}
	tp := sdktrace.NewTracerProvider(sdktrace.WithBatcher(traceExp), sdktrace.WithResource(res))

	metricExp, err := otlpmetrichttp.New(ctx)
	if err != nil {
		return nil, false, err
	}
	mp := sdkmetric.NewMeterProvider(
		sdkmetric.WithReader(sdkmetric.NewPeriodicReader(metricExp)),
		sdkmetric.WithResource(res),
	)

	otel.SetTracerProvider(tp)
	otel.SetMeterProvider(mp)
	otel.SetTextMapPropagator(propagation.NewCompositeTextMapPropagator(propagation.TraceContext{}, propagation.Baggage{}))

	// Error tracking (alpha-launch T-018). Created here so the counter belongs to the installed
	// provider; a failure to create it leaves errorCount nil and only drops the metric, never the
	// request.
	if counter, cerr := otel.Meter(ServiceName).Int64Counter(
		"reelbolt.errors",
		metric.WithDescription("Errors observed by the service, tagged with kind"),
		metric.WithUnit("{error}"),
	); cerr == nil {
		errorCount = counter
	}

	// The alpha funnel's server-emitted event (alpha-launch T-018). Same provider, same destination.
	if counter, ferr := otel.Meter(ServiceName).Int64Counter(
		"reelbolt.alpha.funnel",
		metric.WithDescription("Alpha funnel events emitted by the service, tagged with the event name"),
		metric.WithUnit("{event}"),
	); ferr == nil {
		funnelCount = counter
	}

	return func(c context.Context) error {
		c, cancel := context.WithTimeout(c, 5*time.Second)
		defer cancel()
		terr := tp.Shutdown(c)
		merr := mp.Shutdown(c)
		if terr != nil {
			return terr
		}
		return merr
	}, true, nil
}

// Wrap instruments an http.Handler with otelhttp when export is enabled and
// returns it unchanged otherwise. When it is enabled it also records a panic as
// an error signal before re-panicking, so the response the caller sees is
// unchanged (net/http's own recovery still runs).
func Wrap(h http.Handler) http.Handler {
	if !Enabled() {
		return h
	}
	return otelhttp.NewHandler(routeSpan(recoverErrors(h), h), "go-api",
		otelhttp.WithSpanNameFormatter(spanName))
}

// spanName names a span before routing: the method alone, never the raw path. routeSpan then
// renames it to the route template.
func spanName(_ string, r *http.Request) string { return r.Method }

// unmatchedRoute names the span of a request no route matched.
const unmatchedRoute = "unmatched"

// routeSpan names the request's span after the mux ROUTE TEMPLATE and overwrites the
// url.path attribute otelhttp took from the raw URL with it, so a secret carried in a path
// segment (an invitation token, a pairing code) is never exported (API-GO-10, ARCH-7). The
// template is resolved with the router's own matcher; a handler that is not a mux router, or
// a request no route matches, is exported as "unmatched", never as the raw path.
func routeSpan(next, routes http.Handler) http.Handler {
	router, _ := routes.(*mux.Router)
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if span := trace.SpanFromContext(r.Context()); span.IsRecording() {
			template := unmatchedRoute
			if router != nil {
				var match mux.RouteMatch
				if router.Match(r, &match) && match.Route != nil {
					if t, err := match.Route.GetPathTemplate(); err == nil && t != "" {
						template = t
					}
				}
			}
			span.SetName(r.Method + " " + template)
			span.SetAttributes(attribute.String("url.path", template), attribute.String("http.route", template))
		}
		next.ServeHTTP(w, r)
	})
}

// recoverErrors records a panic as an error signal (alpha-launch T-018), then
// re-panics. Reporting only: it never refuses, rewrites or swallows anything.
func recoverErrors(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		defer func() {
			rec := recover()
			if rec == nil {
				return
			}
			err, ok := rec.(error)
			if !ok {
				err = fmt.Errorf("%v", rec)
			}
			RecordError(r.Context(), ErrorKindUnhandled, err)
			panic(rec)
		}()
		next.ServeHTTP(w, r)
	})
}

// RecordError counts one error and stamps it on the active span (alpha-launch
// T-018). Reporting only, and it never panics: only the error's TYPE is
// recorded, never its message, which can carry user prose.
func RecordError(ctx context.Context, kind string, err error) {
	if err == nil {
		return
	}
	k := normalizeErrorKind(kind)
	if errorCount != nil {
		errorCount.Add(ctx, 1, metric.WithAttributes(attribute.String("kind", k)))
	}
	span := trace.SpanFromContext(ctx)
	if !span.IsRecording() {
		return
	}
	span.SetAttributes(
		attribute.String("error.kind", k),
		attribute.String("error.type", fmt.Sprintf("%T", err)),
	)
	span.SetStatus(codes.Error, fmt.Sprintf("%T", err))
}

// RecordFunnelEvent counts one alpha funnel event. Reporting only, and safe with no provider
// (a no-op counter), so a deployment that has not opted into export is unchanged.
func RecordFunnelEvent(ctx context.Context, event string) {
	if funnelCount == nil {
		return
	}
	funnelCount.Add(ctx, 1, metric.WithAttributes(attribute.String("event", event)))
}

func normalizeErrorKind(kind string) string {
	switch strings.ToLower(strings.TrimSpace(kind)) {
	case ErrorKindUnhandled:
		return ErrorKindUnhandled
	case ErrorKindHandled:
		return ErrorKindHandled
	case ErrorKindUpstream:
		return ErrorKindUpstream
	case ErrorKindValidation:
		return ErrorKindValidation
	default:
		return ErrorKindOther
	}
}

// TagOrganization stamps the active span with the tenant. Safe with no active
// span (a no-op span ignores attributes).
func TagOrganization(ctx context.Context, orgID string) {
	if orgID == "" {
		return
	}
	trace.SpanFromContext(ctx).SetAttributes(attribute.String(OrgIDKey, orgID))
}

func firstNonEmpty(vals ...string) string {
	for _, v := range vals {
		if strings.TrimSpace(v) != "" {
			return v
		}
	}
	return ""
}
