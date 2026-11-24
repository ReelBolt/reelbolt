package middleware

import (
	"mime"
	"net/http"
	"strings"
)

// RequireJSON is the second CSRF layer C11 F-6 found missing: the session
// cookies are SameSite=Lax, and that was the only thing standing between a
// cross-site request and a state-changing handler.
//
// The rule it enforces is the one the plan names: the account endpoints take
// JSON and nothing else. It matters because a cross-site `fetch` may send
// text/plain, application/x-www-form-urlencoded or multipart/form-data without a
// CORS preflight, while application/json is not a CORS-safelisted content type,
// so the browser asks this API for permission first (and the API never grants
// it cross-origin). A request that cannot be sent without the site's own
// JavaScript cannot be forged by another site's.
//
// A request that carries no body is passed through untouched: it has nothing to
// smuggle, and the handler answers it as it always did (a POST with an empty
// body is a 400 from the decoder, not a 415 from here). A request that does
// carry a body must state a JSON content type — application/json, or any
// +json media type (application/vnd.api+json and friends), with or without
// parameters such as charset=utf-8.
//
// This is defence in depth, never the only layer: SameSite=Lax, the httpOnly
// cookie, and the Authorization header nginx rewrites from it all remain.
func RequireJSON(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !hasBody(r) || isJSONContentType(r.Header.Get("Content-Type")) {
			next(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusUnsupportedMediaType)
		_, _ = w.Write([]byte(`{"error":"unsupported media type","message":"This endpoint accepts application/json only."}`))
	}
}

// hasBody reports whether the request declares a body. A chunked request
// (ContentLength -1) declares an unknown length, which counts as a body; only a
// declared length of 0 is treated as empty.
func hasBody(r *http.Request) bool {
	return r.ContentLength != 0
}

// isJSONContentType accepts application/json and every +json media type. An
// unparseable or absent content type is not JSON.
func isJSONContentType(value string) bool {
	if strings.TrimSpace(value) == "" {
		return false
	}
	mediaType, _, err := mime.ParseMediaType(value)
	if err != nil {
		return false
	}
	return mediaType == "application/json" || strings.HasSuffix(mediaType, "+json")
}
