package internalmail

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"testing"
	"time"
)

// fakeCounterStore is the in-memory stand-in for mail_rate_counters: one count per (bucket, window).
// Decide is atomic under one mutex, like the store's transaction under advisory locks.
type fakeCounterStore struct {
	mu     sync.Mutex
	counts map[string]int
	err    error
}

func newFakeCounterStore() *fakeCounterStore {
	return &fakeCounterStore{counts: map[string]int{}}
}

func fakeKey(bucket string, windowStart time.Time) string {
	return bucket + "|" + windowStart.UTC().Format(time.RFC3339)
}

func (s *fakeCounterStore) Decide(_ context.Context, buckets []bucketWindow, decide func([]windowCounts) bool) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.err != nil {
		return false, s.err
	}
	counts := make([]windowCounts, len(buckets))
	for i, b := range buckets {
		counts[i] = windowCounts{
			current:  s.counts[fakeKey(b.bucket, b.start)],
			previous: s.counts[fakeKey(b.bucket, b.start.Add(-b.length))],
		}
	}
	if !decide(counts) {
		return false, nil
	}
	for _, b := range buckets {
		s.counts[fakeKey(b.bucket, b.start)]++
	}
	return true, nil
}

// count is the stored count of one bucket's window.
func (s *fakeCounterStore) count(bucket string, windowStart time.Time) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.counts[fakeKey(bucket, windowStart)]
}

func (s *fakeCounterStore) buckets() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]string, 0, len(s.counts))
	for k := range s.counts {
		out = append(out, k)
	}
	return out
}

func defaultLimits() relayLimits {
	return relayLimitsFromEnv(func(string) string { return "" })
}

// C1: the shared test vectors, asserted verbatim in the C# and the Go suites.
func TestCanonicalMailbox_SharedVectors(t *testing.T) {
	vectors := []struct{ in, want string }{
		{"Victim@Gmail.com", "victim@gmail.com"},
		{" v.i.c.t.i.m+news@googlemail.com ", "victim@gmail.com"},
		{"victim+1@gmail.com", "victim@gmail.com"},
		{"a.b+x@example.com", "a.b@example.com"},
		{"a+b+c@Example.COM", "a@example.com"},
		{"+tag@example.com", "+tag@example.com"},
		{"no-at-sign", "no-at-sign"},
		{`"q@x"@example.com`, `"q@x"@example.com`},
	}
	for _, v := range vectors {
		if got := CanonicalMailbox(v.in); got != v.want {
			t.Errorf("CanonicalMailbox(%q) = %q, want %q", v.in, got, v.want)
		}
	}
}

func TestRelayLimitsFromEnv(t *testing.T) {
	tests := []struct {
		name               string
		domain, global     string
		wantDom, wantGlobl int
	}{
		{"defaults when unset", "", "", 30, 100},
		{"explicit values", "12", "250", 12, 250},
		{"whitespace is trimmed", " 7 ", " 9 ", 7, 9},
		{"non-numeric falls back", "lots", "1e3", 30, 100},
		{"zero falls back", "0", "0", 30, 100},
		{"negative falls back", "-5", "-1", 30, 100},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			env := map[string]string{
				"MARKETING_RELAY_PER_DOMAIN_PER_HOUR": tc.domain,
				"MARKETING_RELAY_GLOBAL_PER_HOUR":     tc.global,
			}
			l := relayLimitsFromEnv(func(k string) string { return env[k] })
			if l.domainPerHour != tc.wantDom || l.globalPerHour != tc.wantGlobl || l.recipientPerDay != 3 {
				t.Fatalf("got %+v, want domain %d global %d recipient 3", l, tc.wantDom, tc.wantGlobl)
			}
		})
	}
}

// F-3.1: plus and dot variants and googlemail.com all reach one Gmail inbox, so they share its cap.
func TestLimiter_GmailVariantsShareTheRecipientCap(t *testing.T) {
	now := time.Date(2026, 10, 13, 10, 0, 0, 0, time.UTC)
	l := newConfirmLimiter(func() time.Time { return now }, newFakeCounterStore(), defaultLimits())
	variants := []string{"victim@gmail.com", "v.ictim+a@gmail.com", "VICTIM+b@googlemail.com", "vi.c.tim@gmail.com", "victim+zzz@gmail.com"}
	allowed := 0
	for _, v := range variants {
		if l.allow(context.Background(), v) {
			allowed++
		}
	}
	if allowed != 3 {
		t.Fatalf("allowed %d of %d variants of one inbox, want 3", allowed, len(variants))
	}
}

