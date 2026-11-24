package middleware

import (
	"errors"
	"testing"
	"time"

	"github.com/google/uuid"
)

type fakeCounter struct {
	tokens map[uuid.UUID][]time.Time
	ips    map[string][]time.Time
	err    error
}

func (f fakeCounter) CountRecentTokens(u uuid.UUID, since time.Time) (int, error) {
	n := 0
	for _, t := range f.tokens[u] {
		if !t.Before(since) {
			n++
		}
	}
	return n, f.err
}

func (f fakeCounter) CountSignupsFromIP(ip string, since time.Time) (int, error) {
	n := 0
	for _, t := range f.ips[ip] {
		if !t.Before(since) {
			n++
		}
	}
	return n, f.err
}

func TestEmailTokenLimiter(t *testing.T) {
	now := time.Date(2026, 10, 6, 12, 0, 0, 0, time.UTC)
	u := uuid.New()
	c := fakeCounter{tokens: map[uuid.UUID][]time.Time{u: {
		now.Add(-59 * time.Minute), now.Add(-30 * time.Minute), now.Add(-2 * time.Hour), // last is outside the window
	}}}
	l := NewEmailTokenLimiter(c)
	if !l.Allow(u, now) {
		t.Fatal("2 tokens in the hour: a third is allowed")
	}
	c.tokens[u] = append(c.tokens[u], now.Add(-time.Minute))
	if l.Allow(u, now) {
		t.Fatal("3 tokens in the hour: a fourth must be refused")
	}
	if !l.Allow(u, now.Add(31*time.Minute)) {
		t.Fatal("once the oldest tokens age out, mail flows again")
	}
	if !l.Allow(uuid.New(), now) {
		t.Fatal("limits are per user")
	}
	if NewEmailTokenLimiter(fakeCounter{err: errors.New("db down")}).Allow(u, now) {
		t.Fatal("a counting error must deny")
	}
}

func TestSignupIPLimiter(t *testing.T) {
	now := time.Date(2026, 10, 6, 12, 0, 0, 0, time.UTC)
	ts := func(hoursAgo ...float64) []time.Time {
		var out []time.Time
		for _, h := range hoursAgo {
			out = append(out, now.Add(-time.Duration(h*float64(time.Hour))))
		}
		return out
	}
	l := NewSignupIPLimiter(fakeCounter{ips: map[string][]time.Time{
		"hourly": ts(0.1, 0.2, 0.3),
		"daily":  ts(2, 3, 4, 5, 6, 7, 8, 9, 10, 11),
		"fine":   ts(0.5, 5),
	}})
	for ip, want := range map[string]bool{"hourly": false, "daily": false, "fine": true, "new": true} {
		if got := l.Allow(ip, now); got != want {
			t.Errorf("%s: got %v want %v", ip, got, want)
		}
	}
	if NewSignupIPLimiter(fakeCounter{err: errors.New("x")}).Allow("a", now) {
		t.Fatal("error must deny")
	}
}
