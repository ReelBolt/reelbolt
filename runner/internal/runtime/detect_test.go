package runtime

import (
	"context"
	"testing"
)

func envWith(vars map[string]string, tools map[string]bool, r Runner) Env {
	return Env{
		Getenv: func(k string) string { return vars[k] },
		LookPath: func(n string) (string, error) {
			if tools[n] {
				return n, nil
			}
			return "", errExit
		},
		Exists: func(string) bool { return false },
		Runner: r,
	}
}

func TestCandidatesOrderAndDedup(t *testing.T) {
	r := &fakeRunner{rules: []rule{{match: "context inspect", out: "unix:///ctx.sock\n"}}}
	e := envWith(map[string]string{"DOCKER_HOST": "unix:///env.sock"}, map[string]bool{"docker": true}, r)
	got := Candidates(context.Background(), e)
	if len(got) < 2 {
		t.Fatalf("got %d candidates, want at least 2: %+v", len(got), got)
	}
	if got[0].Source != "DOCKER_HOST" || got[0].Host != "unix:///env.sock" {
		t.Errorf("first = %+v, want DOCKER_HOST first", got[0])
	}
	if got[1].Source != "docker-context" || got[1].Host != "unix:///ctx.sock" {
		t.Errorf("second = %+v, want docker-context second", got[1])
	}

	// The same host from DOCKER_HOST and the context must collapse to one.
	r = &fakeRunner{rules: []rule{{match: "context inspect", out: "unix:///env.sock\n"}}}
	e = envWith(map[string]string{"DOCKER_HOST": "unix:///env.sock"}, map[string]bool{"docker": true}, r)
	n := 0
	for _, c := range Candidates(context.Background(), e) {
		if c.Host == "unix:///env.sock" {
			n++
		}
	}
	if n != 1 {
		t.Errorf("duplicate host appeared %d times, want 1", n)
	}
}

func TestCandidatesSkipsMissingCLIs(t *testing.T) {
	e := envWith(map[string]string{"DOCKER_HOST": "unix:///x.sock"}, nil, &fakeRunner{})
	for _, c := range Candidates(context.Background(), e) {
		t.Errorf("unexpected candidate with no CLI installed: %+v", c)
	}
}

func TestCandidatesIgnoresUselessContext(t *testing.T) {
	for _, out := range []string{"", "<no value>\n"} {
		r := &fakeRunner{rules: []rule{{match: "context inspect", out: out}}}
		e := envWith(nil, map[string]bool{"docker": true}, r)
		for _, c := range Candidates(context.Background(), e) {
			if c.Source == "docker-context" {
				t.Errorf("context output %q produced a candidate", out)
			}
		}
	}
	r := &fakeRunner{rules: []rule{{match: "context inspect", err: errExit}}}
	e := envWith(nil, map[string]bool{"docker": true}, r)
	for _, c := range Candidates(context.Background(), e) {
		if c.Source == "docker-context" {
			t.Error("failing context inspect produced a candidate")
		}
	}
}
