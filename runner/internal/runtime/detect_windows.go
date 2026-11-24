//go:build windows

package runtime

// platformCandidates: the Podman machine through the CLI's default connection
// (Podman Desktop), then the Docker Desktop named pipe. Docker Desktop is
// normally already found through its docker context.
func platformCandidates(e Env) []Candidate {
	var out []Candidate
	docker, _ := e.LookPath("docker")
	podman, _ := e.LookPath("podman")

	if podman != "" {
		out = append(out, Candidate{Kind: KindPodman, Binary: podman, Source: "podman-machine"})
	}
	out = append(out, Candidate{Kind: KindDocker, Binary: docker, Host: "npipe:////./pipe/docker_engine", Source: "docker-named-pipe"})
	return out
}
