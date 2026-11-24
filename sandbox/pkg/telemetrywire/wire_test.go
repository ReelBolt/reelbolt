package telemetrywire

import (
	"errors"
	"fmt"
	"strings"
	"testing"
)

const crash = `{"schema":"reelbolt.telemetry/1","kind":"crash","id":"0123456789abcdef0123456789abcdef","at":"2026-10-11T09:00:00Z",
"runnerVersion":"1.4.2","os":"linux","arch":"amd64","go":"go1.27.0","runtime":"docker","runtimeMode":"rootless",
"stage":"run","panicType":"runtime.boundsError",
"frames":[{"func":"github.com/vecchiotom/reelbolt/runner/internal/daemon.(*Daemon).Run","file":"daemon.go","line":42}],"framesDropped":2}`

const jobFailure = `{"schema":"reelbolt.telemetry/1","kind":"job_failure","id":"0123456789abcdef","at":"2026-10-11T09:00:00Z",
"runnerVersion":"1.4.2","os":"windows","arch":"amd64","go":"go1.27.0",
"jobType":"video.compile","failureClass":"runner","failureCode":"container_unavailable"}`

func TestTheTwoShapesTheRunnerSendsAreAccepted(t *testing.T) {
	for name, doc := range map[string]string{"crash": crash, "job_failure": jobFailure} {
		r, err := Decode([]byte(doc))
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if r.Kind != name {
			t.Fatalf("kind = %q, want %q", r.Kind, name)
		}
	}
}

func TestAnythingOutsideTheAllowlistIsRefusedWhole(t *testing.T) {
	cases := map[string]string{
		"an unknown field":             strings.Replace(jobFailure, `"jobType"`, `"message":"C:\\Users\\sam\\clip.mp4","jobType"`, 1),
		"a second document":            jobFailure + ` {}`,
		"another schema":               strings.Replace(jobFailure, "telemetry/1", "telemetry/2", 1),
		"an unknown kind":              strings.Replace(jobFailure, `"job_failure"`, `"render"`, 1),
		"a path for a file":            strings.Replace(crash, `"daemon.go"`, `"/home/sam/daemon.go"`, 1),
		"free text as a code":          strings.Replace(jobFailure, `"container_unavailable"`, `"could not open /tmp/x"`, 1),
		"a project name as a job type": strings.Replace(jobFailure, `"video.compile"`, `"My Launch Video"`, 1),
		"an id that is a sentence":     strings.Replace(jobFailure, `"0123456789abcdef"`, `"device of sam@example.com"`, 1),
		"a bad time":                   strings.Replace(jobFailure, `2026-10-11T09:00:00Z`, `yesterday`, 1),
		"an unknown runtime":           strings.Replace(crash, `"docker"`, `"lxc"`, 1),
		"a mode without a runtime":     strings.Replace(crash, `"runtime":"docker",`, ``, 1),
		"crash fields on a failure":    strings.Replace(jobFailure, `"jobType"`, `"stage":"run","jobType"`, 1),
		"failure fields on a crash":    strings.Replace(crash, `"stage"`, `"failureCode":"x","stage"`, 1),
		"a negative line":              strings.Replace(crash, `"line":42`, `"line":-1`, 1),
		"not JSON":                     `stage=run`,
	}
	for name, doc := range cases {
		if _, err := Decode([]byte(doc)); !errors.Is(err, ErrInvalid) {
			t.Errorf("%s: err = %v, want ErrInvalid", name, err)
		}
	}
}

func TestAStackLongerThanTheRunnersCapIsRefused(t *testing.T) {
	frames := make([]string, MaxFrames+1)
	for i := range frames {
		frames[i] = fmt.Sprintf(`{"func":"main.f%d"}`, i)
	}
	doc := strings.Replace(crash,
		`[{"func":"github.com/vecchiotom/reelbolt/runner/internal/daemon.(*Daemon).Run","file":"daemon.go","line":42}]`,
		"["+strings.Join(frames, ",")+"]", 1)
	if _, err := Decode([]byte(doc)); !errors.Is(err, ErrInvalid) {
		t.Fatalf("a %d-frame stack was accepted: %v", MaxFrames+1, err)
	}
}
