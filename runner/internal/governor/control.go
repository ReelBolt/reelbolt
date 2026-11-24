package governor

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"
)

// Control is the runner's control channel: two small JSON documents in the
// runner's cache directory.
//
//   - the REQUEST document is written by whoever wants the running runner
//     paused or resumed ("reelbolt-runner pause") and consumed by the daemon;
//   - the LIVE document is written by the daemon and read by "status" and by
//     the desktop shell.
//
// It is files rather than a socket because the runner listens on no port (an E6
// acceptance criterion) and because a file behaves the same on Windows and
// Linux. The cost is polling, which is why the daemon consumes a request AT
// MOST ONCE and deletes it before parsing it: a corrupt or half-written request
// is therefore never re-read, never retried and never sticky.
type Control struct {
	// RequestPath is where a pause/resume request is written.
	RequestPath string
	// LivePath is where the daemon publishes its state.
	LivePath string
	// Now is the clock. Nil means time.Now.
	Now func() time.Time
}

// Request is the control document.
type Request struct {
	// Action is "pause" or "resume".
	Action string `json:"action"`
	// By records who asked.
	By string `json:"by,omitempty"`
	// Reason is shown to the user.
	Reason string `json:"reason,omitempty"`
	// At is when the request was written, RFC3339.
	At string `json:"at,omitempty"`
}

// Control actions.
const (
	ActionPause  = "pause"
	ActionResume = "resume"
)

// LiveHeartbeatGrace is how old the live document may be before a command
// treats the runner as not running. Three missed beats: long enough to survive
// a slow disk, short enough that "pause" never silently does nothing.
const LiveHeartbeatGrace = 45 * time.Second

// Live is the document the daemon publishes.
type Live struct {
	PID         int    `json:"pid"`
	StartedAt   string `json:"startedAt"`
	HeartbeatAt string `json:"heartbeatAt"`
	Status      Status `json:"status"`
}

// Fresh reports whether the daemon that wrote this document is still beating.
func (l Live) Fresh(now time.Time) bool {
	if l.HeartbeatAt == "" {
		return false
	}
	t, err := time.Parse(time.RFC3339, l.HeartbeatAt)
	if err != nil {
		return false
	}
	return now.Sub(t) <= LiveHeartbeatGrace
}

func (c *Control) now() time.Time {
	if c != nil && c.Now != nil {
		return c.Now()
	}
	return time.Now()
}

// Enabled reports whether this control channel has somewhere to write.
func (c *Control) Enabled() bool { return c != nil && c.RequestPath != "" }

// Request writes a pause or resume request for the running daemon.
func (c *Control) Request(action, by, reason string) error {
	if !c.Enabled() {
		return errors.New("governor: this runner has no control channel")
	}
	switch action {
	case ActionPause, ActionResume:
	default:
		return fmt.Errorf("governor: %q is not %s or %s", action, ActionPause, ActionResume)
	}
	raw, err := json.MarshalIndent(Request{Action: action, By: by, Reason: reason, At: c.now().Format(time.RFC3339)}, "", "  ")
	if err != nil {
		return err
	}
	return writeAtomic(c.RequestPath, append(raw, '\n'))
}

// Take consumes a request.
//
// The file is deleted BEFORE it is parsed, so a request is applied at most once
// and an unreadable one cannot be retried forever. A caller that gets an error
// has already had the file removed and must carry on as if nothing was asked:
// the daemon logs it and keeps running.
func (c *Control) Take() (Request, bool, error) {
	if !c.Enabled() {
		return Request{}, false, nil
	}
	raw, err := os.ReadFile(c.RequestPath)
	if errors.Is(err, os.ErrNotExist) {
		return Request{}, false, nil
	}
	if err != nil {
		_ = os.Remove(c.RequestPath)
		return Request{}, false, fmt.Errorf("governor: read %s: %w", c.RequestPath, err)
	}
	if err := os.Remove(c.RequestPath); err != nil {
		return Request{}, false, fmt.Errorf("governor: consume %s: %w", c.RequestPath, err)
	}
	var r Request
	if err := json.Unmarshal(raw, &r); err != nil {
		return Request{}, false, fmt.Errorf("governor: %s is not a valid request: %w", c.RequestPath, err)
	}
	switch r.Action {
	case ActionPause, ActionResume:
	default:
		return Request{}, false, fmt.Errorf("governor: %s asks for unknown action %q", c.RequestPath, r.Action)
	}
	return r, true, nil
}

// Clear deletes any request left behind by an earlier process.
//
// It is called at startup, and it is the reason a stale "pause" can never take
// effect on a fresh run: the request lives on disk only for the moment between
// a command writing it and the daemon that is already running reading it.
func (c *Control) Clear() error {
	if !c.Enabled() {
		return nil
	}
	if err := os.Remove(c.RequestPath); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}

// Publish writes the live document.
func (c *Control) Publish(l Live) error {
	if !c.Enabled() || c.LivePath == "" {
		return nil
	}
	raw, err := json.MarshalIndent(l, "", "  ")
	if err != nil {
		return err
	}
	return writeAtomic(c.LivePath, append(raw, '\n'))
}

// RemoveLive deletes the live document, so a command run after the daemon
// stopped reports "the runner is not running" rather than acting on a stale
// answer.
func (c *Control) RemoveLive() error {
	if !c.Enabled() || c.LivePath == "" {
		return nil
	}
	if err := os.Remove(c.LivePath); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}

// ReadLive reads the live document.
func (c *Control) ReadLive() (Live, error) {
	if !c.Enabled() || c.LivePath == "" {
		return Live{}, errors.New("governor: this runner publishes no live state")
	}
	raw, err := os.ReadFile(c.LivePath)
	if err != nil {
		return Live{}, err
	}
	var l Live
	if err := json.Unmarshal(raw, &l); err != nil {
		return Live{}, fmt.Errorf("governor: %s is not valid: %w", c.LivePath, err)
	}
	return l, nil
}

// writeAtomic writes a small file by rename, so a reader never sees a half
// written document. It is the same discipline runner.json is written with.
func writeAtomic(path string, raw []byte) error {
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(dir, filepath.Base(path)+".tmp*")
	if err != nil {
		return err
	}
	name := tmp.Name()
	defer func() { _ = os.Remove(name) }()
	if err := tmp.Chmod(0o600); err != nil {
		_ = tmp.Close()
		return err
	}
	if _, err := tmp.Write(raw); err != nil {
		_ = tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	return os.Rename(name, path)
}
