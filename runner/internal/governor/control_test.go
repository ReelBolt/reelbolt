package governor

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func testControl(t *testing.T) *Control {
	t.Helper()
	dir := t.TempDir()
	return &Control{
		RequestPath: filepath.Join(dir, "governor.json"),
		LivePath:    filepath.Join(dir, "runner.live.json"),
	}
}

func TestRequestAndTakeRoundTrip(t *testing.T) {
	c := testControl(t)
	if got, ok, err := c.Take(); err != nil || ok {
		t.Fatalf("Take on an empty channel = %+v, %t, %v", got, ok, err)
	}
	if err := c.Request(ActionPause, "the command line", "because"); err != nil {
		t.Fatalf("Request: %v", err)
	}
	req, ok, err := c.Take()
	if err != nil || !ok {
		t.Fatalf("Take = %+v, %t, %v", req, ok, err)
	}
	if req.Action != ActionPause || req.By != "the command line" || req.Reason != "because" {
		t.Fatalf("request = %+v", req)
	}
	// Consumed exactly once.
	if _, ok, err := c.Take(); err != nil || ok {
		t.Fatalf("a request must be applied at most once: %t, %v", ok, err)
	}
}

// The failure mode this whole file is written around: a request that cannot be
// read must be DROPPED, never retried. A runner that re-read a broken pause
// request on every poll is a runner that can never be resumed.
func TestAnUnreadableRequestIsConsumedNotRetried(t *testing.T) {
	c := testControl(t)
	if err := os.WriteFile(c.RequestPath, []byte("{not json"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, ok, err := c.Take(); err == nil || ok {
		t.Fatalf("an unparsable request must be reported: ok=%t err=%v", ok, err)
	}
	if _, err := os.Stat(c.RequestPath); !os.IsNotExist(err) {
		t.Fatal("the unusable request was left on disk to fail again")
	}
	// And the runner carries on: no request, no error, nothing held.
	if _, ok, err := c.Take(); err != nil || ok {
		t.Fatalf("the next poll must be clean: %t, %v", ok, err)
	}
}

func TestAnUnknownActionIsRefusedAndConsumed(t *testing.T) {
	c := testControl(t)
	raw, _ := json.Marshal(Request{Action: "explode"})
	if err := os.WriteFile(c.RequestPath, raw, 0o600); err != nil {
		t.Fatal(err)
	}
	if _, ok, err := c.Take(); err == nil || ok {
		t.Fatalf("an unknown action must be refused: ok=%t err=%v", ok, err)
	}
	if _, err := os.Stat(c.RequestPath); !os.IsNotExist(err) {
		t.Fatal("the unknown action was left on disk")
	}
}

// Clear is what makes a pause unable to outlive its process.
func TestClearRemovesAStaleRequest(t *testing.T) {
	c := testControl(t)
	if err := c.Request(ActionPause, "an earlier process", ""); err != nil {
		t.Fatal(err)
	}
	if err := c.Clear(); err != nil {
		t.Fatalf("Clear: %v", err)
	}
	if _, ok, err := c.Take(); err != nil || ok {
		t.Fatalf("a cleared request must be gone: %t, %v", ok, err)
	}
	// Clearing nothing is not an error: a first run has no file.
	if err := c.Clear(); err != nil {
		t.Fatalf("Clear on a missing file: %v", err)
	}
}

func TestRequestRefusesAnUnknownAction(t *testing.T) {
	c := testControl(t)
	if err := c.Request("maybe", "me", ""); err == nil {
		t.Fatal("Request accepted an action the daemon has no case for")
	}
}

func TestLiveRoundTripAndFreshness(t *testing.T) {
	c := testControl(t)
	now := time.Date(2026, time.January, 5, 10, 0, 0, 0, time.UTC)
	live := Live{
		PID:         4242,
		StartedAt:   now.Add(-time.Hour).Format(time.RFC3339),
		HeartbeatAt: now.Format(time.RFC3339),
		Status:      Status{Allowed: true, Reason: ReasonAllowed, Mode: ModeAlways},
	}
	if err := c.Publish(live); err != nil {
		t.Fatalf("Publish: %v", err)
	}
	got, err := c.ReadLive()
	if err != nil {
		t.Fatalf("ReadLive: %v", err)
	}
	if got.PID != 4242 || !got.Status.Allowed || got.Status.Mode != ModeAlways {
		t.Fatalf("live = %+v", got)
	}
	if !got.Fresh(now) {
		t.Error("a document written just now must be fresh")
	}
	if got.Fresh(now.Add(LiveHeartbeatGrace + time.Second)) {
		t.Error("a document past the grace must not be fresh")
	}

	info, err := os.Stat(c.LivePath)
	if err != nil {
		t.Fatal(err)
	}
	if perm := info.Mode().Perm(); perm != 0o600 {
		t.Errorf("live document mode = %o, want 600", perm)
	}
}

func TestRemoveLiveMakesACommandSaySo(t *testing.T) {
	c := testControl(t)
	if err := c.Publish(Live{PID: 1, HeartbeatAt: time.Now().Format(time.RFC3339)}); err != nil {
		t.Fatal(err)
	}
	if err := c.RemoveLive(); err != nil {
		t.Fatalf("RemoveLive: %v", err)
	}
	if _, err := c.ReadLive(); err == nil {
		t.Fatal("a stopped runner must leave no live document behind")
	}
}

func TestADisabledControlIsSafe(t *testing.T) {
	c := &Control{}
	if c.Enabled() {
		t.Fatal("a control with no paths must be disabled")
	}
	if err := c.Clear(); err != nil {
		t.Errorf("Clear: %v", err)
	}
	if err := c.Publish(Live{}); err != nil {
		t.Errorf("Publish: %v", err)
	}
	if err := c.RemoveLive(); err != nil {
		t.Errorf("RemoveLive: %v", err)
	}
	if _, ok, err := c.Take(); err != nil || ok {
		t.Errorf("Take: %t, %v", ok, err)
	}
	if err := c.Request(ActionPause, "me", ""); err == nil {
		t.Error("Request must fail loudly when there is nowhere to write")
	}
	if _, err := c.ReadLive(); err == nil {
		t.Error("ReadLive must fail when nothing publishes")
	}
}
