package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

// scalingOf builds the gateway's answer with the fields a case cares about.
func scalingOf(desired int, queued, running int64, buffer, connected, available, freeSlots int) scalingResponse {
	return scalingResponse{
		DesiredCloudPoolSlots: desired, Queued: queued, Running: running, Buffer: buffer,
		ConnectedCloudPool: connected, AvailableCloudPool: available, FreeSlotsCloudPool: freeSlots,
		GatewayInstance: "10.0.0.7",
	}
}

// TestDecideTranslatesSlotsToNodesOnlyAfterD5c is the rule that keeps this
// program from becoming a second formula for the same thing: the slot number is
// the gateway's, and the only arithmetic here is slots-per-node.
func TestDecideTranslatesSlotsToNodesOnlyAfterD5c(t *testing.T) {
	cases := []struct {
		name         string
		scaling      scalingResponse
		min, max     int
		slotsPerNode int
		wantNodes    int
		wantCappedBy string
	}{
		{
			name: "an idle pool keeps the gateway's buffer", scaling: scalingOf(1, 0, 0, 1, 1, 1, 2),
			slotsPerNode: 1, wantNodes: 1, wantCappedBy: cappedByDesired,
		},
		{
			name: "one slot per node is one node per slot", scaling: scalingOf(5, 2, 3, 0, 1, 1, 0),
			slotsPerNode: 1, wantNodes: 5, wantCappedBy: cappedByDesired,
		},
		{
			name: "four slots per node rounds up", scaling: scalingOf(5, 2, 3, 0, 1, 1, 0),
			slotsPerNode: 4, wantNodes: 2, wantCappedBy: cappedByDesired,
		},
		{
			name: "a ceiling below the desire wins", scaling: scalingOf(9, 0, 0, 9, 0, 0, 0),
			max: 2, slotsPerNode: 1, wantNodes: 2, wantCappedBy: cappedByMax,
		},
		{
			name: "a floor above the desire wins", scaling: scalingOf(1, 0, 0, 1, 0, 0, 0),
			min: 3, slotsPerNode: 1, wantNodes: 3, wantCappedBy: cappedByMin,
		},
		{
			name: "running work is a floor of its own", scaling: scalingOf(1, 0, 6, 1, 1, 1, 0),
			slotsPerNode: 2, wantNodes: 3, wantCappedBy: cappedByRunning,
		},
		{
			name: "a zero slots-per-node degrades to one", scaling: scalingOf(3, 0, 0, 3, 0, 0, 0),
			slotsPerNode: 0, wantNodes: 3, wantCappedBy: cappedByDesired,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := Decide(tc.scaling, tc.min, tc.max, tc.slotsPerNode)
			if got.Nodes != tc.wantNodes {
				t.Fatalf("nodes = %d, want %d (%+v)", got.Nodes, tc.wantNodes, got)
			}
			if got.CappedBy != tc.wantCappedBy {
				t.Fatalf("cappedBy = %q, want %q", got.CappedBy, tc.wantCappedBy)
			}
			if got.DesiredSlots != tc.scaling.DesiredCloudPoolSlots {
				t.Fatalf("desiredSlots = %d, want the gateway's %d", got.DesiredSlots, tc.scaling.DesiredCloudPoolSlots)
			}
		})
	}
}

// TestDecideRefusesToScaleBelowRunningWork is the one rule that is a safety
// property rather than an arithmetic one: a ceiling smaller than the fleet in
// use is reported, not obeyed, because obeying it terminates renders.
func TestDecideRefusesToScaleBelowRunningWork(t *testing.T) {
	d := Decide(scalingOf(1, 0, 8, 1, 1, 1, 0), 0, 2, 1)
	if d.Nodes != 8 {
		t.Fatalf("nodes = %d, want the 8 already running rather than the cap of 2", d.Nodes)
	}
	if d.CappedBy != cappedByRunning {
		t.Fatalf("cappedBy = %q, want %q", d.CappedBy, cappedByRunning)
	}
	if len(d.Warnings) == 0 || !strings.Contains(strings.Join(d.Warnings, " "), "POOL_MAX_NODES=2") {
		t.Fatalf("the violated ceiling was not reported: %+v", d.Warnings)
	}
}

