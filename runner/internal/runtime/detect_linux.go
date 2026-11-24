//go:build linux

package runtime

import "path/filepath"

// platformCandidates: rootless docker socket, rootless podman, then the
// rootful docker socket last (ADR section 2, steps 3 to 5).
func platformCandidates(e Env) []Candidate {
	var out []Candidate
	docker, _ := e.LookPath("docker")
	podman, _ := e.LookPath("podman")

	if xdg := e.Getenv("XDG_RUNTIME_DIR"); xdg != "" {
		if sock := filepath.Join(xdg, "docker.sock"); e.Exists(sock) {
			out = append(out, Candidate{Kind: KindDocker, Binary: docker, Host: "unix://" + sock, Source: "rootless-docker-socket"})
		}
		if sock := filepath.Join(xdg, "podman", "podman.sock"); e.Exists(sock) {
			out = append(out, Candidate{Kind: KindPodman, Binary: podman, Host: "unix://" + sock, Source: "rootless-podman-socket"})
		}
	}
	if podman != "" {
		// Podman CLI default (rootless, or a configured remote connection).
		out = append(out, Candidate{Kind: KindPodman, Binary: podman, Source: "podman-default"})
	}
	if e.Exists("/var/run/docker.sock") {
		out = append(out, Candidate{Kind: KindDocker, Binary: docker, Host: "unix:///var/run/docker.sock", Source: "rootful-docker-socket"})
	}
	return out
}