// F-3.3: the counts live in the store, so a new limiter (a restart, or another replica) keeps them.
func TestLimiter_ANewLimiterOverTheSameStoreKeepsTheCounts(t *testing.T) {
	now := time.Date(2026, 10, 13, 10, 0, 0, 0, time.UTC)
	store := newFakeCounterStore()
	clock := func() time.Time { return now }
	first := newConfirmLimiter(clock, store, defaultLimits())
	for i := 0; i < 3; i++ {
		if !first.allow(context.Background(), "subscriber@example.com") {
			t.Fatalf("send %d refused", i+1)
		}
	}
	restarted := newConfirmLimiter(clock, store, defaultLimits())
	if restarted.allow(context.Background(), "subscriber+again@example.com") {
		t.Fatal("a restarted limiter admitted a fourth mail to the same mailbox within 24 h")
	}
	// C5: the window slides — the previous day still weighs on the next one, and only fades out.
	now = time.Date(2026, 10, 14, 0, 0, 1, 0, time.UTC)
	if newConfirmLimiter(clock, store, defaultLimits()).allow(context.Background(), "subscriber@example.com") {
		t.Fatal("the next fixed window admitted a mail at once (fixed-window edge)")
	}
	now = time.Date(2026, 10, 15, 0, 0, 1, 0, time.UTC)
	if !newConfirmLimiter(clock, store, defaultLimits()).allow(context.Background(), "subscriber@example.com") {
		t.Fatal("two windows later the mailbox is still refused")
	}
}

// C7: the per-domain threshold is an alarm only: above it the relay warns once per window and still
// sends (the mailbox and global caps are what refuse).
func TestLimiter_DomainThresholdWarnsButAllows(t *testing.T) {
	now := time.Date(2026, 10, 13, 10, 0, 0, 0, time.UTC)
	limits := defaultLimits()
	limits.domainPerHour = 4
	var buf bytes.Buffer
	l := newConfirmLimiter(func() time.Time { return now }, newFakeCounterStore(), limits)
	l.logger = slog.New(slog.NewTextHandler(&buf, nil))
	for i := 0; i < 10; i++ {
		if !l.allow(context.Background(), fmt.Sprintf("u%d@corp.example", i)) {
			t.Fatalf("send %d refused by the domain alarm", i+1)
		}
	}
	out := buf.String()
	if n := strings.Count(out, "level=WARN"); n != 1 || !strings.Contains(out, "domain") {
		t.Fatalf("%d warnings, want 1 domain alarm: %q", n, out)
	}
	if strings.Contains(out, "@") || strings.Contains(out, "corp.example") {
		t.Fatalf("the alarm names the address or domain: %q", out)
	}
}

// C5 / N-06: refused attempts increment nothing — not the mailbox, the domain or the global bucket.
func TestLimiter_RefusedAttemptsDoNotConsume(t *testing.T) {
	now := time.Date(2026, 10, 13, 10, 0, 0, 0, time.UTC)
	store := newFakeCounterStore()
	clock := func() time.Time { return now }
	for i := 0; i < 13; i++ {
		newConfirmLimiter(clock, store, defaultLimits()).allow(context.Background(), "victim@gmail.com")
	}
	rcpt := store.count("relay:rcpt:"+sha256Hex("victim@gmail.com"), windowStart(now, recipientWindow))
	dom := store.count("relay:domain:gmail.com", windowStart(now, domainWindow))
	glob := store.count("relay:global", windowStart(now, globalWindow))
	if rcpt != 3 || dom != 3 || glob != 3 {
		t.Fatalf("counts rcpt %d domain %d global %d, want 3 each (10 refused attempts must not count)", rcpt, dom, glob)
	}
}

// C5 / N-06: N sends just before a window edge and N just after admit about N, not 2N.
func TestLimiter_SlidingWindowAcrossTheEdge(t *testing.T) {
	const n = 10
	store := newFakeCounterStore()
	limits := defaultLimits()
	limits.globalPerHour = n
	now := time.Date(2026, 10, 13, 10, 59, 59, 0, time.UTC)
	clock := func() time.Time { return now }
	sendBurst := func(tag string) int {
		allowed := 0
		for i := 0; i < n; i++ {
			// A fresh limiter per send: the store alone decides (another replica's view).
			if newConfirmLimiter(clock, store, limits).allow(context.Background(), fmt.Sprintf("%s%d@d%d.example", tag, i, i)) {
				allowed++
			}
		}
		return allowed
	}
	if got := sendBurst("a"); got != n {
		t.Fatalf("first burst admitted %d, want %d", got, n)
	}
	now = now.Add(2 * time.Second) // 11:00:01, a new fixed window
	if got := sendBurst("b"); got != 0 {
		t.Fatalf("right after the edge %d more were admitted, want 0 (sliding estimate)", got)
	}
	now = time.Date(2026, 10, 13, 11, 30, 0, 0, time.UTC) // the previous window weighs 0.5
	if got := sendBurst("c"); got != n/2 {
		t.Fatalf("half way through the window %d were admitted, want %d", got, n/2)
	}
}

