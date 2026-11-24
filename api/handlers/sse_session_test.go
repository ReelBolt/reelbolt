package handlers

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
)

// streamUntilClosed runs an SSE handler with a caller whose session check is `valid` and whose
// token expires at `expires`, and reports whether the handler returned by itself within wait.
// The request context is never cancelled before then: the client stays connected.
func streamUntilClosed(t *testing.T, stream func(http.ResponseWriter, *http.Request), expires time.Time, valid func() bool, wait time.Duration) (closed bool, body string) {
	t.Helper()
	prev := ssePingInterval
	ssePingInterval = 10 * time.Millisecond
	t.Cleanup(func() { ssePingInterval = prev })

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	uc := middleware.UserContext{UserID: uuid.NewString(), OrganizationID: uuid.New(), OrgRole: models.OrgRoleMember}
	ctx = context.WithValue(ctx, middleware.UserContextKey, uc)
	ctx = middleware.WithSessionCheck(ctx, expires, valid)
	req := httptest.NewRequestWithContext(ctx, http.MethodGet, "/api/v1/workflows/events", nil)
	rec := httptest.NewRecorder()

	done := make(chan struct{})
	go func() {
		defer close(done)
		stream(rec, req)
	}()
	select {
	case <-done:
		return true, rec.Body.String()
	case <-time.After(wait):
		cancel()
		<-done
		return false, rec.Body.String()
	}
}

func executionStream(w http.ResponseWriter, r *http.Request) {
	uc, _ := middleware.GetUserContext(r)
	streamExecutionEvents(w, r, w.(http.Flusher), uc, uuid.NewString())
}

// AUTHZ-GO-2 / API-GO-04: a stream ends on the ping tick once its caller would no longer be
// admitted (removed or demoted member, password reset, deleted account).
func TestEventStreams_CloseWhenTheSessionStopsBeingValid(t *testing.T) {
	for name, stream := range map[string]func(http.ResponseWriter, *http.Request){
		"org events":       handleWorkflowEvents,
		"execution events": executionStream,
	} {
		var checks atomic.Int32
		// Admitted for the first two ticks, then revoked.
		valid := func() bool { return checks.Add(1) <= 2 }
		closed, body := streamUntilClosed(t, stream, time.Time{}, valid, 2*time.Second)
		if !closed {
			t.Errorf("%s: the stream outlived its session", name)
		}
		if !strings.Contains(body, "event: connected") || strings.Count(body, ": ping") != 2 {
			t.Errorf("%s: want the connected event and two pings before the close, got %q", name, body)
		}
	}
}

// A stream ends at the token's exp even if nothing else changed.
func TestEventStreams_EndAtTokenExpiry(t *testing.T) {
	for name, stream := range map[string]func(http.ResponseWriter, *http.Request){
		"org events":       handleWorkflowEvents,
		"execution events": executionStream,
	} {
		closed, _ := streamUntilClosed(t, stream, time.Now().Add(60*time.Millisecond), func() bool { return true }, 2*time.Second)
		if !closed {
			t.Errorf("%s: the stream outlived its token", name)
		}
	}
}

// A session that stays valid keeps its stream: the re-check must not end healthy streams.
func TestEventStreams_StayOpenWhileTheSessionIsValid(t *testing.T) {
	closed, body := streamUntilClosed(t, handleWorkflowEvents, time.Now().Add(time.Hour), func() bool { return true }, 150*time.Millisecond)
	if closed {
		t.Fatal("a valid session's stream was closed")
	}
	if strings.Count(body, ": ping") < 2 {
		t.Fatalf("the keep-alive stopped: %q", body)
	}
}
