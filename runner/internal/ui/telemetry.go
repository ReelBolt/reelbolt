package ui

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/daemon"
	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
)

// This file is E11's half of the shell: the privacy panel, the actions a user
// takes from it, and the one place the runner's own facts are turned into a
// telemetry event.
//
// Three rules hold it together, and they are the same three the telemetry
// package states in its own doc comment:
//
//   - The switch is OFF unless runner.json says otherwise, and the shell never
//     writes that file itself: SetTelemetry goes through the same
//     validate-then-save path every other setting does.
//   - The shell hands the reporter FACTS, never text. telemetryEnv reads the
//     probe's runtime kind and this build's version; a job failure is reported
//     by its protocol class and code and never by its message, which the
//     observer is not even given (see daemon.JobEvent.Class).
//   - Nothing here talks to a network. The reporter does, through its own
//     transport, and only when the switch is on and an endpoint is compiled in.

// TelemetryView is the Settings screen's privacy panel.
type TelemetryView struct {
	// Enabled is the user's answer, read from the live configuration.
	Enabled bool `json:"enabled"`
	// Asked is true once the first-run wizard has asked, so it asks once.
	Asked bool `json:"asked"`
	// Available is false when this build has no collector address compiled in.
	// Release builds stamp one; a build without it says so rather than leaving
	// the user to wonder why the counter stays at zero.
	Available bool   `json:"available"`
	Endpoint  string `json:"endpoint,omitempty"`
	// ConsentVersion is the disclosure that was accepted.
	ConsentVersion string `json:"consentVersion,omitempty"`
	// Disclosure is the promise, taken verbatim from the telemetry package so
	// the screen cannot render a different one.
	Disclosure string `json:"disclosure"`
	Sent       int64  `json:"sent"`
	// Unsent counts reports built with the switch on but no collector
	// configured: nothing was attempted, which is not the same as a failure.
	Unsent    int64  `json:"unsent"`
	Dropped   int64  `json:"dropped"`
	Failed    int64  `json:"failed"`
	LastError string `json:"lastError,omitempty"`
	// LogsDir is where the rotating log lives, and what the diagnostics export
	// attaches.
	LogsDir string `json:"logsDir,omitempty"`
	// Note explains the state in one sentence.
	Note string `json:"note,omitempty"`
}

// TelemetryReporter is the reporter the shell owns. It is never nil: a build
// that never enables the feature gets one whose switch reads the configuration
// and answers "off".
func (s *Shell) TelemetryReporter() *telemetry.Reporter {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.telemetry
}

// config is the live configuration. The shell's daemon and the Settings screen
// are looking at one machine, so they read one value.
func (s *Shell) config() *config.Config {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.opts.Config
}

// telemetryEnv is what the reporter is allowed to know about this machine.
func (s *Shell) telemetryEnv() telemetry.Env {
	s.mu.Lock()
	probe := s.probe
	s.mu.Unlock()
	env := telemetry.Env{
		Version:   s.opts.Version,
		OS:        runtime.GOOS,
		Arch:      runtime.GOARCH,
		GoVersion: runtime.Version(),
	}
	if probe != nil && probe.Runtime != nil {
		env.RuntimeKind = string(probe.Runtime.Candidate.Kind)
		env.RuntimeMode = string(probe.Runtime.Mode)
	}
	return env
}

// telemetryView assembles the panel.
func (s *Shell) telemetryView() TelemetryView {
	rep := s.TelemetryReporter()
	stats := rep.Stats()
	tel := s.config().Telemetry
	dir, _ := telemetry.LogsDir()
	v := TelemetryView{
		Enabled:        tel.On(),
		Asked:          tel.Asked(),
		Available:      stats.Available,
		Endpoint:       stats.Endpoint,
		ConsentVersion: tel.ConsentVersion,
		Disclosure:     telemetry.Disclosure,
		Sent:           stats.Sent,
		Unsent:         stats.Unsent,
		Dropped:        stats.Dropped,
		Failed:         stats.Failed,
		LastError:      stats.LastError,
		LogsDir:        dir,
	}
	v.Note = telemetryNote(v)
	return v
}

