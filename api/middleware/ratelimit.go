package middleware

import (
	"net"
	"net/http"
	"strings"
	"time"

	"github.com/google/uuid"
)

// Per-account limits on emailed single-use tokens (docs/design/saas-accounts.md,
// C5). The count comes from user_tokens rows, not process memory, so the limit
// holds across API replicas and restarts.
const (
	EmailTokenMaxPerHour = 3
	EmailTokenWindow     = time.Hour

	// Per-IP signup caps; counted from application_users.signup_ip.
	SignupMaxPerIPHour = 3
	SignupMaxPerIPDay  = 10

	// Per-IP cap on starting a device pairing; counted from
	// runner_pairing_requests.request_ip. Pairing is unauthenticated, so this is the
	// only thing that stops one host from filling the table with pending requests.
	RunnerPairStartMaxPerIPHour = 10
	RunnerPairStartWindow       = time.Hour
)

// TokenCounter counts the emailed tokens issued to a user since a moment.
type TokenCounter interface {
	CountRecentTokens(userID uuid.UUID, since time.Time) (int, error)
}

// SignupCounter counts accounts created from an IP address since a moment.
type SignupCounter interface {
	CountSignupsFromIP(ip string, since time.Time) (int, error)
}

// EmailTokenLimiter caps how many verification/reset emails one account can be
// sent per window, which bounds email bombing of a victim's inbox.
type EmailTokenLimiter struct {
	Counter TokenCounter
	Max     int
	Window  time.Duration
}

// NewEmailTokenLimiter returns the standard 3-per-hour limiter.
func NewEmailTokenLimiter(c TokenCounter) EmailTokenLimiter {
	return EmailTokenLimiter{Counter: c, Max: EmailTokenMaxPerHour, Window: EmailTokenWindow}
}

// Allow reports whether another token may be issued to userID at now. A
// counting error denies: a database that cannot answer must not become an
// unthrottled mail cannon.
func (l EmailTokenLimiter) Allow(userID uuid.UUID, now time.Time) bool {
	n, err := l.Counter.CountRecentTokens(userID, now.Add(-l.Window))
	return err == nil && n < l.Max
}

// SignupIPLimiter caps account creation per client IP (3/hour, 10/day).
type SignupIPLimiter struct {
	Counter SignupCounter
	PerHour int
	PerDay  int
}

// NewSignupIPLimiter returns the standard limiter.
func NewSignupIPLimiter(c SignupCounter) SignupIPLimiter {
	return SignupIPLimiter{Counter: c, PerHour: SignupMaxPerIPHour, PerDay: SignupMaxPerIPDay}
}

// Allow reports whether ip may create another account at now. Errors deny.
func (l SignupIPLimiter) Allow(ip string, now time.Time) bool {
	hour, err := l.Counter.CountSignupsFromIP(ip, now.Add(-time.Hour))
	if err != nil || hour >= l.PerHour {
		return false
	}
	day, err := l.Counter.CountSignupsFromIP(ip, now.Add(-24*time.Hour))
	return err == nil && day < l.PerDay
}

// PairingCounter counts device-pairing requests opened from an IP since a moment.
type PairingCounter interface {
	CountPairStartsFromIP(ip string, since time.Time) (int, error)
}

// RunnerPairStartLimiter caps how many device pairings one client IP may open per
// window (WP E4b step 1).
type RunnerPairStartLimiter struct {
	Counter PairingCounter
	Max     int
	Window  time.Duration
}

// NewRunnerPairStartLimiter returns the standard limiter.
func NewRunnerPairStartLimiter(c PairingCounter) RunnerPairStartLimiter {
	return RunnerPairStartLimiter{Counter: c, Max: RunnerPairStartMaxPerIPHour, Window: RunnerPairStartWindow}
}

// Allow reports whether ip may start another pairing at now. As with every other
// limiter here, a counting error denies: a database that cannot answer must not
// become an open door.
func (l RunnerPairStartLimiter) Allow(ip string, now time.Time) bool {
	n, err := l.Counter.CountPairStartsFromIP(ip, now.Add(-l.Window))
	return err == nil && n < l.Max
}

// ClientIP is the client address as nginx forwards it: X-Real-IP first, then the
// head of X-Forwarded-For, then the socket peer. It is used for rate limiting and
// abuse review only, never for authorization.
//
// A header that is present but is not an address falls THROUGH to the next source
// rather than ending the search: a limiter keyed on a garbage value would be a
// limiter that lets one host send unlimited requests.
func ClientIP(r *http.Request) string {
	if ip := strings.TrimSpace(r.Header.Get("X-Real-IP")); net.ParseIP(ip) != nil {
		return ip
	}
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		if ip := strings.TrimSpace(strings.Split(xff, ",")[0]); net.ParseIP(ip) != nil {
			return ip
		}
	}
	ip, _, _ := net.SplitHostPort(r.RemoteAddr)
	if net.ParseIP(ip) == nil {
		return ""
	}
	return ip
}
