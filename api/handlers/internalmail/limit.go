package internalmail

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"log/slog"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/gorm"
)

// The relay's own caps on the marketing double opt-in mail (S-07, F-3). It is the one template mailed
// to addresses with no account, so a coerced caller must not be able to turn it into a mail cannon at
// one person, one domain or the world. The caller (the Inference API) has its own caps; these sit
// behind them and hold even if those are bypassed.
const (
	confirmPerRecipientPerDay = 3

	// Defaults for the env-configurable caps.
	defaultRelayPerDomainPerHour = 30
	defaultRelayGlobalPerHour    = 100

	envRelayPerDomainPerHour = "MARKETING_RELAY_PER_DOMAIN_PER_HOUR"
	envRelayGlobalPerHour    = "MARKETING_RELAY_GLOBAL_PER_HOUR"

	recipientWindow = 24 * time.Hour
	globalWindow    = time.Hour
	domainWindow    = time.Hour

	// storeTimeout bounds one limiter decision's counter round trips, so a hung database refuses
	// rather than stalls.
	storeTimeout = 3 * time.Second
)

// CanonicalMailbox is shared contract C1 (identical in the C# API); see services.CanonicalMailbox.
func CanonicalMailbox(raw string) string {
	return services.CanonicalMailbox(raw)
}

func sha256Hex(s string) string {
	h := sha256.Sum256([]byte(s))
	return hex.EncodeToString(h[:])
}

// relayLimits are the caps the limiter enforces.
type relayLimits struct {
	recipientPerDay int
	domainPerHour   int
	globalPerHour   int
}

// relayLimitsFromEnv reads the two configurable caps; unset, non-numeric or non-positive values fall
// back to the defaults (a typo must never lift a cap).
func relayLimitsFromEnv(getenv func(string) string) relayLimits {
	return relayLimits{
		recipientPerDay: confirmPerRecipientPerDay,
		domainPerHour:   positiveIntOr(getenv(envRelayPerDomainPerHour), defaultRelayPerDomainPerHour),
		globalPerHour:   positiveIntOr(getenv(envRelayGlobalPerHour), defaultRelayGlobalPerHour),
	}
}

func positiveIntOr(raw string, def int) int {
	n, err := strconv.Atoi(strings.TrimSpace(raw))
	if err != nil || n <= 0 {
		return def
	}
	return n
}

// bucketWindow names one counter of a decision: the bucket, the start of its current fixed window and
// the window length (the previous window starts one length earlier).
type bucketWindow struct {
	bucket string
	start  time.Time
	length time.Duration
}

// windowCounts are a bucket's counts in its current and previous windows.
type windowCounts struct {
	current, previous int
}

// slidingEstimate is contract C5: count(current window) + count(previous window) x (1 - elapsed
// fraction of the current window).
func slidingEstimate(c windowCounts, b bucketWindow, now time.Time) float64 {
	elapsed := float64(now.Sub(b.start)) / float64(b.length)
	if elapsed < 0 {
		elapsed = 0
	}
	if elapsed > 1 {
		elapsed = 1
	}
	return float64(c.current) + float64(c.previous)*(1-elapsed)
}

// counterStore is the persistent, shared counter (contracts C2, C5). Decide reads every bucket's
// current and previous window counts, asks decide, and increments every bucket's current window only
// when decide admits — all atomically, so a refused attempt increments nothing and concurrent
// decisions on one bucket cannot both see room for one more.
type counterStore interface {
	Decide(ctx context.Context, buckets []bucketWindow, decide func([]windowCounts) bool) (bool, error)
}

// incrementSQL is the C2 upsert. The Inference API owns the mail_rate_counters schema; the Go API
// never migrates it.
const incrementSQL = `INSERT INTO mail_rate_counters (bucket, window_start, count, updated_at) VALUES ($1, $2, 1, now()) ` +
	`ON CONFLICT (bucket, window_start) DO UPDATE SET count = mail_rate_counters.count + 1, updated_at = now()`

// sqlCounterStore runs C5 over mail_rate_counters through the API's database handle, resolved per
// call because database.DB is set at startup, after package initialisation.
type sqlCounterStore struct {
	db func() *gorm.DB
}