// TestDecideWarnsAboutConnectedButUselessWorkers uses the distinction D5c makes
// on purpose: a worker with no job types is connected and takes nothing, so
// adding nodes would not drain the queue.
func TestDecideWarnsAboutConnectedButUselessWorkers(t *testing.T) {
	d := Decide(scalingOf(4, 3, 1, 1, 2, 0, 0), 0, 0, 1)
	if joined := strings.Join(d.Warnings, " | "); !strings.Contains(joined, "advertise no job types") {
		t.Fatalf("no warning about useless workers: %v", d.Warnings)
	}
	// The saturation warning is deliberately NOT emitted alongside it: when
	// nothing is available, "the pool is saturated" adds nothing to "adding
	// nodes will not drain the queue".
	if joined := strings.Join(d.Warnings, " | "); strings.Contains(joined, "no free slots") {
		t.Fatalf("an unhelpful second warning was emitted: %v", d.Warnings)
	}

	// A pool that is available but full is the case that one is for.
	full := Decide(scalingOf(4, 3, 1, 1, 2, 2, 0), 0, 0, 1)
	if joined := strings.Join(full.Warnings, " | "); !strings.Contains(joined, "no free slots") {
		t.Fatalf("no warning about saturation: %v", full.Warnings)
	}
}

func TestCeilDiv(t *testing.T) {
	for _, tc := range []struct{ n, by, want int }{
		{0, 4, 0}, {1, 4, 1}, {4, 4, 1}, {5, 4, 2}, {8, 4, 2}, {3, 0, 3},
	} {
		if got := ceilDiv(tc.n, tc.by); got != tc.want {
			t.Fatalf("ceilDiv(%d, %d) = %d, want %d", tc.n, tc.by, got, tc.want)
		}
	}
}

func TestDecisionEnvCarriesTheWholeDecision(t *testing.T) {
	d := Decide(scalingOf(5, 2, 3, 0, 1, 1, 4), 0, 0, 2)
	env := strings.Join(d.Env(), " ")
	for _, want := range []string{
		"POOL_NODES=3", "POOL_DESIRED_SLOTS=5", "POOL_SLOTS_PER_NODE=2",
		"POOL_RUNNING=3", "POOL_QUEUED=2", "POOL_CAPPED_BY=desired",
	} {
		if !strings.Contains(env, want) {
			t.Fatalf("the environment does not carry %s: %s", want, env)
		}
	}
}

// TestParseMetricsReadsTheD5bGauges: these two names are the gateway's, and this
// program must only ever PARSE them — a second registration of either name in
// the gateway panics, so the scaler has no collector of its own at all.
// TestParseMetricsReadsTheD5bGauges: these two names are the gateway's, and this
// program must only ever PARSE them — a second registration of either name in
// the gateway panics, so the scaler has no collector of its own at all.
func TestParseMetricsReadsTheD5bGauges(t *testing.T) {
	const text = "# HELP reelbolt_runner_queue_depth queued\n" +
		"# TYPE reelbolt_runner_queue_depth gauge\n" +
		"reelbolt_runner_queue_depth{job_type=\"video.compile\",target=\"CloudPool\"} 2\n" +
		"reelbolt_runner_queue_depth{job_type=\"video.analyze.extract\",target=\"CloudPool\"} 1\n" +
		"reelbolt_runner_queue_depth{job_type=\"video.compile\",target=\"OrgRunner\"} 7\n" +
		"reelbolt_runner_free_slots{target=\"CloudPool\"} 4\n" +
		"reelbolt_runner_free_slots{target=\"OrgRunner\"} 9\n" +
		"reelbolt_runner_connected{kind=\"CloudPool\"} 2\n"

	got := ParseMetrics(text)
	if got.QueueDepth != 3 {
		t.Fatalf("queueDepth = %v, want 3 (the two CloudPool series, not the org one)", got.QueueDepth)
	}
	if got.FreeSlots != 4 {
		t.Fatalf("freeSlots = %v, want 4", got.FreeSlots)
	}
	if got.Series != 3 {
		t.Fatalf("series = %d, want 3", got.Series)
	}
}

func TestParseMetricsToleratesNonsense(t *testing.T) {
	for _, text := range []string{
		"",
		"# only a comment\n",
		"reelbolt_runner_free_slots\n",
		"reelbolt_runner_free_slots{target=\"CloudPool\"} not-a-number\n",
		"reelbolt_runner_free_slots{target=\"CloudPool\"\n",
	} {
		got := ParseMetrics(text)
		if got.QueueDepth != 0 || got.FreeSlots != 0 {
			t.Fatalf("ParseMetrics(%q) = %+v, want zeros", text, got)
		}
	}
}

func TestParseMetricsHandlesACommaInALabelValue(t *testing.T) {
	got := ParseMetrics("reelbolt_runner_free_slots{note=\"a,b\",target=\"CloudPool\"} 3\n")
	if got.FreeSlots != 3 {
		t.Fatalf("freeSlots = %v, want 3", got.FreeSlots)
	}
}

