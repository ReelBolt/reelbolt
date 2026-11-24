package poolwaker

import (
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// Ledger is the persisted daily spend estimate (EUR, ex VAT). A started hour
// counts in full and is charged when the node is created. It survives restart
// and rolls over at the UTC day boundary.
type Ledger struct {
	mu   sync.Mutex
	path string
	st   ledgerState
}

type ledgerState struct {
	Day        string  `json:"day"`
	SpentEUR   float64 `json:"spentEur"`
	AlertedDay string  `json:"alertedDay,omitempty"`
}

func utcDay(t time.Time) string { return t.UTC().Format("2006-01-02") }

// OpenLedger loads the ledger at path (missing file is an empty ledger).
func OpenLedger(path string, now time.Time) (*Ledger, error) {
	l := &Ledger{path: path}
	b, err := os.ReadFile(path)
	switch {
	case errors.Is(err, os.ErrNotExist):
	case err != nil:
		return nil, fmt.Errorf("read ledger: %w", err)
	default:
		if err := json.Unmarshal(b, &l.st); err != nil {
			return nil, fmt.Errorf("ledger %s is corrupt: %w", path, err)
		}
	}
	l.rollLocked(now)
	return l, nil
}

func (l *Ledger) rollLocked(now time.Time) {
	if d := utcDay(now); l.st.Day != d {
		l.st = ledgerState{Day: d}
	}
}

// Spent returns today's estimate.
func (l *Ledger) Spent(now time.Time) float64 {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.rollLocked(now)
	return l.st.SpentEUR
}

// CanSpend reports whether cost fits under cap today.
func (l *Ledger) CanSpend(now time.Time, cost, cap float64) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.rollLocked(now)
	return l.st.SpentEUR+cost <= cap+1e-9
}

// Charge adds cost and persists. It reports whether the alert fraction was
// crossed for the first time today.
func (l *Ledger) Charge(now time.Time, cost, cap, alertFraction float64) (alert bool, err error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.rollLocked(now)
	l.st.SpentEUR = math.Round((l.st.SpentEUR+cost)*1e6) / 1e6
	if cap > 0 && l.st.SpentEUR >= cap*alertFraction && l.st.AlertedDay != l.st.Day {
		l.st.AlertedDay = l.st.Day
		alert = true
	}
	return alert, l.saveLocked()
}

func (l *Ledger) saveLocked() error {
	if l.path == "" {
		return nil
	}
	b, err := json.Marshal(l.st)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(l.path), 0o750); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(filepath.Dir(l.path), ".ledger-*")
	if err != nil {
		return err
	}
	name := tmp.Name()
	if _, err := tmp.Write(b); err != nil {
		_ = tmp.Close()
		_ = os.Remove(name)
		return err
	}
	if err := tmp.Sync(); err != nil {
		_ = tmp.Close()
		_ = os.Remove(name)
		return err
	}
	if err := tmp.Close(); err != nil {
		_ = os.Remove(name)
		return err
	}
	return os.Rename(name, l.path)
}
