package ui

import (
	"strings"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
)

// SettingsPatch is a change to runner.json from the window.
//
// It is a PATCH and not the whole document, on purpose. The file is the user's:
// it may carry dev hosts and options this build does not show, and a window
// that round-tripped the whole structure would silently drop whatever the
// running version does not know about.
type SettingsPatch struct {
	// Slots is how many jobs run at once (1..config.MaxSlots).
	Slots *int `json:"slots,omitempty"`
	// UpdateChannel is "stable" or "beta".
	UpdateChannel *string `json:"updateChannel,omitempty"`
	// AutoUpdate turns the background release check off. The daemon the DESKTOP
	// shell runs never installs an updater (see shell.go), so this governs the
	// headless runner; the window still shows it, because one machine has one
	// configuration.
	AutoUpdate *bool `json:"autoUpdate,omitempty"`
	// Autostart adds or removes the login item. It is not part of runner.json.
	Autostart *bool `json:"autostart,omitempty"`
	// Telemetry turns crash reporting on or off (E11). It is a full consent
	// record rather than a bare flag: the answer, the time it was given and the
	// disclosure version that was accepted all live in runner.json, so "asked
	// once" is a fact the file can prove.
	Telemetry *bool `json:"telemetry,omitempty"`
	// Limits is a PARTIAL change to the per-job limits: every non-zero field
	// replaces the current value, and a zero (or absent) field keeps it.
	Limits *governor.LimitsConfig `json:"limits,omitempty"`
	// Schedule REPLACES the schedule. It is one structured value (a mode, an
	// idle time, time windows, a battery rule), so a partial merge of it would
	// be a guess about which half the person meant.
	Schedule *governor.Schedule `json:"schedule,omitempty"`
	// MediaCacheMaxBytes is the cap on the downloaded-media cache.
	MediaCacheMaxBytes *int64 `json:"mediaCacheMaxBytes,omitempty"`
}

// empty reports whether the patch changes nothing.
func (p SettingsPatch) empty() bool {
	return p.Slots == nil && p.UpdateChannel == nil && p.AutoUpdate == nil &&
		p.Autostart == nil && p.Telemetry == nil && p.Limits == nil &&
		p.Schedule == nil && p.MediaCacheMaxBytes == nil
}

// Messages the Settings screen shows after a save.
const (
	savedMessage        = "Saved."
	savedNextJobMessage = "Saved. It applies to the next job."
	savedNextRunMessage = "Saved. ReelBolt on this computer picks it up the next time it starts."
)

// minMediaCacheBytes is the smallest media-cache cap the window accepts. A cap
// smaller than one source file is a cache that never keeps anything; zero
// turns the cache off.
const minMediaCacheBytes = config.MinMediaCacheBytes

