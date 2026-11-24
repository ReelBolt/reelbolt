package ui

import (
	"fmt"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent/srccache"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
)

// The downloaded-media cache, as the window sees it.
//
// The cache itself is sandbox/pkg/agent/srccache, and srccache.Open returns ONE
// instance per directory per process: the shell and the daemon it hosts look at
// the same counters and the same files, so "Clear" here and a job reading a
// cached source there cannot disagree. The cap lives in runner.json
// (mediaCache.maxBytes); zero turns the cache off.

// mediaCacheDir is where the cache lives: the option a test sets, or the
// runner's usual cache directory.
func (s *Shell) mediaCacheDir() string {
	if s.opts.MediaCacheDir != "" {
		return s.opts.MediaCacheDir
	}
	dir, err := config.MediaCacheDir()
	if err != nil {
		return ""
	}
	return dir
}

// mediaCache returns the cache, opening it on first use, or nil when it is
// turned off or cannot be opened (which the log says once).
func (s *Shell) mediaCache() *srccache.Cache {
	max := s.config().MediaCache.EffectiveMaxBytes()
	if max <= 0 {
		return nil
	}
	if d := s.currentDaemon(); d != nil {
		if c := d.MediaCache(); c != nil {
			return c
		}
	}
	s.mu.Lock()
	c, failed := s.media, s.mediaFailed
	s.mu.Unlock()
	if c != nil || failed {
		return c
	}
	dir := s.mediaCacheDir()
	if dir == "" {
		return nil
	}
	c, err := srccache.Open(dir, max)
	s.mu.Lock()
	defer s.mu.Unlock()
	if err != nil {
		s.mediaFailed = true
		s.log.Printf("media cache: %v", err)
		return nil
	}
	s.media = c
	return c
}

// mediaCacheView is the cache's figures, or nil when it is off.
func (s *Shell) mediaCacheView() *MediaCacheView {
	c := s.mediaCache()
	if c == nil {
		return nil
	}
	st := c.Stats()
	return &MediaCacheView{
		Root: st.Root, Bytes: st.Bytes, Entries: st.Entries, MaxBytes: st.MaxBytes,
		Hits: st.Hits, Misses: st.Misses, SavedBytes: st.SavedBytes,
	}
}

// ClearMediaCache deletes every cached source and reports what it freed. A file
// a job is reading right now is removed when the job lets go of it.
func (s *Shell) ClearMediaCache() (int64, error) {
	c := s.mediaCache()
	if c == nil {
		return 0, say("The media cache is turned off, so there is nothing to clear.")
	}
	freed, err := c.Clear()
	s.notify()
	if err != nil {
		s.log.Printf("media cache: clear: %v", err)
		return freed, sayWrap(fmt.Sprintf("Freed %s, but some files couldn't be removed. Try again in a moment.", humanBytes(freed)), err)
	}
	return freed, nil
}

// applyMediaCacheMax hands a new cap to the live cache. It reports whether the
// runner has to restart to start using a cache it was started without.
func (s *Shell) applyMediaCacheMax(before, after int64) (restart bool) {
	if after <= 0 {
		s.mu.Lock()
		c := s.media
		s.mu.Unlock()
		if d := s.currentDaemon(); d != nil && d.MediaCache() != nil {
			c = d.MediaCache()
		}
		if c != nil {
			c.SetMaxBytes(0)
		}
		return false
	}
	if c := s.mediaCache(); c != nil {
		c.SetMaxBytes(after)
	}
	return before <= 0
}
