package main

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
)

func TestConflictMapsTo409(t *testing.T) {
	if statusFromErr(container.ErrPeerSandboxExists) != http.StatusConflict {
		t.Fatal("peer conflict must be 409")
	}
}

func TestAdoptRequiresBearer(t *testing.T) {
	h := &apiHandler{manager: container.NewWithRunner(container.Config{SandboxRoot: t.TempDir()}, "docker", nil)}
	guarded := requireAPIToken("tok", http.HandlerFunc(h.adopt))
	rec := httptest.NewRecorder()
	guarded.ServeHTTP(rec, httptest.NewRequest(http.MethodPost, "/admin/adopt", nil))
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("want 401, got %d", rec.Code)
	}
}

func TestAdoptRefusesMissingOrNullPeerSlots(t *testing.T) {
	h := &apiHandler{manager: container.NewWithRunner(container.Config{SandboxRoot: t.TempDir(), Slot: "blue"},
		"docker", func(ctx context.Context, args ...string) ([]byte, error) { return nil, nil })}
	for body, want := range map[string]int{
		`{}`:                          http.StatusBadRequest,
		`{"peerSlots":null}`:          http.StatusBadRequest,
		``:                            http.StatusBadRequest,
		`{"peerSlots":[]}`:            http.StatusOK,
		`{"peerSlots":["green"]}`:     http.StatusOK,
		`{"peerSlots":[""]}`:          http.StatusBadRequest,
		`{"peerSlots":["green"," "]}`: http.StatusBadRequest,
	} {
		rec := httptest.NewRecorder()
		h.adopt(rec, httptest.NewRequest(http.MethodPost, "/admin/adopt", strings.NewReader(body)))
		if rec.Code != want {
			t.Fatalf("body %q: want %d, got %d", body, want, rec.Code)
		}
	}
}

// With SANDBOX_SLOT empty there is no handoff: /admin/adopt does not exist and runs no pass.
func TestAdoptIs404WithoutASlot(t *testing.T) {
	ran := false
	h := &apiHandler{manager: container.NewWithRunner(container.Config{SandboxRoot: t.TempDir()},
		"docker", func(ctx context.Context, args ...string) ([]byte, error) { ran = true; return nil, nil })}
	rec := httptest.NewRecorder()
	h.adopt(rec, httptest.NewRequest(http.MethodPost, "/admin/adopt", strings.NewReader(`{"peerSlots":[]}`)))
	if rec.Code != http.StatusNotFound || ran {
		t.Fatalf("want 404 and no container call, got %d (ran=%v)", rec.Code, ran)
	}
}