// Decide holds pg_advisory_xact_lock(hashtext(bucket)) for every bucket, taken in ordinal-sorted
// bucket order (the C# side takes them in the same order, so the two never deadlock), reads, decides
// and increments in one transaction; the locks are released at commit.
func (s sqlCounterStore) Decide(ctx context.Context, buckets []bucketWindow, decide func([]windowCounts) bool) (bool, error) {
	db := s.db()
	if db == nil {
		return false, fmt.Errorf("mail_rate_counters: no database connection")
	}
	names := make([]string, 0, len(buckets))
	seen := map[string]bool{}
	for _, b := range buckets {
		if !seen[b.bucket] {
			seen[b.bucket] = true
			names = append(names, b.bucket)
		}
	}
	sort.Strings(names)

	admitted := false
	err := db.WithContext(ctx).Transaction(func(tx *gorm.DB) error {
		for _, name := range names {
			if err := tx.Exec(`SELECT pg_advisory_xact_lock(hashtext(?))`, name).Error; err != nil {
				return fmt.Errorf("mail_rate_counters lock: %w", err)
			}
		}
		counts := make([]windowCounts, len(buckets))
		for i, b := range buckets {
			var rows []struct {
				WindowStart time.Time
				Count       int
			}
			if err := tx.Raw(`SELECT window_start, count FROM mail_rate_counters WHERE bucket = ? AND window_start IN (?, ?)`,
				b.bucket, b.start.UTC(), b.start.Add(-b.length).UTC()).Scan(&rows).Error; err != nil {
				return fmt.Errorf("mail_rate_counters read: %w", err)
			}
			for _, r := range rows {
				if r.WindowStart.Equal(b.start) {
					counts[i].current = r.Count
				} else {
					counts[i].previous = r.Count
				}
			}
		}
		if !decide(counts) {
			return nil
		}
		for _, b := range buckets {
			if err := tx.Exec(incrementSQL, b.bucket, b.start.UTC()).Error; err != nil {
				return fmt.Errorf("mail_rate_counters increment: %w", err)
			}
		}
		admitted = true
		return nil
	})
	if err != nil {
		return false, err
	}
	return admitted, nil
}

// windowStart floors t (in UTC) to a fixed window of length w.
func windowStart(t time.Time, w time.Duration) time.Time {
	secs := int64(w / time.Second)
	u := t.UTC().Unix()
	return time.Unix(u-u%secs, 0).UTC()
}

// confirmLimiter enforces the caps in two layers, both of which must admit a send: an in-memory
// sliding log (per process) and the persistent sliding-window counters in mail_rate_counters (shared
// by replicas, kept across restarts). Contract C7: the mailbox cap (3 per 24 h) and the global cap
// (MARKETING_RELAY_GLOBAL_PER_HOUR) refuse; the per-domain threshold
// (MARKETING_RELAY_PER_DOMAIN_PER_HOUR) only raises an alarm, once per window.
//
// The in-memory layer's memory is bounded by its global cap: at most globalPerHour sends are admitted
// per hour, so at most 24x that many recipients hold entries inside the 24 h window, and expired
// entries are swept.
type confirmLimiter struct {
	now    func() time.Time
	store  counterStore
	limits relayLimits
	logger *slog.Logger

	mu        sync.Mutex
	global    []time.Time
	recipient map[string][]time.Time
	lastSweep time.Time
	// warnedWindow / warnedDomainWindow are the windows the global cap warning and the domain alarm
	// were last logged for: one line per window each.
	warnedWindow       time.Time
	warnedDomainWindow time.Time
}

func newConfirmLimiter(now func() time.Time, store counterStore, limits relayLimits) *confirmLimiter {
	return &confirmLimiter{
		now:       now,
		store:     store,
		limits:    limits,
		logger:    slog.Default(),
		recipient: map[string][]time.Time{},
	}
}

// prune drops the timestamps at or before cutoff from an ascending slice.
func prune(ts []time.Time, cutoff time.Time) []time.Time {
	i := 0
	for i < len(ts) && !ts[i].After(cutoff) {
		i++
	}
	return ts[i:]
}

// allow reports whether one marketing-confirm send to recipient is admitted by every cap, recording
// it. Any counter-store error refuses the send (fail closed). A refused send is recorded nowhere.
func (l *confirmLimiter) allow(ctx context.Context, recipient string) bool {
	canonical := CanonicalMailbox(recipient)
	now := l.now()
	if !l.reserveInMemory(canonical, now) {
		return false
	}
	if l.allowPersistent(ctx, canonical, now) {
		return true
	}
	l.releaseInMemory(canonical, now)
	return false
}