// The in-memory layer stays consistent with the store: a send the store refuses is not recorded in
// memory either, so it cannot lock the mailbox out later.
func TestLimiter_InMemoryLayerDoesNotRecordStoreRefusals(t *testing.T) {
	now := time.Date(2026, 10, 13, 10, 0, 0, 0, time.UTC)
	store := newFakeCounterStore()
	limits := defaultLimits()
	limits.globalPerHour = 2
	clock := func() time.Time { return now }
	// Another replica fills the global bucket.
	other := newConfirmLimiter(clock, store, limits)
	for i := 0; i < 2; i++ {
		if !other.allow(context.Background(), fmt.Sprintf("x%d@x%d.example", i, i)) {
			t.Fatal("setup send refused")
		}
	}
	l := newConfirmLimiter(clock, store, limits)
	for i := 0; i < 5; i++ {
		if l.allow(context.Background(), "victim@example.com") {
			t.Fatal("the store's global cap did not refuse")
		}
	}
	now = now.Add(2 * time.Hour) // the store's windows have rolled over; the mailbox has sent nothing
	for i := 0; i < 2; i++ {
		if !l.allow(context.Background(), "victim@example.com") {
			t.Fatalf("send %d refused: the in-memory layer counted the refused attempts", i+1)
		}
	}
}

// F-3.4: the global cap is configurable and counted in the store.
func TestLimiter_CapsMailsGloballyFromTheStore(t *testing.T) {
	now := time.Date(2026, 10, 13, 10, 0, 0, 0, time.UTC)
	store := newFakeCounterStore()
	limits := defaultLimits()
	limits.globalPerHour = 5
	clock := func() time.Time { return now }
	for i := 0; i < 5; i++ {
		// A fresh limiter per send: only the store can be what enforces the cap.
		if !newConfirmLimiter(clock, store, limits).allow(context.Background(), fmt.Sprintf("u%d@d%d.example", i, i)) {
			t.Fatalf("send %d refused", i+1)
		}
	}
	if newConfirmLimiter(clock, store, limits).allow(context.Background(), "late@late.example") {
		t.Fatal("the global cap admitted a sixth mail in the hour")
	}
}

// Bucket keys never carry a raw address: the recipient is a sha256 of the canonical mailbox.
func TestLimiter_BucketsCarryNoRawAddress(t *testing.T) {
	now := time.Date(2026, 10, 13, 10, 17, 3, 0, time.UTC)
	store := newFakeCounterStore()
	l := newConfirmLimiter(func() time.Time { return now }, store, defaultLimits())
	if !l.allow(context.Background(), "Victim+x@GoogleMail.com") {
		t.Fatal("refused")
	}
	want := map[string]bool{
		"relay:rcpt:" + sha256Hex("victim@gmail.com") + "|2026-10-13T00:00:00Z": true,
		"relay:domain:gmail.com|2026-10-13T10:00:00Z":                           true,
		"relay:global|2026-10-13T10:00:00Z":                                     true,
	}
	got := store.buckets()
	if len(got) != len(want) {
		t.Fatalf("buckets %v", got)
	}
	for _, b := range got {
		if !want[b] {
			t.Fatalf("unexpected bucket %q (all: %v)", b, got)
		}
		if strings.Contains(b, "victim") {
			t.Fatalf("bucket %q carries the address", b)
		}
	}
}

// Fail closed: a store error (including a missing table) refuses the send and logs no address.
func TestLimiter_FailsClosedOnAStoreError(t *testing.T) {
	now := time.Date(2026, 10, 13, 10, 0, 0, 0, time.UTC)
	store := newFakeCounterStore()
	store.err = errors.New(`ERROR: relation "mail_rate_counters" does not exist (SQLSTATE 42P01)`)
	var buf bytes.Buffer
	l := newConfirmLimiter(func() time.Time { return now }, store, defaultLimits())
	l.logger = slog.New(slog.NewTextHandler(&buf, nil))
	if l.allow(context.Background(), "subscriber@example.com") {
		t.Fatal("a store error admitted the send")
	}
	out := buf.String()
	if !strings.Contains(out, "level=ERROR") || !strings.Contains(out, "mail_rate_counters") {
		t.Fatalf("store failure not logged: %q", out)
	}
	if strings.Contains(out, "subscriber") {
		t.Fatalf("the log carries the address: %q", out)
	}
}

// F-3.4 / F-1.1c: hitting the global cap warns once per window, not once per refused request.
func TestLimiter_GlobalCapWarnsOncePerWindow(t *testing.T) {
	now := time.Date(2026, 10, 13, 10, 0, 0, 0, time.UTC)
	limits := defaultLimits()
	limits.globalPerHour = 2
	var buf bytes.Buffer
	l := newConfirmLimiter(func() time.Time { return now }, newFakeCounterStore(), limits)
	l.logger = slog.New(slog.NewTextHandler(&buf, nil))
	for i := 0; i < 10; i++ {
		l.allow(context.Background(), fmt.Sprintf("u%d@d%d.example", i, i))
	}
	if n := strings.Count(buf.String(), "level=WARN"); n != 1 {
		t.Fatalf("%d warnings in one window, want 1: %q", n, buf.String())
	}
	now = now.Add(time.Hour)
	for i := 0; i < 10; i++ {
		l.allow(context.Background(), fmt.Sprintf("v%d@e%d.example", i, i))
	}
	if n := strings.Count(buf.String(), "level=WARN"); n != 2 {
		t.Fatalf("%d warnings over two windows, want 2: %q", n, buf.String())
	}
	if strings.Contains(buf.String(), "@") {
		t.Fatalf("the warning carries an address: %q", buf.String())
	}
}
