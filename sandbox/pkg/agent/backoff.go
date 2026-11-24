package agent

import (
	"encoding/binary"
	"io"
	"time"
)

// Backoff is the reconnect schedule: an exponential delay with full jitter.
//
// Full jitter (a uniform draw from [0, exponential]) rather than a fixed
// exponential is what stops a fleet of workers whose gateway redeployed from
// stampeding it together on the same tick.
type Backoff struct {
	// Base is the first delay's ceiling. Zero means 1s.
	Base time.Duration
	// Max is the ceiling of the exponential. Zero means 60s.
	Max time.Duration
	// ResetAfter is how long a connection must last before the attempt counter
	// is forgiven. A gateway that accepts and immediately drops connections
	// would otherwise look like a healthy one and be hammered. Zero means 60s.
	ResetAfter time.Duration
}

// DefaultBackoff is the schedule an agent uses when Config.Backoff is zero.
func DefaultBackoff() Backoff {
	return Backoff{Base: time.Second, Max: 60 * time.Second, ResetAfter: 60 * time.Second}
}

func (b Backoff) isZero() bool { return b == Backoff{} }

func (b Backoff) normalize() Backoff {
	if b.Base <= 0 {
		b.Base = time.Second
	}
	if b.Max <= 0 {
		b.Max = 60 * time.Second
	}
	if b.Max < b.Base {
		b.Max = b.Base
	}
	if b.ResetAfter <= 0 {
		b.ResetAfter = 60 * time.Second
	}
	return b
}

// Delay returns the wait before attempt (zero-based). Jitter is drawn from r so
// the schedule is reproducible in tests.
func (b Backoff) Delay(attempt int, r io.Reader) time.Duration {
	b = b.normalize()
	ceiling := b.Base
	for i := 0; i < attempt && ceiling < b.Max; i++ {
		ceiling *= 2
	}
	if ceiling > b.Max {
		ceiling = b.Max
	}
	if ceiling <= 0 {
		return 0
	}
	// A uniform draw from [ceiling/2, ceiling]: some jitter, but never a
	// near-zero retry that looks like a spin.
	const floorRatio = 0.5
	floor := time.Duration(float64(ceiling) * floorRatio)
	span := ceiling - floor
	if span <= 0 {
		return ceiling
	}
	return floor + time.Duration(randFraction(r)*float64(span))
}

func randFraction(r io.Reader) float64 {
	var buf [8]byte
	if _, err := io.ReadFull(r, buf[:]); err != nil {
		return 0.5
	}
	const max53 = float64(1 << 53)
	return float64(binary.BigEndian.Uint64(buf[:])>>11) / max53
}
