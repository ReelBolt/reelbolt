package middleware

import (
	"log"
	"net/http"
)

// UserIDHeader carries the authenticated user id from the API to nginx, which logs it as
// "uid" in its JSON access log and strips it from the response the client sees (nginx.conf).
const UserIDHeader = "X-ReelBolt-Uid"

// SetUserIDHeader marks the response with the authenticated user id. Only the id: never an
// address or a token.
func SetUserIDHeader(w http.ResponseWriter, id string) {
	if id != "" {
		w.Header().Set(UserIDHeader, id)
	}
}

// statusRecorder remembers the status the wrapped handler wrote (200 when it wrote none).
type statusRecorder struct {
	http.ResponseWriter
	status int
}

func (s *statusRecorder) WriteHeader(code int) {
	if s.status == 0 {
		s.status = code
	}
	s.ResponseWriter.WriteHeader(code)
}

func (s *statusRecorder) Write(b []byte) (int, error) {
	if s.status == 0 {
		s.status = http.StatusOK
	}
	return s.ResponseWriter.Write(b)
}

// Flush keeps a streaming handler behind the gate working.
func (s *statusRecorder) Flush() {
	if f, ok := s.ResponseWriter.(http.Flusher); ok {
		if s.status == 0 {
			s.status = http.StatusOK
		}
		f.Flush()
	}
}

func (s *statusRecorder) Unwrap() http.ResponseWriter { return s.ResponseWriter }

// PlatformAdmin gates platform-wide routes. Being an organization Owner never
// satisfies it; only the platformAdmin authority does.
//
// Every request it lets through is logged with the user id, method, path (EscapedPath, quoted: a
// %0A in the path cannot forge a line; never the query string) and the handler's status; every refusal with the reason (forensics, T-007).
func PlatformAdmin(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		uc, ok := GetUserContext(r)
		if !ok || !uc.PlatformAdmin {
			reason, user := "not_platform_admin", uc.UserID
			if !ok {
				reason = "no_session"
			}
			if user == "" {
				user = "-"
			}
			log.Printf("admin_request outcome=refused user=%q method=%q path=%q reason=%q", user, r.Method, r.URL.EscapedPath(), reason)
			http.Error(w, `{"error":"forbidden"}`, http.StatusForbidden)
			return
		}
		rec := &statusRecorder{ResponseWriter: w}
		next.ServeHTTP(rec, r)
		status := rec.status
		if status == 0 {
			status = http.StatusOK
		}
		log.Printf("admin_request outcome=allowed user=%q method=%q path=%q status=%d", uc.UserID, r.Method, r.URL.EscapedPath(), status)
	})
}
