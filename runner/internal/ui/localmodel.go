package ui

import (
	"context"
	"errors"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/llmrelay"
)

// LocalModelHost is what the "Local model" screen needs from the runner. It is
// a small interface so tests can fake it; the real one is shellLocalModel,
// which reaches the running daemon's relay.
//
// The model server is the person's own. ReelBolt only relays requests to it
// over the runner's existing outbound connection; nothing here opens a port.
type LocalModelHost interface {
	// Status is the relay's live counters and last known models.
	Status() llmrelay.Status
	// Settings is the saved sharing setting.
	Settings() (config.LocalLLM, error)
	// Detect looks for model servers on this computer.
	Detect(ctx context.Context) []llmrelay.Detected
	// Save stores the setting and applies it to the running relay.
	Save(s config.LocalLLM) error
	// Refresh re-reads the model list from the configured server.
	Refresh(ctx context.Context) error
}

// LocalModelDoc is the Local model screen's document.
type LocalModelDoc struct {
	Available       bool            `json:"available"`
	Enabled         bool            `json:"enabled"`
	BaseURL         string          `json:"baseUrl"`
	AllowRemoteHost bool            `json:"allowRemoteHost"`
	Status          llmrelay.Status `json:"status"`
}

// LocalModelPatch is a save from the screen. Consent must be true whenever
// sharing is being switched on from off: the switch is never a silent default.
type LocalModelPatch struct {
	Enabled         bool   `json:"enabled"`
	BaseURL         string `json:"baseUrl"`
	AllowRemoteHost bool   `json:"allowRemoteHost"`
	Consent         bool   `json:"consent"`
}

// LocalModelResult is the answer to a save, a check or a detect.
type LocalModelResult struct {
	OK      bool                `json:"ok"`
	Message string              `json:"message,omitempty"`
	Doc     LocalModelDoc       `json:"doc"`
	Servers []llmrelay.Detected `json:"servers,omitempty"`
}

const (
	localModelUnavailable = "Sharing a local model isn't available in this window."
	localModelConsentMsg  = "Please confirm that you're happy for your workspace's prompts to be sent to the model on this computer."
	localModelSavedMsg    = "Saved."
	localModelOffMsg      = "Saved. Sharing is off."
	localModelDetectTO    = 8 * time.Second
	localModelCheckTO     = 15 * time.Second
)

func (a *App) localModel() LocalModelHost {
	if a.opts.LocalModel != nil {
		return a.opts.LocalModel
	}
	return shellLocalModel{a.shell}
}

// LocalModel is the screen's current document.
func (a *App) LocalModel() LocalModelDoc {
	h := a.localModel()
	set, err := h.Settings()
	if err != nil {
		return LocalModelDoc{Available: false}
	}
	st := h.Status()
	if st.BaseURL == "" {
		st.BaseURL = set.BaseURLOr()
	}
	return LocalModelDoc{
		Available: true, Enabled: set.Enabled, BaseURL: set.BaseURLOr(),
		AllowRemoteHost: set.AllowRemoteHost, Status: st,
	}
}

// DetectLocalModels looks for servers on this computer.
func (a *App) DetectLocalModels(ctx context.Context) LocalModelResult {
	ctx, cancel := context.WithTimeout(ctx, localModelDetectTO)
	defer cancel()
	found := a.localModel().Detect(ctx)
	if found == nil {
		found = []llmrelay.Detected{}
	}
	res := LocalModelResult{OK: true, Doc: a.LocalModel(), Servers: found}
	if len(found) == 0 {
		res.Message = "No model server was found on this computer. Start yours, or type its address."
	}
	return res
}

