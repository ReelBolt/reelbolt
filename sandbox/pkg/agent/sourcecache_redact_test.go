package agent

import (
	"context"
	"net/http"
	"strings"
	"testing"
)

// stubCache is a SourceCache that is never reached: the URL check fails first.
type stubCache struct{}

func (stubCache) Acquire(string) SourceEntry          { return nil }
func (stubCache) Create(string) (SourceWriter, error) { return nil, nil }
func (stubCache) RecordHit(int64)                     {}
func (stubCache) RecordMiss()                         {}

// An unusable stage url must not echo its signature in the op error.
func TestCachedStageErrorRedactsTheURL(t *testing.T) {
	_, opErr := FetchToFSCached(context.Background(), http.DefaultClient, stubCache{}, nil,
		"https://bad host/x?X-Amz-Signature=supersecret", "inputs/a.mp4", 1<<20, "")
	if opErr == nil {
		t.Fatal("want an error for an unusable url")
	}
	if strings.Contains(opErr.Message, "supersecret") {
		t.Fatalf("the error leaks the signature: %q", opErr.Message)
	}
}