// reserveInMemory is the per-process layer: it records the send if both in-memory caps admit it. A
// refused send is not recorded, so a flood of refusals does not extend the window.
func (l *confirmLimiter) reserveInMemory(key string, now time.Time) bool {
	l.mu.Lock()
	defer l.mu.Unlock()

	if now.Sub(l.lastSweep) >= time.Minute {
		for k, ts := range l.recipient {
			if ts = prune(ts, now.Add(-recipientWindow)); len(ts) == 0 {
				delete(l.recipient, k)
			} else {
				l.recipient[k] = ts
			}
		}
		l.lastSweep = now
	}

	l.global = prune(l.global, now.Add(-globalWindow))
	sent := prune(l.recipient[key], now.Add(-recipientWindow))
	globalFull := len(l.global) >= l.limits.globalPerHour
	if globalFull {
		l.warnGlobalCap(windowStart(now, globalWindow))
	}
	if globalFull || len(sent) >= l.limits.recipientPerDay {
		if len(sent) == 0 {
			delete(l.recipient, key)
		} else {
			l.recipient[key] = sent
		}
		return false
	}
	l.global = append(l.global, now)
	l.recipient[key] = append(sent, now)
	return true
}

// releaseInMemory undoes a reservation the persistent layer refused, so the in-memory layer never
// counts a send that did not happen.
func (l *confirmLimiter) releaseInMemory(key string, at time.Time) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.global = removeLast(l.global, at)
	if ts := removeLast(l.recipient[key], at); len(ts) == 0 {
		delete(l.recipient, key)
	} else {
		l.recipient[key] = ts
	}
}

// removeLast drops the last occurrence of at from ts.
func removeLast(ts []time.Time, at time.Time) []time.Time {
	for i := len(ts) - 1; i >= 0; i-- {
		if ts[i].Equal(at) {
			return append(ts[:i], ts[i+1:]...)
		}
	}
	return ts
}

// allowPersistent decides the C2 buckets atomically under C5: the mailbox and global estimates must
// leave room for this send; the domain estimate only raises the alarm. Bucket names carry a hash of
// the mailbox, never the address.
func (l *confirmLimiter) allowPersistent(ctx context.Context, canonical string, now time.Time) bool {
	if l.store == nil {
		l.logger.Error("internal mail: no mail_rate_counters store configured; refusing marketing-confirm send")
		return false
	}
	domain := ""
	if at := strings.LastIndex(canonical, "@"); at >= 0 {
		domain = canonical[at+1:]
	}
	const rcpt, dom, glob = 0, 1, 2
	buckets := []bucketWindow{
		rcpt: {"relay:rcpt:" + sha256Hex(canonical), windowStart(now, recipientWindow), recipientWindow},
		dom:  {"relay:domain:" + domain, windowStart(now, domainWindow), domainWindow},
		glob: {"relay:global", windowStart(now, globalWindow), globalWindow},
	}
	globalFull, domainOver := false, false
	decide := func(c []windowCounts) bool {
		fits := func(i, limit int) bool { return slidingEstimate(c[i], buckets[i], now)+1 <= float64(limit) }
		globalFull = !fits(glob, l.limits.globalPerHour)
		domainOver = !fits(dom, l.limits.domainPerHour)
		return fits(rcpt, l.limits.recipientPerDay) && !globalFull
	}

	ctx, cancel := context.WithTimeout(ctx, storeTimeout)
	defer cancel()
	admitted, err := l.store.Decide(ctx, buckets, decide)
	if err != nil {
		l.logger.Error("internal mail: mail_rate_counters unavailable; refusing marketing-confirm send (fail closed)",
			"err", services.RedactAddresses(err.Error()))
		return false
	}
	l.mu.Lock()
	if globalFull {
		l.warnGlobalCap(buckets[glob].start)
	}
	if admitted && domainOver {
		l.warnDomainAlarm(buckets[dom].start)
	}
	l.mu.Unlock()
	return admitted
}

// warnGlobalCap logs the global cap being reached once per window. The caller holds l.mu.
func (l *confirmLimiter) warnGlobalCap(window time.Time) {
	if l.warnedWindow.Equal(window) {
		return
	}
	l.warnedWindow = window
	l.logger.Warn("internal mail: marketing-confirm global relay cap reached; further sends refused this window",
		"cap_per_hour", l.limits.globalPerHour, "window_start", window.Format(time.RFC3339))
}

// warnDomainAlarm logs, once per window, that one recipient domain passed the per-domain alarm
// threshold. Sends continue (C7); the line names neither the domain nor any address. The caller holds
// l.mu.
func (l *confirmLimiter) warnDomainAlarm(window time.Time) {
	if l.warnedDomainWindow.Equal(window) {
		return
	}
	l.warnedDomainWindow = window
	l.logger.Warn("internal mail: marketing-confirm per-domain alarm threshold passed; sends continue",
		"threshold_per_hour", l.limits.domainPerHour, "window_start", window.Format(time.RFC3339))
}