// SaveLocalModel validates and applies a change.
func (a *App) SaveLocalModel(p LocalModelPatch) LocalModelResult {
	h := a.localModel()
	fail := func(msg string) LocalModelResult {
		return LocalModelResult{OK: false, Message: msg, Doc: a.LocalModel()}
	}
	cur, err := h.Settings()
	if err != nil {
		return fail(localModelUnavailable)
	}
	raw := strings.TrimSpace(p.BaseURL)
	if raw == "" {
		raw = config.DefaultLocalLLMBaseURL
	}
	if _, err := config.ParseLocalLLMBase(raw, p.AllowRemoteHost); err != nil {
		return fail(localModelAddressMessage(err))
	}
	if p.Enabled && !cur.Enabled && !p.Consent {
		return fail(localModelConsentMsg)
	}
	next := cur
	next.Enabled, next.BaseURL, next.AllowRemoteHost = p.Enabled, raw, p.AllowRemoteHost
	if err := h.Save(next); err != nil {
		if isPlain(err) {
			return fail(err.Error())
		}
		return fail("Those settings couldn't be saved. Check the address and try again.")
	}
	msg := localModelSavedMsg
	if !next.Enabled {
		msg = localModelOffMsg
	}
	return LocalModelResult{OK: true, Message: msg, Doc: a.LocalModel()}
}

// CheckLocalModel re-reads the model list and says whether the server answered.
func (a *App) CheckLocalModel(ctx context.Context) LocalModelResult {
	ctx, cancel := context.WithTimeout(ctx, localModelCheckTO)
	defer cancel()
	h := a.localModel()
	if set, err := h.Settings(); err == nil && !set.Enabled {
		return LocalModelResult{OK: false, Message: "Sharing is off. Turn it on to check the model.", Doc: a.LocalModel()}
	}
	if err := h.Refresh(ctx); err != nil {
		return LocalModelResult{OK: false, Message: "Couldn't reach a model server at that address. Make sure it's running.", Doc: a.LocalModel()}
	}
	doc := a.LocalModel()
	if len(doc.Status.Models) == 0 {
		return LocalModelResult{OK: true, Message: "The server answered, but it lists no models yet.", Doc: doc}
	}
	return LocalModelResult{OK: true, Message: "Connected.", Doc: doc}
}

// localModelAddressMessage turns a parse error into a sentence for a person.
func localModelAddressMessage(err error) string {
	m := err.Error()
	switch {
	case strings.Contains(m, "allowRemoteHost"):
		return "That address is on another computer. Tick \"My model runs on another computer on my network\" to use it."
	case strings.Contains(m, "credentials"):
		return "The address can't contain a user name or password."
	case strings.Contains(m, "query or fragment"):
		return "The address should end at the port, with nothing after a ? or #."
	case strings.Contains(m, "http://"):
		return "The address should start with http:// or https://, for example http://127.0.0.1:11434."
	case strings.Contains(m, "needs a host"):
		return "The address is missing a computer name, for example http://127.0.0.1:11434."
	}
	return "That address isn't valid. Try something like http://127.0.0.1:11434."
}

// shellLocalModel is the real host: the running daemon's relay when the runner
// is up, and the saved file when it is not.
type shellLocalModel struct{ s *Shell }

func (h shellLocalModel) Status() llmrelay.Status {
	if d := h.s.currentDaemon(); d != nil && d.LocalLLM() != nil {
		return d.LocalLLM().Status()
	}
	set, _ := llmrelay.GetSettings()
	return llmrelay.Status{Enabled: set.Enabled, BaseURL: set.BaseURLOr()}
}

func (h shellLocalModel) Settings() (config.LocalLLM, error) { return llmrelay.GetSettings() }

func (h shellLocalModel) Detect(ctx context.Context) []llmrelay.Detected {
	return llmrelay.Detect(ctx)
}

func (h shellLocalModel) Save(s config.LocalLLM) error {
	if err := llmrelay.SaveSettings(s); err != nil {
		return err
	}
	if d := h.s.currentDaemon(); d != nil {
		return d.ApplyLocalLLM(s)
	}
	return nil
}

func (h shellLocalModel) Refresh(ctx context.Context) error {
	d := h.s.currentDaemon()
	if d == nil || d.LocalLLM() == nil {
		return errors.New("runner not running")
	}
	_, err := d.LocalLLM().RefreshModels(ctx)
	return err
}
