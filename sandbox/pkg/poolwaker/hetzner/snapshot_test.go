package hetzner

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/poolwaker"
)

func TestCoreLimitRefusalBecomesASizeRefusal(t *testing.T) {
	p := newP(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodPost {
			w.WriteHeader(http.StatusForbidden)
			fmt.Fprint(w, `{"error":{"code":"resource_limit_exceeded","message":"dedicated core limit exceeded"}}`)
			return
		}
		fmt.Fprint(w, `{}`)
	}), nil)
	_, err := p.Create(context.Background(), poolwaker.NodeSpec{Name: "rb-pool-a", Instance: "stg", ServerType: "ccx43", UserData: "#cloud-config"})
	var sr *poolwaker.SizeRefusedError
	if !errors.Is(err, poolwaker.ErrSizeRefused) || !errors.As(err, &sr) || sr.ServerType != "ccx43" || !strings.Contains(sr.Reason, "dedicated core limit exceeded") {
		t.Fatalf("want a size refusal naming ccx43, got %v", err)
	}
	if strings.Contains(err.Error(), secret) {
		t.Fatal("the token must never appear in an error")
	}
}

func TestOtherErrorsAreNotSizeRefusals(t *testing.T) {
	for _, tc := range []struct {
		status int
		body   string
	}{
		{http.StatusUnauthorized, `{"error":{"code":"unauthorized","message":"unable to authenticate"}}`},
		{http.StatusUnprocessableEntity, `{"error":{"code":"invalid_input","message":"bad user_data"}}`},
		{http.StatusInternalServerError, `{"error":{"code":"server_error","message":"oops"}}`},
	} {
		p := newP(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(tc.status)
			fmt.Fprint(w, tc.body)
		}), nil)
		_, err := p.Create(context.Background(), poolwaker.NodeSpec{Name: "rb-pool-a", Instance: "stg", ServerType: "ccx33"})
		if err == nil || errors.Is(err, poolwaker.ErrSizeRefused) {
			t.Fatalf("status %d must stay an ordinary error, got %v", tc.status, err)
		}
	}
}

func imagesHandler(t *testing.T, calls *int32, body string, created *map[string]any) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case strings.HasPrefix(r.URL.Path, "/images"):
			atomic.AddInt32(calls, 1)
			q := r.URL.Query()
			if q.Get("type") != "snapshot" || q.Get("label_selector") != SnapshotLabel || q.Get("status") != "available" {
				t.Errorf("snapshot query must select labelled available snapshots: %s", r.URL.RawQuery)
			}
			fmt.Fprint(w, body)
		case r.Method == http.MethodPost:
			_ = json.NewDecoder(r.Body).Decode(created)
			fmt.Fprint(w, `{"server":{"id":7,"name":"rb-pool-a","status":"initializing","labels":{},"server_type":{"name":"ccx33"}}}`)
		default:
			fmt.Fprint(w, `{"server_types":[{"name":"ccx33","prices":[{"location":"fsn1","price_hourly":{"net":"0.222"}}]}]}`)
		}
	})
}

func TestLatestSnapshotIsTheNewestLabelledOneAndCached(t *testing.T) {
	var calls int32
	var got map[string]any
	body := `{"images":[
	 {"id":10,"type":"snapshot","status":"available","created":"2026-11-09T10:00:00Z","labels":{"reelbolt-pool-image":"1","reelbolt-version":"old"}},
	 {"id":12,"type":"snapshot","status":"available","created":"2026-11-11T10:00:00Z","labels":{"reelbolt-pool-image":"1","reelbolt-version":"new"}},
	 {"id":13,"type":"snapshot","status":"available","created":"2026-11-12T10:00:00Z","labels":{}},
	 {"id":14,"type":"backup","status":"available","created":"2026-11-13T10:00:00Z","labels":{"reelbolt-pool-image":"1"}}]}`
	now := time.Date(2026, 11, 11, 12, 0, 0, 0, time.UTC)
	p := newP(t, imagesHandler(t, &calls, body, &got), func(c *Config) { c.SnapshotID = ""; c.LatestSnapshot = true })
	p.now = func() time.Time { return now }
	for i := 0; i < 2; i++ {
		if _, err := p.Create(context.Background(), poolwaker.NodeSpec{Name: "rb-pool-a", Instance: "stg", ServerType: "ccx33", UserData: "x"}); err != nil {
			t.Fatal(err)
		}
		if got["image"] != float64(12) {
			t.Fatalf("image %v, want the newest labelled snapshot 12 (not unlabelled 13, not the backup 14)", got["image"])
		}
	}
	if calls != 1 {
		t.Fatalf("the choice is cached: %d image lookups", calls)
	}
	now = now.Add(6 * time.Minute)
	if _, err := p.Create(context.Background(), poolwaker.NodeSpec{Name: "rb-pool-a", Instance: "stg", ServerType: "ccx33", UserData: "x"}); err != nil || calls != 2 {
		t.Fatalf("after the cache TTL the list is read again: calls=%d err=%v", calls, err)
	}
}

func TestPinnedSnapshotWinsAndNoLookupIsMade(t *testing.T) {
	var calls int32
	var got map[string]any
	p := newP(t, imagesHandler(t, &calls, `{"images":[]}`, &got), func(c *Config) { c.LatestSnapshot = true })
	if _, err := p.Create(context.Background(), poolwaker.NodeSpec{Name: "rb-pool-a", Instance: "stg", ServerType: "ccx33", UserData: "x"}); err != nil {
		t.Fatal(err)
	}
	if got["image"] != float64(4242) || calls != 0 {
		t.Fatalf("a pinned id is used as is: image=%v lookups=%d", got["image"], calls)
	}
}

func TestNoLabelledSnapshotIsAClearErrorNotACreate(t *testing.T) {
	var calls int32
	var got map[string]any
	p := newP(t, imagesHandler(t, &calls, `{"images":[]}`, &got), func(c *Config) { c.SnapshotID = ""; c.LatestSnapshot = true })
	_, err := p.Create(context.Background(), poolwaker.NodeSpec{Name: "rb-pool-a", Instance: "stg", ServerType: "ccx33", UserData: "x"})
	if err == nil || !strings.Contains(err.Error(), SnapshotLabel) || got != nil {
		t.Fatalf("want a clear error and no server created: %v %v", err, got)
	}
	if _, err := New(Config{Token: NewToken(secret), Instance: "stg"}); err == nil {
		t.Fatal("neither a snapshot id nor LatestSnapshot must be refused")
	}
}
