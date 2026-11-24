package main

import (
	"sync"
	"time"
)

// maxTrackedNonces bounds the replay cache. It is far above the number of
// distinct (device, nonce) pairs a legitimate fleet presents inside the TTL
// window (240 s), so reaching it means the cache is under a flood. At that point
// the gateway refuses further handshakes (4429) rather than evicting entries,
// because an evicted entry is exactly what a replay needs.
const maxTrackedNonces = 1 << 16

// nonceCache remembers (deviceId, nonce) pairs so a captured hello cannot be
// replayed. The specification requires a cache of at least 240 s; expired
// entries are swept opportunistically on insert, so no goroutine is needed.
type nonceCache struct {
	ttl time.Duration

	mu        sync.Mutex
	seen      map[string]time.Time
	nextSweep time.Time
}

func newNonceCache(ttl time.Duration) *nonceCache {
	return &nonceCache{ttl: ttl, seen: map[string]time.Time{}}
}

// claim records the pair and reports whether it is new. A false return with
// saturated unset is a replay and the connection is refused with 4401;
// saturated means the cache is full of live entries and the handshake is
// refused with 4429.
func (n *nonceCache) claim(deviceID, nonce string, now time.Time) (ok bool, saturated bool) {
	// A device id is a uuid and a nonce is base64url, so neither can contain the
	// separator and the pair cannot collide.
	key := deviceID + "|" + nonce
	n.mu.Lock()
	defer n.mu.Unlock()

	if !now.Before(n.nextSweep) {
		n.sweep(now)
		n.nextSweep = now.Add(n.ttl / 4)
	}
	if _, exists := n.seen[key]; exists {
		return false, false
	}
	if len(n.seen) >= maxTrackedNonces {
		// Sweep once more with the current time before giving up: the map is only
		// genuinely full of live entries if the flood outpaces the TTL.
		n.sweep(now)
		if len(n.seen) >= maxTrackedNonces {
			return false, true
		}
	}
	n.seen[key] = now
	return true, false
}

// size reports the number of tracked pairs (tests and metrics).
func (n *nonceCache) size() int {
	n.mu.Lock()
	defer n.mu.Unlock()
	return len(n.seen)
}

func (n *nonceCache) sweep(now time.Time) {
	for key, at := range n.seen {
		if now.Sub(at) >= n.ttl {
			delete(n.seen, key)
		}
	}
}