// SaveSettings applies a patch, validating the whole result before writing it,
// and returns the one sentence the window shows.
//
// Validation is the config package's own (config.Config.Validate), not a second
// copy of the rules: a change the command line would refuse has to be refused
// here too, or the two disagree about what a valid machine is. checkPatch only
// runs first so that the common mistakes come back as sentences a person can
// act on instead of field names.
//
// What happens next depends on the setting:
//
//   - The schedule is applied to the running runner at once.
//   - The per-job limits and the number of jobs at once are read when the
//     runner starts, so the in-process runner is restarted — never in the
//     middle of a job: with one running, the restart waits for it, and the
//     sentence says the change applies to the next job.
//   - A runner another process started reads the file when it next starts.
func (a *App) SaveSettings(p SettingsPatch) (string, error) {
	if p.empty() {
		return "", say("Nothing to save.")
	}
	if err := checkPatch(p); err != nil {
		return "", err
	}
	s := a.shell
	s.mu.Lock()
	cfg := s.opts.Config
	// A copy, so a rejected patch cannot leave the live configuration half
	// applied: the file and the running daemon must agree after this returns.
	next := *cfg
	if p.Slots != nil {
		next.Slots = *p.Slots
	}
	if p.UpdateChannel != nil {
		next.UpdateChannel = strings.ToLower(strings.TrimSpace(*p.UpdateChannel))
	}
	if p.AutoUpdate != nil {
		on := *p.AutoUpdate
		next.AutoUpdate = &on
	}
	if p.Telemetry != nil {
		// The consent record is written by the telemetry package's own Set, so
		// the version of the disclosure a user accepted is stamped in one place
		// and cannot drift from the text the screen showed.
		next.Telemetry.Set(*p.Telemetry, s.now(), telemetry.ConsentVersion)
	}
	if p.Limits != nil {
		next.Limits = mergeLimits(cfg.Limits, *p.Limits)
	}
	if p.MediaCacheMaxBytes != nil {
		next.MediaCache.SetMaxBytes(*p.MediaCacheMaxBytes)
	}
	if p.Schedule != nil {
		sched := *p.Schedule
		sched.Windows = append([]governor.Window(nil), p.Schedule.Windows...)
		sched.ApplyDefaults()
		next.Schedule = sched
	}
	s.mu.Unlock()

	if err := next.Validate(); err != nil {
		s.log.Printf("settings: refused: %v", err)
		return "", sayWrap("That setting isn't allowed: "+err.Error()+".", err)
	}

	if p.Autostart != nil {
		if err := a.setAutostart(*p.Autostart); err != nil {
			return "", err
		}
	}

	needsSave := p.Slots != nil || p.UpdateChannel != nil || p.AutoUpdate != nil ||
		p.Telemetry != nil || p.Limits != nil || p.Schedule != nil || p.MediaCacheMaxBytes != nil
	if needsSave {
		if err := s.saveConfig(&next); err != nil {
			s.log.Printf("settings: write %s: %v", s.ConfigPath(), err)
			return "", sayWrap("Couldn't save your settings. Check that your user folder isn't full, then try again.", err)
		}
		s.mu.Lock()
		s.opts.Config = &next
		s.mu.Unlock()
	}

	message := savedMessage
	if p.Schedule != nil {
		if d := s.currentDaemon(); d != nil {
			d.Governor().SetSchedule(next.Schedule)
		} else if _, other := s.liveRunner(); other {
			message = savedNextRunMessage
		}
	}
	restart := (p.Limits != nil && next.Limits != cfg.Limits) || (p.Slots != nil && next.Slots != cfg.Slots)
	if p.MediaCacheMaxBytes != nil {
		// The cap applies to the live cache at once; turning a cache on that
		// the runner started without needs the runner to restart to use it.
		if s.applyMediaCacheMax(cfg.MediaCache.EffectiveMaxBytes(), next.MediaCache.EffectiveMaxBytes()) {
			restart = true
		}
	}
	if restart {
		switch {
		case s.currentDaemon() != nil:
			if s.RestartRunnerWhenIdle() {
				message = savedNextJobMessage
			}
		default:
			if _, other := s.liveRunner(); other {
				message = savedNextRunMessage
			}
		}
	}
	s.notify()
	return message, nil
}

// setAutostart adds or removes the login item.
func (a *App) setAutostart(on bool) error {
	as := a.opts.Autostart
	if as == nil {
		return say("This computer can't start ReelBolt automatically when you sign in.")
	}
	if on {
		argv, err := AutostartCommand("")
		if err != nil {
			a.shell.log.Printf("autostart: %v", err)
			return sayWrap("Couldn't turn on starting ReelBolt when you sign in.", err)
		}
		if err := as.Enable(argv); err != nil {
			a.shell.log.Printf("autostart: %v", err)
			return sayWrap("Couldn't turn on starting ReelBolt when you sign in.", err)
		}
		return nil
	}
	if err := as.Disable(); err != nil {
		a.shell.log.Printf("autostart: %v", err)
		return sayWrap("Couldn't turn off starting ReelBolt when you sign in.", err)
	}
	return nil
}

// mergeLimits lays the non-zero fields of a patch over the current limits.
func mergeLimits(cur, patch governor.LimitsConfig) governor.LimitsConfig {
	out := cur
	if patch.Memory > 0 {
		out.Memory = patch.Memory
	}
	if patch.CPUs > 0 {
		out.CPUs = patch.CPUs
	}
	if patch.PIDs > 0 {
		out.PIDs = patch.PIDs
	}
	if patch.FFmpegThreads > 0 {
		out.FFmpegThreads = patch.FFmpegThreads
	}
	if patch.Priority != "" {
		out.Priority = governor.Priority(strings.ToLower(strings.TrimSpace(string(patch.Priority))))
	}
	if patch.DiskQuotaBytes > 0 {
		out.DiskQuotaBytes = patch.DiskQuotaBytes
	}
	return out
}

