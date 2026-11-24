package poolwaker

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// UsageRecord is one JSONL line per ended node (plan.md contract).
type UsageRecord struct {
	Node         string     `json:"node"`
	Provider     string     `json:"provider"`
	ServerType   string     `json:"serverType"`
	Instance     string     `json:"instance"`
	CreatedAt    time.Time  `json:"createdAt"`
	ReadyAt      *time.Time `json:"readyAt"`
	EndedAt      time.Time  `json:"endedAt"`
	NodeSeconds  int64      `json:"nodeSeconds"`
	BilledHours  int        `json:"billedHours"`
	EstimatedEUR float64    `json:"estimatedEUR"`
	JobSeconds   *float64   `json:"jobSeconds"`
	Reason       string     `json:"reason"`
}

// UsageWriter appends records to a JSONL file ("" disables it).
type UsageWriter struct {
	mu   sync.Mutex
	path string
}

// NewUsageWriter returns a writer for path.
func NewUsageWriter(path string) *UsageWriter { return &UsageWriter{path: path} }

// Write appends one record.
func (w *UsageWriter) Write(r UsageRecord) error {
	if w == nil || w.path == "" {
		return nil
	}
	b, err := json.Marshal(r)
	if err != nil {
		return err
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	if err := os.MkdirAll(filepath.Dir(w.path), 0o750); err != nil {
		return err
	}
	f, err := os.OpenFile(w.path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o640)
	if err != nil {
		return err
	}
	_, werr := f.Write(append(b, '\n'))
	if cerr := f.Close(); werr == nil {
		werr = cerr
	}
	return werr
}
