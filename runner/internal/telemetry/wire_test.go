package telemetry

import (
	"errors"
	"reflect"
	"runtime/debug"
	"sort"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/telemetrywire"
)

// The collector (the runner gateway's /telemetry/v1/reports) refuses any
// document that is not exactly what this package writes. These tests hold the
// two ends together: every event shape the runner builds must pass the
// collector's strict decode, and the two structs must name the same fields.

func TestEveryEventTheRunnerBuildsPassesTheCollectorsCheck(t *testing.T) {
	at := time.Date(2026, 10, 11, 9, 0, 0, 0, time.UTC)
	events := map[string]Event{
		"crash from a real stack": NewCrash(testEnv(), "0123456789abcdef0123456789abcdef", at, "run",
			errors.New("C:\\Users\\sam\\clip.mp4: boom"), debug.Stack()),
		"crash with no stack":        NewCrash(testEnv(), "0123456789abcdef", at, "probe", "string panic", nil),
		"crash with a hostile stage": NewCrash(testEnv(), "0123456789abcdef", at, "Not A Stage", nil, nil),
		"job failure":                NewJobFailure(testEnv(), "0123456789abcdef", at, "video.compile", "runner", "container_unavailable"),
		"job failure with junk":      NewJobFailure(Env{Version: "dev build!", OS: "linux"}, "0123456789abcdef", at, "My Project", "Runner", "/tmp/x"),
		"no runtime":                 NewJobFailure(Env{Version: "1.0.0", RuntimeMode: "rootless"}, "0123456789abcdef", at, "video.compile", "runner", "x"),
	}
	for name, ev := range events {
		body, err := ev.Payload()
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if _, err := telemetrywire.Decode(body); err != nil {
			t.Errorf("%s: the collector refuses what the runner sends: %v\n%s", name, err, body)
		}
	}
}

// The schema strings must be the same, and so must the field names: a field
// the runner adds without the collector learning it would make every report
// of that kind a 400.
func TestTheCollectorKnowsExactlyTheRunnersFields(t *testing.T) {
	if Schema != telemetrywire.Schema {
		t.Fatalf("schema %q vs collector %q", Schema, telemetrywire.Schema)
	}
	if maxFrames != telemetrywire.MaxFrames {
		t.Fatalf("frame cap %d vs collector %d", maxFrames, telemetrywire.MaxFrames)
	}
	for _, pair := range [][2]any{{Event{}, telemetrywire.Report{}}, {Frame{}, telemetrywire.Frame{}}} {
		ours, theirs := jsonNames(reflect.TypeOf(pair[0])), jsonNames(reflect.TypeOf(pair[1]))
		if strings.Join(ours, ",") != strings.Join(theirs, ",") {
			t.Fatalf("%T fields %v, collector %T fields %v", pair[0], ours, pair[1], theirs)
		}
	}
	for _, k := range []Kind{KindCrash, KindJobFailure} {
		if string(k) != telemetrywire.KindCrash && string(k) != telemetrywire.KindJobFailure {
			t.Fatalf("kind %q is unknown to the collector", k)
		}
	}
}

func jsonNames(t reflect.Type) []string {
	var out []string
	for i := 0; i < t.NumField(); i++ {
		tag := t.Field(i).Tag.Get("json")
		out = append(out, tag)
	}
	sort.Strings(out)
	return out
}
