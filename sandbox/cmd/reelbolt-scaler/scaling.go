package main

import (
	"errors"
	"fmt"
	"strconv"
	"strings"
)

// This file is the decision half of the scaling hook: a pure function over the
// gateway's answer, plus the Prometheus reader that cross-checks it.
//
// WHAT THIS IS NOT. It is not F12. There is no cloud credential in this
// repository and no provider API to call, so this command computes the node
// count an autoscaler should ask its provider for, prints it, and — only when
// told to — hands it to a command the operator supplies. What F12 must add on
// top of this is exactly one thing: a provider, i.e. POOL_SCALE_COMMAND replaced
// by a real "set the node count of this instance pool" call (and the IAM, the
// cooldown and the cloud-init that makes a new node join). Everything upstream
// of that call — which number, from which signal, and which ceiling — is here,
// and it is the part that has to be right whatever the provider turns out to be.

// scalingResponse mirrors GET /internal/v1/scaling (D5c). It is deliberately a
// mirror and not an import: the gateway is package main, so nothing can import
// its type. TestScalingResponseMatchesTheGateway is the drift guard — it reads
// the gateway's own source and fails when a field this client reads is not in
// it.
type scalingResponse struct {
	DesiredCloudPoolSlots  int    `json:"desiredCloudPoolSlots"`
	Queued                 int64  `json:"queued"`
	Orphaned               int64  `json:"orphaned"`
	AwaitingWorker         int64  `json:"awaitingWorker"`
	Running                int64  `json:"running"`
	Buffer                 int    `json:"buffer"`
	ConnectedCloudPool     int    `json:"connectedCloudPool"`
	AvailableCloudPool     int    `json:"availableCloudPool"`
	FreeSlotsCloudPool     int    `json:"freeSlotsCloudPool"`
	FreeSlotsOrgRunner     int    `json:"freeSlotsOrgRunner"`
	RunnersWithoutJobTypes int    `json:"runnersWithoutJobTypes"`
	DrainingRunners        int    `json:"drainingRunners"`
	GatewayInstance        string `json:"gatewayInstance"`
}

// Decision is one scaling round's answer.
//
// Nodes is what F12 hands its provider. SlotsPerWorker and DesiredSlots are
// carried so the log line and the JSON explain the number rather than asserting
// it, and CappedBy names which rule produced it — the question an operator asks
// when the pool will not grow.
type Decision struct {
	DesiredSlots int   `json:"desiredSlots"`
	SlotsPerNode int   `json:"slotsPerNode"`
	Nodes        int   `json:"nodes"`
	MinNodes     int   `json:"minNodes"`
	MaxNodes     int   `json:"maxNodes"`
	RunningNodes int   `json:"runningNodes"`
	Queued       int64 `json:"queued"`
	// Awaiting is every job waiting for a pool worker: Queued (first-time, requeued
	// after a failure or a lost lease) plus jobs leased to a worker that is gone.
	// It is the waker's demand; Queued alone misses the second kind.
	Awaiting    int64    `json:"awaiting"`
	Running     int64    `json:"running"`
	Buffer      int      `json:"buffer"`
	Connected   int      `json:"connectedCloudPool"`
	Available   int      `json:"availableCloudPool"`
	FreeSlots   int      `json:"freeSlotsCloudPool"`
	CappedBy    string   `json:"cappedBy"`
	GatewayInst string   `json:"gatewayInstance"`
	Warnings    []string `json:"warnings,omitempty"`
}

// CappedBy values.
const (
	cappedByDesired = "desired"
	cappedByRunning = "running"
	cappedByMin     = "min"
	cappedByMax     = "max"
)

// awaitingJobs reads the gateway's awaitingWorker, or Queued from a gateway that
// predates it (an older gateway never reports orphaned leases).
func awaitingJobs(s scalingResponse) int64 {
	if s.AwaitingWorker > s.Queued {
		return s.AwaitingWorker
	}
	return s.Queued
}

