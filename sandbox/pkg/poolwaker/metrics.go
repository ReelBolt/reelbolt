package poolwaker

import (
	"fmt"
	"sort"
	"strings"
	"sync"
)

type metricSet struct {
	mu sync.Mutex
	v  map[string]float64
}

func newMetrics() *metricSet { return &metricSet{v: map[string]float64{}} }

func (m *metricSet) add(k string, d float64) {
	m.mu.Lock()
	m.v[k] += d
	m.mu.Unlock()
}

func (m *metricSet) set(k string, v float64) {
	m.mu.Lock()
	m.v[k] = v
	m.mu.Unlock()
}

// Text renders Prometheus exposition text.
func (m *metricSet) Text() string {
	m.mu.Lock()
	defer m.mu.Unlock()
	keys := make([]string, 0, len(m.v))
	for k := range m.v {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	var sb strings.Builder
	for _, k := range keys {
		fmt.Fprintf(&sb, "reelbolt_poolwaker_%s %g\n", k, m.v[k])
	}
	return sb.String()
}
