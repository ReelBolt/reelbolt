//go:build !linux && !windows

package runtime

// platformCandidates is a compile-only stub for platforms the runner does not
// ship on (decision D26: no macOS runner for now). It offers the conventional
// Docker socket and the Podman CLI default so the package still behaves
// sensibly if built there.
func platformCandidates(e Env) []Candidate {
	var out []Candidate
	docker, _ := e.LookPath("docker")
	podman, _ := e.LookPath("podman")
	if podman != "" {
		out = append(out, Candidate{Kind: KindPodman, Binary: podman, Source: "podman-default"})
	}
	if e.Exists("/var/run/docker.sock") {
		out = append(out, Candidate{Kind: KindDocker, Binary: docker, Host: "unix:///var/run/docker.sock", Source: "docker-socket"})
	}
	return out
}