// Decide turns the gateway's answer into a node count.
//
// The slot number is D5c's (queued + running + buffer) and is never recomputed
// here: a scaler that derived its own would be a second formula for the same
// thing, and the two would disagree the first time one of them changed. What
// this adds is the translation from slots to nodes, and the two rules that keep
// the translation from killing work:
//
//   - never below what is RUNNING. Scaling a node away under a job that is
//     already running is a failed job, not a saving, so runningNodes is a floor
//     whatever the ceiling says.
//   - a ceiling that is below that floor is reported, not obeyed. An operator
//     whose POOL_MAX_NODES is smaller than the fleet in use has a configuration
//     problem; silently terminating renders would hide it.
func Decide(s scalingResponse, minNodes, maxNodes, slotsPerNode int) Decision {
	if slotsPerNode < 1 {
		slotsPerNode = 1
	}
	d := Decision{
		DesiredSlots: s.DesiredCloudPoolSlots,
		SlotsPerNode: slotsPerNode,
		MinNodes:     minNodes,
		MaxNodes:     maxNodes,
		Queued:       s.Queued,
		Awaiting:     awaitingJobs(s),
		Running:      s.Running,
		Buffer:       s.Buffer,
		Connected:    s.ConnectedCloudPool,
		Available:    s.AvailableCloudPool,
		FreeSlots:    s.FreeSlotsCloudPool,
		GatewayInst:  s.GatewayInstance,
	}

	desiredNodes := ceilDiv(s.DesiredCloudPoolSlots, slotsPerNode)
	d.RunningNodes = ceilDiv(int(s.Running), slotsPerNode)

	d.Nodes, d.CappedBy = desiredNodes, cappedByDesired
	if d.RunningNodes > d.Nodes {
		d.Nodes, d.CappedBy = d.RunningNodes, cappedByRunning
	}
	if minNodes > 0 && d.Nodes < minNodes {
		d.Nodes, d.CappedBy = minNodes, cappedByMin
	}
	if maxNodes > 0 && d.Nodes > maxNodes {
		if maxNodes < d.RunningNodes {
			d.Warnings = append(d.Warnings, fmt.Sprintf(
				"POOL_MAX_NODES=%d is below the %d node(s) already running; the cap is NOT being applied, "+
					"because scaling below running work kills jobs rather than saving money",
				maxNodes, d.RunningNodes))
		} else {
			d.Nodes, d.CappedBy = maxNodes, cappedByMax
		}
	}

	// Warnings are the difference between a number an operator can act on and a
	// number they have to take on faith. Each one is a state D5c already
	// distinguishes on purpose.
	if s.ConnectedCloudPool > 0 && s.AvailableCloudPool == 0 {
		d.Warnings = append(d.Warnings, fmt.Sprintf(
			"%d pool worker(s) are connected but %d can take work: they advertise no job types (no ffmpeg, "+
				"or a runtime image that is not digest-pinned). Adding nodes will not drain the queue",
			s.ConnectedCloudPool, s.AvailableCloudPool))
	}
	if s.Queued > 0 && s.FreeSlotsCloudPool == 0 && s.AvailableCloudPool > 0 {
		d.Warnings = append(d.Warnings, fmt.Sprintf(
			"%d queued job(s) but no free slots on this gateway replica's workers: the pool is saturated at its "+
				"current size", s.Queued))
	}
	if s.DesiredCloudPoolSlots == 0 && s.Running > 0 {
		d.Warnings = append(d.Warnings, "the gateway reported running jobs with a desired slot count of 0; "+
			"the buffer is expected to keep that number at 1")
	}
	return d
}

// ceilDiv divides rounding up. Two slots on one node need two nodes, not one.
func ceilDiv(n, by int) int {
	if n <= 0 {
		return 0
	}
	if by < 1 {
		by = 1
	}
	return (n + by - 1) / by
}

// Env renders the decision as the environment the scale command receives. An
// operator's hook should not have to re-read the gateway to know what it is
// being asked to do, and the whole decision travels with the call so a logged
// hook invocation is a complete record of the round.
func (d Decision) Env() []string {
	return []string{
		"POOL_NODES=" + strconv.Itoa(d.Nodes),
		"POOL_DESIRED_SLOTS=" + strconv.Itoa(d.DesiredSlots),
		"POOL_SLOTS_PER_NODE=" + strconv.Itoa(d.SlotsPerNode),
		"POOL_RUNNING=" + strconv.FormatInt(d.Running, 10),
		"POOL_QUEUED=" + strconv.FormatInt(d.Queued, 10),
		"POOL_CONNECTED=" + strconv.Itoa(d.Connected),
		"POOL_AVAILABLE=" + strconv.Itoa(d.Available),
		"POOL_FREE_SLOTS=" + strconv.Itoa(d.FreeSlots),
		"POOL_CAPPED_BY=" + d.CappedBy,
	}
}

