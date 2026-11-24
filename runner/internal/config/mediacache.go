package config

import (
	"fmt"
	"path/filepath"
)

// The downloaded-media cache's limits. The cache itself is
// sandbox/pkg/agent/srccache; these are the knobs runner.json holds for it.
const (
	// DefaultMediaCacheBytes is the cap when runner.json says nothing: enough
	// for a project's worth of 1080p sources without becoming the largest thing
	// in a laptop's cache directory.
	DefaultMediaCacheBytes int64 = 10 << 30
	// MinMediaCacheBytes is the smallest cap Validate accepts, other than 0
	// (off). Below it a single source would not fit, and the cache would only
	// churn.
	MinMediaCacheBytes int64 = 256 << 20
	// MediaCacheDirName is the cache's directory under CacheDir().
	MediaCacheDirName = "media"
)

// MediaCache is runner.json's "mediaCache" block.
//
// MaxBytes is a pointer so that "absent" and "0" are different answers: absent
// is the default (DefaultMediaCacheBytes), and 0 turns the cache off. Every
// other value must be at least MinMediaCacheBytes.
type MediaCache struct {
	MaxBytes *int64 `json:"maxBytes,omitempty"`
}

// EffectiveMaxBytes is the cap in force: the configured one, or the default.
// Zero means the cache is off.
func (m MediaCache) EffectiveMaxBytes() int64 {
	if m.MaxBytes == nil {
		return DefaultMediaCacheBytes
	}
	return *m.MaxBytes
}

// Enabled reports whether the runner keeps downloaded media at all.
func (m MediaCache) Enabled() bool { return m.EffectiveMaxBytes() > 0 }

// SetMaxBytes records a cap; 0 turns the cache off. It does not validate: call
// Config.Validate before saving, as for every other field.
func (m *MediaCache) SetMaxBytes(n int64) { m.MaxBytes = &n }

// Validate refuses a cap the cache cannot honour.
func (m MediaCache) Validate() error {
	if m.MaxBytes == nil {
		return nil
	}
	n := *m.MaxBytes
	if n < 0 {
		return fmt.Errorf("maxBytes must not be negative, got %d", n)
	}
	if n != 0 && n < MinMediaCacheBytes {
		return fmt.Errorf("maxBytes must be 0 (off) or at least %d (256 MiB), got %d", MinMediaCacheBytes, n)
	}
	return nil
}

// MediaCacheDir returns <UserCacheDir>/ReelBolt/media, where the downloaded-media
// cache lives.
func MediaCacheDir() (string, error) {
	cache, err := CacheDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(cache, MediaCacheDirName), nil
}
