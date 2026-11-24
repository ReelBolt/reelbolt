//go:build linux

package runtime

import (
	"context"
	"testing"
)

func TestLinuxPlatformOrder(t *testing.T) {
	existing := map[string]bool{
		"/run/user/1000/docker.sock":        true,
		"/run/user/1000/podman/podman.sock": true,
		"/var/run/docker.sock":              true,
	}
	e := envWith(map[string]string{"XDG_RUNTIME_DIR": "/run/user/1000"},
		map[string]bool{"docker": true, "podman": true},
		&fakeRunner{rules: []rule{{match: "context inspect", err: errExit}}})
	e.Exists = func(p string) bool { return existing[p] }

	var sources []string
	for _, c := range Candidates(context.Background(), e) {
		sources = append(sources, c.Source)
	}
	want := []string{"rootless-docker-socket", "rootless-podman-socket", "podman-default", "rootful-docker-socket"}
	if len(sources) != len(want) {
		t.Fatalf("sources = %v, want %v", sources, want)
	}
	for i := range want {
		if sources[i] != want[i] {
			t.Fatalf("sources = %v, want %v (rootful must be last)", sources, want)
		}
	}
}