// TestScalingResponseMatchesTheGateway is the drift guard, and the reason this
// mirror struct is allowed to exist: the gateway is package main, so nothing can
// import its type. The test reads the gateway's own source — the technique D6's
// .NET client used — and fails when this client reads a field the gateway does
// not send. The tags are taken from this struct by reflection rather than
// retyped, so a field added here without a matching gateway field fails.
func TestScalingResponseMatchesTheGateway(t *testing.T) {
	source, err := os.ReadFile(filepath.Join("..", "runner-gateway", "sessions.go"))
	if err != nil {
		t.Fatalf("read the gateway source: %v", err)
	}
	block := scalingStructBlock(t, string(source))

	for _, tag := range mirrorJSONTags(t) {
		if !strings.Contains(block, tag) {
			t.Fatalf("this client reads %s but the gateway's scalingResponse does not declare it; the wire "+
				"shape changed and the decision above would silently read a zero", tag)
		}
	}
	if !strings.Contains(block, "DesiredCloudPoolSlots") {
		t.Fatal("the gateway no longer declares desiredCloudPoolSlots: D5c's formula is the one this scaler reads")
	}
}

// scalingStructBlock returns the body of the gateway's scalingResponse struct.
func scalingStructBlock(t *testing.T, source string) string {
	t.Helper()
	const marker = "type scalingResponse struct {"
	start := strings.Index(source, marker)
	if start < 0 {
		t.Fatal("the gateway no longer declares scalingResponse: the scaling signal moved or was renamed")
	}
	end := strings.Index(source[start:], "\n}")
	if end < 0 {
		t.Fatal("the gateway's scalingResponse struct is not closed as expected")
	}
	return source[start : start+end]
}

// mirrorJSONTags names the wire fields this client reads, taken from the
// struct by reflection so a field added here cannot go unmapped.
func mirrorJSONTags(t *testing.T) []string {
	t.Helper()
	typ := reflect.TypeOf(scalingResponse{})
	tags := make([]string, 0, typ.NumField())
	for i := 0; i < typ.NumField(); i++ {
		tag := typ.Field(i).Tag.Get("json")
		if tag == "" {
			t.Fatalf("field %s of scalingResponse has no json tag, so it is invisible on the wire", typ.Field(i).Name)
		}
		tags = append(tags, tag)
	}
	return tags
}

// TestDecideJSONIsReadableByF12 pins the machine-readable contract an autoscaler
// consumes, because that is the only interface F12 has.
func TestDecideJSONIsReadableByF12(t *testing.T) {
	d := Decide(scalingOf(4, 2, 2, 0, 1, 1, 1), 1, 8, 2)
	blob, err := json.Marshal(struct {
		Decision Decision `json:"decision"`
	}{d})
	if err != nil {
		t.Fatal(err)
	}
	var back struct {
		Decision map[string]any `json:"decision"`
	}
	if err := json.Unmarshal(blob, &back); err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"nodes", "desiredSlots", "slotsPerNode", "cappedBy", "queued", "running"} {
		if _, ok := back.Decision[key]; !ok {
			t.Fatalf("the JSON decision has no %q: %s", key, blob)
		}
	}
	if back.Decision["nodes"] != float64(2) {
		t.Fatalf("nodes = %v, want 2", back.Decision["nodes"])
	}
}

// A job leased to a worker that has gone is counted by the gateway as running, not queued. The
// waker's demand is awaitingWorker, which includes it; an older gateway without the field falls back
// to queued.
func TestAwaitingJobsIncludeOrphanedLeasesAndFallBackToQueued(t *testing.T) {
	s := scalingOf(2, 0, 1, 1, 0, 0, 0)
	s.Orphaned, s.AwaitingWorker = 1, 1
	if got := Decide(s, 0, 0, 1).Awaiting; got != 1 {
		t.Fatalf("an orphaned lease must be demand, got %d", got)
	}
	old := scalingOf(2, 3, 0, 1, 0, 0, 0)
	if got := Decide(old, 0, 0, 1).Awaiting; got != 3 {
		t.Fatalf("a gateway without awaitingWorker must fall back to queued, got %d", got)
	}
	running := scalingOf(2, 0, 1, 1, 1, 1, 1)
	if got := Decide(running, 0, 0, 1).Awaiting; got != 0 {
		t.Fatalf("a job a worker holds is not demand, got %d", got)
	}
}