// telemetryNote is the one sentence under the switch, and it says the truest
// thing available: off, on-and-working, or on-and-unable-to-send.
func telemetryNote(v TelemetryView) string {
	switch {
	case !v.Enabled:
		return "Off. Nothing is sent. ReelBolt works exactly the same either way."
	case !v.Available:
		return "On. Crash reports will be sent once this version can reach ReelBolt."
	default:
		return "On. A short report is sent only if ReelBolt crashes or can't start a job."
	}
}

// SetTelemetry records the user's answer.
//
// It writes runner.json through the same validation the command line uses, so
// nothing here can produce a configuration the daemon would refuse to load.
func (s *Shell) SetTelemetry(on bool) error {
	s.mu.Lock()
	cfg := s.opts.Config
	next := *cfg
	s.mu.Unlock()

	next.Telemetry.Set(on, s.now(), telemetry.ConsentVersion)
	if err := next.Validate(); err != nil {
		s.log.Printf("telemetry: refused: %v", err)
		return sayWrap("That setting isn't allowed: "+err.Error()+".", err)
	}
	if err := s.saveConfig(&next); err != nil {
		s.log.Printf("telemetry: write %s: %v", s.ConfigPath(), err)
		return sayWrap("Couldn't save your settings. Check that your user folder isn't full, then try again.", err)
	}
	s.mu.Lock()
	s.opts.Config = &next
	s.mu.Unlock()
	s.notify()
	return nil
}

// MarkTelemetryAsked records that the first-run wizard asked, without changing
// the answer. It is what makes "ask once" true.
func (s *Shell) MarkTelemetryAsked() error {
	s.mu.Lock()
	cfg := s.opts.Config
	if cfg.Telemetry.Asked() {
		s.mu.Unlock()
		return nil
	}
	next := *cfg
	s.mu.Unlock()

	next.Telemetry.MarkAsked(s.now())
	if err := s.saveConfig(&next); err != nil {
		s.log.Printf("telemetry: write %s: %v", s.ConfigPath(), err)
		return sayWrap("Couldn't save your settings. Check that your user folder isn't full, then try again.", err)
	}
	s.mu.Lock()
	s.opts.Config = &next
	s.mu.Unlock()
	s.notify()
	return nil
}

// TelemetryPreview is the document a user reads before deciding.
func (s *Shell) TelemetryPreview() telemetry.Preview {
	return s.TelemetryReporter().Preview()
}

// observeJobFailure is the shell's refusal observer.
//
// It is a SEPARATE stream from the ledger's (see daemon/jobfailures.go), and it
// reports one thing: a job this runner could not start. A finished job carries
// no verdict on this machine - the gateway owns that - so nothing about a
// completed render can ever be reported from here. The reporter itself checks
// the switch; this method does not have to.
func (s *Shell) observeJobFailure(ev daemon.JobFailure) {
	s.TelemetryReporter().ReportJobFailure(ev.JobType, ev.Class, ev.Code)
}

// ExportDiagnostics writes the bundle the Settings screen's button produces.
//
// It is local, it is synchronous, and it returns the path so the screen can
// name the file rather than saying "done". Everything textual in the bundle
// goes through the same scrubber the acceptance criteria test.
func (s *Shell) ExportDiagnostics() (telemetry.DiagnosticsResult, error) {
	var res telemetry.DiagnosticsResult
	dir := s.DiagnosticsDir()
	name := "reelbolt-diagnostics-" + s.now().UTC().Format("20060102T150405Z") + ".zip"

	scrub := telemetry.NewScrubber()
	bundle := telemetry.Diagnostics{
		App:      "ReelBolt runner",
		Version:  s.opts.Version,
		Summary:  s.diagnosticsSummary(),
		Scan:     s.diagnosticsCapabilities(),
		Scrubber: scrub,
		Now:      s.now,
	}
	if dir == "" {
		return res, say("There's no folder on this computer to save a diagnostics report in.")
	}
	if cfg := s.config(); cfg != nil {
		// The device name, the device id and the workspace id are this
		// machine's own identifiers, and the export names them where it is
		// useful. They are also the three strings most worth guaranteeing are
		// gone from the log, so the scrubber is told about them explicitly.
		scrub.WithNames(cfg.DeviceName, cfg.DeviceID, cfg.OrganizationID)
	}
	if path, err := config.Path(); err == nil {
		if raw, err := os.ReadFile(path); err == nil {
			bundle.Config = raw
		}
	}
	if logs, err := s.logFiles(); err == nil {
		bundle.Logs = logs
	}
	return telemetry.WriteDiagnosticsZip(filepath.Join(dir, name), bundle)
}