// MetricsSummary is the fleet-wide context read out of the gateway's Prometheus
// text endpoint.
//
// It exists because /internal/v1/scaling's free-slot and connection counts are
// PER REPLICA — the registry is in memory, and D5c says so — while the queue
// depth is database-wide. A scaler pointed at one replica therefore sees the
// whole queue and one pod's capacity, which is exactly the combination that
// makes it over-scale. The metrics are the fleet-wide number, so the hook reads
// them (D5b registered them; re-registering any of these names in the gateway
// panics, which is why this program only ever PARSES them) and reports the
// difference.
type MetricsSummary struct {
	QueueDepth float64 `json:"queueDepth"`
	FreeSlots  float64 `json:"freeSlots"`
	Series     int     `json:"series"`
}

// ParseMetrics extracts the two D5b gauges the scaling signal is made of.
//
// Hand-rolled rather than via a client library: this program must not register
// a collector at all (the gateway owns those names), and the two families are
// one line each in the exposition format.
func ParseMetrics(text string) MetricsSummary {
	depth, dn := sumGauge(text, "reelbolt_runner_queue_depth", map[string]string{"target": "CloudPool"})
	slots, sn := sumGauge(text, "reelbolt_runner_free_slots", map[string]string{"target": "CloudPool"})
	return MetricsSummary{QueueDepth: depth, FreeSlots: slots, Series: dn + sn}
}

// sumGauge sums every sample of one metric whose labels are a superset of
// match. It returns the sum and how many samples contributed.
func sumGauge(text, metric string, match map[string]string) (float64, int) {
	var (
		total  float64
		series int
	)
	for _, line := range strings.Split(text, "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		name, _, ok := cutMetricLine(line)
		if !ok || name != metric {
			continue
		}
		labels, value, ok := splitLabelsAndValue(line[len(name):])
		if !ok {
			continue
		}
		if !labelsMatch(labels, match) {
			continue
		}
		total += value
		series++
	}
	return total, series
}

// cutMetricLine splits "name{...} value" into the metric name and the rest.
func cutMetricLine(line string) (string, string, bool) {
	i := strings.IndexAny(line, "{ ")
	if i <= 0 {
		return "", "", false
	}
	return line[:i], line[i:], true
}

// splitLabelsAndValue parses "{a="b",c="d"} 12" (or " 12" with no labels).
func splitLabelsAndValue(rest string) (map[string]string, float64, bool) {
	labels := map[string]string{}
	rest = strings.TrimSpace(rest)
	if strings.HasPrefix(rest, "{") {
		end := strings.LastIndex(rest, "}")
		if end < 0 {
			return nil, 0, false
		}
		body := rest[1:end]
		rest = strings.TrimSpace(rest[end+1:])
		for _, pair := range splitLabelPairs(body) {
			eq := strings.Index(pair, "=")
			if eq <= 0 {
				continue
			}
			key := strings.TrimSpace(pair[:eq])
			value := strings.Trim(strings.TrimSpace(pair[eq+1:]), "\"")
			labels[key] = value
		}
	}
	fields := strings.Fields(rest)
	if len(fields) == 0 {
		return nil, 0, false
	}
	value, err := strconv.ParseFloat(fields[0], 64)
	if err != nil {
		return nil, 0, false
	}
	return labels, value, true
}

// splitLabelPairs splits on commas that are not inside a quoted value, which is
// how a label value containing a comma survives.
func splitLabelPairs(body string) []string {
	var (
		out   []string
		start int
		quote bool
	)
	for i := 0; i < len(body); i++ {
		switch body[i] {
		case '"':
			quote = !quote
		case ',':
			if !quote {
				out = append(out, body[start:i])
				start = i + 1
			}
		}
	}
	out = append(out, body[start:])
	return out
}

func labelsMatch(labels, match map[string]string) bool {
	for k, v := range match {
		if labels[k] != v {
			return false
		}
	}
	return true
}

// errNoDecision is returned when the gateway answered something this command
// cannot act on. It is separate from a transport failure because the two have
// different remedies: this one means "the gateway is older or newer than this
// scaler".
var errNoDecision = errors.New("the gateway did not report a desired CloudPool slot count")
