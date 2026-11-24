package poolwaker

import (
	"fmt"
	"strings"
	"time"
)

// Clock is the loop's time source (tests inject a fake one).
type Clock interface{ Now() time.Time }

// RealClock is the wall clock.
type RealClock struct{}

// Now implements Clock.
func (RealClock) Now() time.Time { return time.Now() }

// Config holds every rule of the controller. Zero values are replaced by
// defaults in Normalize.
type Config struct {
	Instance string
	// ServerType is the preferred size. ServerTypes is the ordered list the waker tries (first
	// usable one wins); when it is empty it is just [ServerType]. After Normalize the two agree:
	// ServerType == ServerTypes[0].
	ServerType   string
	ServerTypes  []string
	MaxNodes     int // hard cap, default 2
	SlotsPerNode int // informational (demand translation is the caller's), default 1
	// IdleWindow ends an idle node earlier than its paid hour does. Zero (the default) turns it off:
	// a node then stays connected until HourEndMargin before the end of its paid hour.
	IdleWindow time.Duration
	// HourEndMargin is how long before the end of its paid hour an idle node is deleted, default 5 min.
	HourEndMargin   time.Duration
	Cooldown        time.Duration // default 60 s
	BootDeadline    time.Duration // default 300 s
	MaxLifetime     time.Duration // default 6 h
	DeadGrace       time.Duration // default 120 s
	TokenTTL        time.Duration // default 4 h (must outlive MaxLifetime's boot window, not the node)
	DailyCapEUR     float64       // default 5
	AlertFraction   float64       // default 0.8
	BreakerFailures int           // consecutive boot failures that open the breaker, default 3
	BreakerBackoff  time.Duration // default 15 min
	ProviderErrors  int           // consecutive provider errors that make the waker unhealthy, default 3
	NodeGatewayURL  string        // the wss URL nodes dial
	CPULimit        string        // SANDBOX_CPU_LIMIT for the node, default "all" (every core, one job at a time)
	// SizeRefusalBackoff is how long a size the provider refused (core limit, out of stock) is left
	// alone before it is tried again, default 1 h. It is what stops a refused size from being retried
	// every loop iteration.
	SizeRefusalBackoff time.Duration
	HourLookahead      time.Duration // reserve the next billed hour this long before it starts, default 90 s
	HourHardStop       time.Duration // a busy node that cannot afford the next hour is deleted this long before it, default 30 s
	DryRun             bool

	// createLagGrace is how long a created node may be missing from List.
	createLagGrace time.Duration
}

// Normalize fills defaults and validates.
func (c *Config) Normalize() error {
	def := func(d *time.Duration, v time.Duration) {
		if *d <= 0 {
			*d = v
		}
	}
	if c.Instance == "" {
		c.Instance = "default"
	}
	for _, r := range c.Instance {
		if (r < 'a' || r > 'z') && (r < '0' || r > '9') && r != '-' {
			return fmt.Errorf("instance %q must use only [a-z0-9-]", c.Instance)
		}
	}
	if c.MaxNodes <= 0 {
		c.MaxNodes = 2
	}
	if c.SlotsPerNode <= 0 {
		c.SlotsPerNode = 1
	}
	if c.IdleWindow < 0 {
		c.IdleWindow = 0
	}
	def(&c.HourEndMargin, 5*time.Minute)
	if c.HourEndMargin >= time.Hour {
		return fmt.Errorf("hour end margin %s must be shorter than one hour", c.HourEndMargin)
	}
	def(&c.Cooldown, 60*time.Second)
	def(&c.BootDeadline, 300*time.Second)
	def(&c.MaxLifetime, 6*time.Hour)
	def(&c.DeadGrace, 120*time.Second)
	def(&c.TokenTTL, 4*time.Hour)
	def(&c.BreakerBackoff, 15*time.Minute)
	def(&c.HourLookahead, 90*time.Second)
	def(&c.HourHardStop, 30*time.Second)
	def(&c.createLagGrace, 2*time.Minute)
	if c.DailyCapEUR <= 0 {
		c.DailyCapEUR = 5
	}
	if c.AlertFraction <= 0 || c.AlertFraction > 1 {
		c.AlertFraction = 0.8
	}
	if c.BreakerFailures <= 0 {
		c.BreakerFailures = 3
	}
	if c.ProviderErrors <= 0 {
		c.ProviderErrors = 3
	}
	if c.CPULimit == "" {
		c.CPULimit = "all"
	}
	def(&c.SizeRefusalBackoff, time.Hour)
	if len(c.ServerTypes) == 0 && c.ServerType != "" {
		c.ServerTypes = []string{c.ServerType}
	}
	seen := map[string]bool{}
	var types []string
	for _, t := range c.ServerTypes {
		t = strings.TrimSpace(t)
		if t == "" || seen[t] {
			continue
		}
		seen[t] = true
		types = append(types, t)
	}
	if len(types) == 0 {
		return fmt.Errorf("server type is required")
	}
	c.ServerTypes, c.ServerType = types, types[0]
	return nil
}