// DiagnosticsDir is where an export is written and where "Open the folder"
// points. One method, so the button and the file cannot disagree.
//
// It never fails: a machine whose cache directory cannot be determined has no
// export directory, and the button says so through the error an export returns
// rather than through a path this would have to invent.
func (s *Shell) DiagnosticsDir() string {
	if s.opts.DiagnosticsDir != "" {
		return s.opts.DiagnosticsDir
	}
	dir, err := telemetry.DefaultDiagnosticsDir()
	if err != nil {
		return ""
	}
	return dir
}

// logFiles is the log the diagnostics export attaches.
//
// The shell may own the log (it opened a rotating writer because nobody handed
// it a logger) or the HOST may (the desktop binary opens desktop.log and hands
// the shell a logger), so the fallback is the directory itself rather than
// nothing: a bundle with no log in it is the one thing this export exists to
// avoid.
func (s *Shell) logFiles() ([]string, error) {
	s.mu.Lock()
	w := s.logWriter
	s.mu.Unlock()
	if w != nil {
		return w.Files(), nil
	}
	dir := s.opts.LogDir
	if dir == "" {
		dir, _ = telemetry.LogsDir()
	}
	return telemetry.ListLogs(dir), nil
}

// diagnosticsSummary is the machine's own facts, as the export's summary.json.
//
// It is assembled from the same values every other screen renders, so a support
// engineer and a user are looking at one answer. It carries the identifiers the
// telemetry payload deliberately does NOT, because this file is read by the
// person who owns them - and the scrubber is told about them so a log line that
// quotes one is redacted anyway.
func (s *Shell) diagnosticsSummary() []byte {
	s.mu.Lock()
	cfg := s.opts.Config
	probe := s.probe
	probeErr := s.probeErr
	running := s.running
	runErr := s.runErr
	revoked := s.revoked
	s.mu.Unlock()

	summary := map[string]any{
		"generatedBy": "reelbolt-runner-desktop",
		"version":     s.opts.Version,
		"os":          runtime.GOOS,
		"arch":        runtime.GOARCH,
		"go":          runtime.Version(),
		"cores":       runtime.NumCPU(),
		"configPath":  s.ConfigPath(),
		"paired":      cfg.IsPaired(),
		"deviceName":  cfg.DeviceName,
		"deviceId":    cfg.DeviceID,
		"workspaceId": cfg.OrganizationID,
		"gatewayUrl":  cfg.GatewayURL,
		"keyStore":    s.opts.Keys.Describe(),
		"slots":       cfg.Slots,
		"limits":      cfg.Limits,
		"schedule":    cfg.Schedule,
		"update": map[string]any{
			"channel":    cfg.UpdateChannelOr(),
			"autoUpdate": cfg.UpdatesEnabled(),
		},
		"telemetry": map[string]any{
			"enabled":        cfg.Telemetry.On(),
			"asked":          cfg.Telemetry.Asked(),
			"consentVersion": cfg.Telemetry.ConsentVersion,
			"available":      s.TelemetryReporter().Available(),
		},
		"runner": map[string]any{
			"running": running,
			"error":   runErr,
			"revoked": revoked,
		},
	}
	if probe != nil {
		summary["runtime"] = runtimeView(*probe, cfg.Slots)
		summary["probe"] = map[string]any{
			"containerCli": probe.ContainerCLI,
			"image":        probe.Image.Ref,
			"imageOrigin":  probe.Image.Origin,
		}
	} else if probeErr != "" {
		summary["probe"] = map[string]any{"error": probeErr}
	}
	raw, err := json.MarshalIndent(summary, "", "  ")
	if err != nil {
		return []byte("{\"error\":\"the summary could not be serialised\"}")
	}
	return raw
}

// diagnosticsCapabilities is the probe's own capability document, when there is
// one. It is the same document the runner advertises to the gateway, so a
// support engineer reads what the platform read.
func (s *Shell) diagnosticsCapabilities() []byte {
	caps, ok := s.Capabilities()
	if !ok {
		return nil
	}
	raw, err := json.MarshalIndent(caps, "", "  ")
	if err != nil {
		return nil
	}
	return raw
}
