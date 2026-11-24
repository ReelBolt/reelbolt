package runtimeimage

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestVendoredLockMatchesTheSandboxLock(t *testing.T) {
	// The pin is canonical in sandbox/runtime-image.lock and vendored here,
	// because go:embed cannot reach outside its own module. If they ever differ,
	// one of them is stale and a runner may be advertising a digest the release
	// pipeline did not publish.
	canonical := filepath.Join("..", "..", "..", "sandbox", "runtime-image.lock")
	want, err := os.ReadFile(canonical)
	if err != nil {
		t.Skipf("the sandbox module is not checked out beside the runner module: %v", err)
	}
	if string(want) != string(LockBytes()) {
		t.Fatalf("runner/internal/runtimeimage/runtime-image.lock has drifted from %s.\n"+
			"canonical: %s\nvendored:  %s", canonical, want, LockBytes())
	}
}

func TestPinnedParsesTheLock(t *testing.T) {
	l, err := Pinned()
	if err != nil {
		t.Fatalf("Pinned: %v", err)
	}
	if l.Version == "" {
		t.Error("the lock carries no version")
	}
}

func TestResolvePrecedence(t *testing.T) {
	pinned := Lock{Version: "1.2.3", Digest: "sha256:" + strings.Repeat("b", 64)}
	cases := []struct {
		name       string
		explicit   string
		env        string
		lock       Lock
		wantRef    string
		wantOrigin string
	}{
		{"configured wins over everything", "local/img:dev", "env/img:dev", pinned, "local/img:dev", "configured"},
		{"pinned wins over the environment", "", "env/img:dev", pinned,
			DefaultRepository + "@" + pinned.Digest, "pinned runtime-image.lock 1.2.3"},
		{"environment when nothing is pinned", "", "env/img:dev", Lock{}, "env/img:dev", "REELBOLT_RUNTIME_IMAGE"},
		{"development default last", "", "", Lock{}, DefaultRef, "development default"},
		{"an unpinned lock still falls through", "", "", Lock{Version: "0.0.0-unreleased"}, DefaultRef, "development default"},
		{"blank explicit is not a value", "   ", "env/img:dev", Lock{}, "env/img:dev", "REELBOLT_RUNTIME_IMAGE"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := resolve(tc.explicit, tc.env, tc.lock)
			if got.Ref != tc.wantRef {
				t.Errorf("Ref = %q, want %q", got.Ref, tc.wantRef)
			}
			if got.Origin != tc.wantOrigin {
				t.Errorf("Origin = %q, want %q", got.Origin, tc.wantOrigin)
			}
		})
	}
}

func TestDigest(t *testing.T) {
	if d := Digest("repo@sha256:abc"); d != "sha256:abc" {
		t.Errorf("Digest = %q", d)
	}
	if d := Digest("repo:latest"); d != "" {
		t.Errorf("a tag is not a digest, got %q", d)
	}
	if d := Digest("repo@sha512:abc"); d != "" {
		t.Errorf("only sha256 is accepted, got %q", d)
	}
}
