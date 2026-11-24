package daemon

import (
	"context"
	"encoding/json"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/caps"
	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/llmrelay"
)

// llmRefreshEvery is how often the shared model list is re-read.
const llmRefreshEvery = time.Minute

// LocalLLM is the relay serving the user's own model server. It is always
// present and serves nothing unless sharing is enabled.
func (d *Daemon) LocalLLM() *llmrelay.Relay { return d.llm }

// ApplyLocalLLM puts new sharing settings into effect without a restart. The
// caller persists them (llmrelay.SaveSettings). When the advertised model list
// changes and no job is running, the runner reconnects so the gateway learns of
// it at the next hello.
func (d *Daemon) ApplyLocalLLM(s config.LocalLLM) error {
	if err := d.cfg.SetLocalLLM(s); err != nil {
		return err
	}
	if err := d.llm.Apply(s); err != nil {
		return err
	}
	go d.syncLocalLLM(context.Background())
	return nil
}

// helloCapabilities is the capability document for one hello.
func (d *Daemon) helloCapabilities() protocol.Capabilities {
	doc := caps.WithLocalLLM(d.probe.Capabilities, d.llm.Capabilities())
	d.llmMu.Lock()
	d.advertisedLLM = llmKey(doc.LocalLLM)
	d.llmMu.Unlock()
	return doc
}

func llmKey(l *protocol.LocalLLMCapabilities) string {
	if l == nil {
		return ""
	}
	raw, _ := json.Marshal(l)
	return string(raw)
}

// syncLocalLLM refreshes the model list and, when what would be advertised
// differs from the last hello and the runner is idle, asks for a reconnect.
func (d *Daemon) syncLocalLLM(ctx context.Context) {
	if d.llm.Settings().Enabled {
		rctx, cancel := context.WithTimeout(ctx, 5*time.Second)
		_, err := d.llm.RefreshModels(rctx)
		cancel()
		if err != nil {
			d.logf("local model: server not reachable")
		}
	}
	d.llmMu.Lock()
	advertised := d.advertisedLLM
	d.llmMu.Unlock()
	if llmKey(caps.WithLocalLLM(d.probe.Capabilities, d.llm.Capabilities()).LocalLLM) == advertised || d.Busy() {
		return
	}
	d.logf("local model: list changed, reconnecting to advertise it")
	select {
	case d.reconnect <- struct{}{}:
	default:
	}
}

// llmLoop keeps the model list fresh while the daemon runs.
func (d *Daemon) llmLoop(ctx context.Context) {
	ticker := time.NewTicker(llmRefreshEvery)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			if d.llm.ModelsStale() {
				d.syncLocalLLM(ctx)
			}
		}
	}
}

// primeLocalLLM reads the model list before the first hello so it is advertised
// from the start. It never blocks startup for long.
func (d *Daemon) primeLocalLLM(ctx context.Context) {
	if !d.llm.Settings().Enabled {
		return
	}
	rctx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	_, _ = d.llm.RefreshModels(rctx)
}