// checkPatch refuses the common mistakes with a sentence a person can act on.
// It is a front for config.Validate, not a replacement: anything it lets
// through is still validated there.
func checkPatch(p SettingsPatch) error {
	if p.Slots != nil && (*p.Slots < 1 || *p.Slots > config.MaxSlots) {
		return sayf("Jobs at once must be between 1 and %d.", config.MaxSlots)
	}
	if p.UpdateChannel != nil {
		switch strings.ToLower(strings.TrimSpace(*p.UpdateChannel)) {
		case "", "stable", "beta":
		default:
			return say("Choose Stable or Beta for updates.")
		}
	}
	if l := p.Limits; l != nil {
		if l.Memory < 0 || l.DiskQuotaBytes < 0 || l.CPUs < 0 || l.PIDs < 0 || l.FFmpegThreads < 0 {
			return say("Limits can't be negative.")
		}
		if l.Memory > 0 && l.Memory < governor.ByteSize(governor.MinMemoryBytes) {
			return sayf("Memory per job must be at least %s.", humanBytes(governor.MinMemoryBytes))
		}
		if l.CPUs > governor.MaxCPUs {
			return sayf("Processor cores per job can be at most %d.", governor.MaxCPUs)
		}
		if l.PIDs > 0 && (l.PIDs < governor.MinPIDs || l.PIDs > governor.MaxPIDs) {
			return sayf("Processes per job must be between %d and %d.", governor.MinPIDs, governor.MaxPIDs)
		}
		if l.FFmpegThreads > governor.MaxFFmpegThreads {
			return sayf("Media processing threads per job can be at most %d.", governor.MaxFFmpegThreads)
		}
		if l.Priority != "" && !governor.Priority(strings.ToLower(strings.TrimSpace(string(l.Priority)))).IsKnown() {
			return say("Choose Normal, Below normal or Low for how jobs share this computer.")
		}
		if l.DiskQuotaBytes > 0 && l.DiskQuotaBytes < governor.ByteSize(governor.MinDiskQuotaBytes) {
			return sayf("Disk space per job must be at least %s.", humanBytes(governor.MinDiskQuotaBytes))
		}
	}
	if sch := p.Schedule; sch != nil {
		if err := checkSchedule(*sch); err != nil {
			return err
		}
	}
	if m := p.MediaCacheMaxBytes; m != nil && *m != 0 && *m < minMediaCacheBytes {
		return sayf("The media cache must be at least %s, or 0 to turn it off.", humanBytes(minMediaCacheBytes))
	}
	return nil
}

// checkSchedule refuses a schedule with a sentence a person can act on.
func checkSchedule(raw governor.Schedule) error {
	s := raw
	s.ApplyDefaults()
	if !s.Mode.IsKnown() {
		return say("Choose when this computer works: any time, when you're away, when plugged in, or at set hours.")
	}
	if raw.IdleMinutes < 0 {
		return say("The time away must be at least 1 minute.")
	}
	if s.Mode == governor.ModeWindows && len(s.Windows) == 0 {
		return say("Add at least one time range, or choose another option.")
	}
	switch s.OnBattery {
	case governor.BatteryAllow, governor.BatteryPause:
	default:
		return say("Choose whether this computer keeps working on battery.")
	}
	for _, w := range s.Windows {
		if _, err := governor.ParseClock(w.Start); err != nil {
			return sayf("%q isn't a time. Use the 24-hour clock, like 09:00.", w.Start)
		}
		if _, err := governor.ParseClock(w.End); err != nil {
			return sayf("%q isn't a time. Use the 24-hour clock, like 18:00.", w.End)
		}
		for _, d := range w.Days {
			if !knownDay(d) {
				return sayf("%q isn't a day. Use names like Mon or Tuesday.", d)
			}
		}
	}
	return nil
}

// knownDay mirrors the governor's day names: the first three letters of an
// English day, any case.
func knownDay(d string) bool {
	n := strings.ToLower(strings.TrimSpace(d))
	if len(n) > 3 {
		n = n[:3]
	}
	switch n {
	case "sun", "mon", "tue", "wed", "thu", "fri", "sat":
		return true
	}
	return false
}
