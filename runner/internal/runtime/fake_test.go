package runtime

import (
	"context"
	"errors"
	"strings"
	"sync"
)

// rule answers any command whose "name arg arg ..." line contains match.
type rule struct {
	match string
	out   string
	err   error
}

// fakeRunner is a scripted Runner. Rules are checked in order; an unmatched
// command fails loudly so a test cannot pass by accident.
type fakeRunner struct {
	mu    sync.Mutex
	rules []rule
	calls []string
	envs  [][]string
}

func (f *fakeRunner) Run(_ context.Context, env []string, name string, args ...string) ([]byte, error) {
	line := name + " " + strings.Join(args, " ")
	f.mu.Lock()
	f.calls = append(f.calls, line)
	f.envs = append(f.envs, env)
	f.mu.Unlock()
	for _, r := range f.rules {
		if strings.Contains(line, r.match) {
			return []byte(r.out), r.err
		}
	}
	return nil, errors.New("fakeRunner: unexpected command: " + line)
}

func (f *fakeRunner) called(substr string) bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, c := range f.calls {
		if strings.Contains(c, substr) {
			return true
		}
	}
	return false
}

const (
	dockerInfoRootless = `{"Architecture":"x86_64","OperatingSystem":"Ubuntu 24.04","SecurityOptions":["name=seccomp,profile=builtin","name=rootless"]}`
	dockerInfoRootful  = `{"Architecture":"x86_64","OperatingSystem":"Ubuntu 24.04","SecurityOptions":["name=seccomp,profile=builtin"]}`
	dockerInfoDesktop  = `{"Architecture":"aarch64","OperatingSystem":"Docker Desktop","SecurityOptions":[]}`
	podmanInfoLocal    = `{"host":{"arch":"amd64","security":{"rootless":true},"serviceIsRemote":false}}`
	podmanInfoMachine  = `{"host":{"arch":"amd64","security":{"rootless":false},"serviceIsRemote":true}}`

	testImage = "ghcr.io/reelbolt/reelbolt-sandbox-runtime@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
)

var errExit = errors.New("exit status 1")
