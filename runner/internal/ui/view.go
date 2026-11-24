package ui

import (
	"fmt"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

// Inputs is everything the shell observed, at one instant.
//
// It is a plain value rather than a pile of getters so that Build is a pure
// function: every state the tray can show, including the awkward ones (a runner
// someone else started, a governor holding on a schedule, a device the gateway
// refused), is a table entry in a test rather than a scenario someone has to
// reproduce by hand.
type Inputs struct {
	// Version is this build's version string.
	Version string

	// Runner is the running runner, in this process or another.
	Runner RunnerState

	// DaemonErr is the error the shell's own runner exited with, if it did.
	// It is what turns StateStopped into StateError.
	DaemonErr string
	// Restarting is true while the shell restarts its own runner to apply new
	// settings, so a few seconds of "not running" are not shown as a stop.
	Restarting bool

	// Identity, from runner.json.
	Paired     bool
	DeviceID   string
	DeviceName string
	Workspace  string
	GatewayURL string
	KeyStore   string
	// Revoked means the gateway refused this device. The daemon keeps the key
	// and wipes the device id, so this is the one "unpaired" a user can cause
	// from the dashboard rather than by never pairing.
	Revoked bool
	// WorkspaceName and PairedByEmail are the device API's answer to "who am
	// I", empty until it has answered.
	WorkspaceName string
	PairedByEmail string

	// Current is the job in flight, when there is exactly one to name.
	Current *Job
	// Jobs is the ledger, most recent first.
	Jobs []Job

	// Runtime is what the probe found.
	Runtime RuntimeView
	// Limits and Schedule are the resolved governor configuration, so the
	// window can show what a job will be given and when the machine works
	// without the user opening runner.json.
	Limits   governor.Limits
	Schedule governor.Schedule
	// Settings is the editable half of the configuration.
	Settings SettingsView
	// Cache is the local disk the runner owns.
	Cache CacheView

	// Update is what the release check last said. Nil means it has not run.
	Update *UpdateView

	// DashboardURL is the ReelBolt origin a job's dashboard link is built on.
	// Empty means no job gets a link.
	DashboardURL string

	// Warnings are lines the user should read, collected rather than
	// summarised: a warning nobody can act on is noise, so each one names the
	// thing that is wrong.
	Warnings []string

	// Now is the clock. Zero means time.Now.
	Now time.Time
}

// RunnerState is the running runner, however it was started.
//
// It is deliberately not "the shell's daemon": a person who runs
// "reelbolt-runner run --headless" in a service manager and then opens the tray
// is looking at ONE machine, and a tray that ignored the headless runner would
// show "stopped" over a machine that is working. The live document the daemon
// publishes is what both of them read.
type RunnerState struct {
	// Running is true when a runner is connected on this machine.
	Running bool
	// InProcess is true when this process is the one running it.
	InProcess bool
	// PID and StartedAt identify it.
	PID       int
	StartedAt string
	// Governor is its verdict, when it is known. A runner started by another
	// process publishes it in the live document; this process reads it
	// straight from the governor, which is fresher.
	Governor governor.Status
	// GovernorKnown is false when a runner is running but this process cannot
	// see its verdict — a stale or unreadable live document.
	GovernorKnown bool
}

// DeviceView is the Device screen.
type DeviceView struct {
	Paired    bool   `json:"paired"`
	DeviceID  string `json:"deviceId,omitempty"`
	Name      string `json:"name,omitempty"`
	Workspace string `json:"workspaceId,omitempty"`
	// WorkspaceName and PairedByEmail come from ReelBolt (the device's own "who
	// am I" call) and are absent until it has answered once.
	WorkspaceName string `json:"workspaceName,omitempty"`
	PairedByEmail string `json:"pairedByEmail,omitempty"`
	GatewayURL    string `json:"gatewayUrl,omitempty"`
	KeyStore      string `json:"keyStore,omitempty"`
	Revoked       bool   `json:"revoked,omitempty"`
}

// RuntimeView is what the capability probe found: the first-run wizard's
// runtime check and the "why can this machine only run ffmpeg" answer.
type RuntimeView struct {
	// Image is the pinned or configured runtime image, and Origin says where
	// it came from.
	Image  string `json:"image,omitempty"`
	Origin string `json:"imageOrigin,omitempty"`
	// ContainerCLI is the runtime that was verified, empty when none was.
	ContainerCLI string `json:"containerCli,omitempty"`
	// FfmpegVersion is empty when no usable ffmpeg was found.
	FfmpegVersion string `json:"ffmpegVersion,omitempty"`
	// JobTypes is the capability document's own list.
	JobTypes []string `json:"jobTypes"`
	// RemotionReady is true only when the runtime ran the pinned image and
	// confirmed it is Remotion-ready.
	RemotionReady bool `json:"remotionReady"`
	// Failure is one plain sentence about why videos cannot be rendered here,
	// shown verbatim. Detail is an optional second sentence.
	Failure string `json:"failure,omitempty"`
	Detail  string `json:"detail,omitempty"`
	// Candidates is every runtime that was tried, so "it found nothing" can be
	// read as "it tried docker and podman and here is what each said".
	Candidates []string `json:"candidates,omitempty"`
	// Slots is how many jobs run at once.
	Slots int `json:"slots"`
	// Known is true once the probe has answered. It separates "this machine
	// cannot run anything" from "this machine has not been asked yet", which
	// are different sentences and only one of them is an error.
	Known bool `json:"known"`
	// Probing is true while the machine is still being surveyed. The probe
	// starts a container, so it takes seconds and the window says so rather
	// than showing an empty runtime panel.
	Probing bool `json:"probing,omitempty"`
	// ProbeError is why the probe itself failed, which is different from a
	// probe that ran and found nothing usable.
	ProbeError string `json:"probeError,omitempty"`
	// ImagePresent is whether the video toolkit is downloaded on this computer.
	// Nil when it could not be checked (no working Docker or Podman).
	ImagePresent *bool `json:"imagePresent,omitempty"`
	// ImageBytes is its size on disk, once present and when the engine says.
	ImageBytes int64 `json:"imageBytes,omitempty"`
	// ImageDownloadable is true only for a toolkit pinned to a published
	// version (name@sha256:...). An unpinned development image exists only on
	// the computer that built it, so there is nothing to download and the
	// window does not offer to.
	ImageDownloadable bool `json:"imageDownloadable"`
	// Download is the in-app download ("download-runtime"), once one started.
	Download *RuntimeDownload `json:"download,omitempty"`
}

// Capable reports whether this machine can run anything at all.
func (r RuntimeView) Capable() bool { return len(r.JobTypes) > 0 }

// SettingsView is the Settings screen's data.
type SettingsView struct {
	Slots    int                   `json:"slots"`
	Limits   governor.LimitsConfig `json:"limits"`
	Schedule governor.Schedule     `json:"schedule"`
	// Host is what this computer has, so the limit editors can offer sensible
	// ranges.
	Host *HostView `json:"host,omitempty"`
	// MediaCacheMaxBytes is the cap on the downloaded-media cache.
	MediaCacheMaxBytes *int64 `json:"mediaCacheMaxBytes,omitempty"`
	UpdateChannel      string `json:"updateChannel"`
	AutoUpdate         bool   `json:"autoUpdate"`
	TrustedKeys        string `json:"trustedKeys,omitempty"`
	Autostart          bool   `json:"autostart"`
	AutostartKnown     bool   `json:"autostartKnown"`
	ConfigPath         string `json:"configPath,omitempty"`
	// Telemetry is the opt-in crash-reporting switch (E11). It is part of the
	// settings document rather than a screen of its own because that is what it
	// is: one setting, with a disclosure and a payload preview attached.
	Telemetry TelemetryView `json:"telemetry"`
}

// CacheView is the disk ReelBolt uses on this computer, in two parts.
//
// Bytes and Jobs are leftover job folders: each job works in a folder of its
// own and deletes it when it ends, so anything counted here is from a job that
// was interrupted, and "Clear" is always safe while nothing runs.
//
// Media is the downloaded-media cache: the source files (footage, music,
// images) a job downloaded, kept so that the next job using the same file does
// not download it again. It has a size cap (Settings), it evicts the least
// recently used files to stay under it, and Capped is true while it is on. It
// is absent when the cache is turned off.
type CacheView struct {
	ScratchRoot   string `json:"scratchRoot,omitempty"`
	WorkspaceRoot string `json:"workspaceRoot,omitempty"`
	Bytes         int64  `json:"bytes"`
	Jobs          int    `json:"entries"`
	Known         bool   `json:"known"`
	// Capped is true when the downloaded-media cache is on, and therefore
	// has a size cap.
	Capped bool            `json:"capped"`
	Note   string          `json:"note,omitempty"`
	Media  *MediaCacheView `json:"media,omitempty"`
}

// MediaCacheView is the downloaded-media cache's figures.
type MediaCacheView struct {
	Root     string `json:"root,omitempty"`
	Bytes    int64  `json:"bytes"`
	Entries  int64  `json:"entries"`
	MaxBytes int64  `json:"maxBytes"`
	// Hits and Misses count since this app started.
	Hits   int64 `json:"hits"`
	Misses int64 `json:"misses"`
	// SavedBytes is how much was not downloaded again thanks to the cache.
	SavedBytes int64 `json:"savedBytes"`
}

// UpdateView is what the release check last said.
type UpdateView struct {
	CheckedAt      string `json:"checkedAt,omitempty"`
	CurrentVersion string `json:"currentVersion"`
	Channel        string `json:"channel"`
	Action         string `json:"action"`
	ReleaseVersion string `json:"releaseVersion,omitempty"`
	Reason         string `json:"reason,omitempty"`
	// RestartNeeded is true when an update was installed and the runner is
	// still the old binary until it restarts.
	RestartNeeded bool `json:"restartRequired,omitempty"`
	// StopTakingJobs is the gateway's "your version is below minSupported"
	// answer: the runner must not take work until it is updated.
	StopTakingJobs bool `json:"stopTakingJobs,omitempty"`
}

// View is the whole document the tray and the window render.
type View struct {
	State    TrayState `json:"state"`
	Headline string    `json:"headline"`
	Detail   string    `json:"detail,omitempty"`
	Remedy   *Remedy   `json:"remedy,omitempty"`

	Runner RunnerView `json:"runner"`
	Device DeviceView `json:"device"`

	Current *JobView  `json:"currentJob,omitempty"`
	Jobs    []JobView `json:"jobs"`

	Runtime  RuntimeView       `json:"runtime"`
	Governor GovernorView      `json:"governor"`
	Limits   governor.Limits   `json:"limits"`
	Schedule governor.Schedule `json:"schedule"`
	Settings SettingsView      `json:"settings"`
	Cache    CacheView         `json:"cache"`
	Update   *UpdateView       `json:"update,omitempty"`

	// FolderSummary is folder sync in one line for the tray, empty when this
	// machine syncs no folders and nothing is wrong.
	FolderSummary string `json:"folderSummary,omitempty"`

	Version  string   `json:"version"`
	Warnings []string `json:"warnings,omitempty"`
}

// RunnerView is the running runner as the window shows it.
type RunnerView struct {
	Running         bool   `json:"running"`
	InProcess       bool   `json:"inProcess"`
	PID             int    `json:"pid,omitempty"`
	StartedAt       string `json:"startedAt,omitempty"`
	GovernorKnown   bool   `json:"governorKnown"`
	Allowed         bool   `json:"allowed"`
	Reason          string `json:"reason,omitempty"`
	ReasonDetail    string `json:"reasonDetail,omitempty"`
	Held            bool   `json:"held"`
	HeldBy          string `json:"heldBy,omitempty"`
	HeldReason      string `json:"heldReason,omitempty"`
	NextChange      string `json:"nextChange,omitempty"`
	Degraded        bool   `json:"degraded,omitempty"`
	ScheduleSummary string `json:"scheduleSummary,omitempty"`
	SignalsSummary  string `json:"signalsSummary,omitempty"`
}

// GovernorView is the governor's own status plus the two sentences a person
// reads. The raw fields are carried through unchanged so the window and the
// tray can never disagree with "reelbolt-runner status".
type GovernorView struct {
	Status   governor.Status `json:"status"`
	Known    bool            `json:"known"`
	Why      string          `json:"why,omitempty"`
	Resumes  string          `json:"resumesAt,omitempty"`
	CanPause bool            `json:"canPause"`
}

// Build derives the View. It is pure: same Inputs, same View, no clock read
// except the one in Inputs.
func Build(in Inputs) View {
	now := in.Now
	if now.IsZero() {
		now = time.Now()
	}

	st := in.Runner.Governor
	verdict := describeGovernor(st, in.Schedule)

	v := View{
		Version: in.Version,
		Runner: RunnerView{
			Running:         in.Runner.Running,
			InProcess:       in.Runner.InProcess,
			PID:             in.Runner.PID,
			StartedAt:       in.Runner.StartedAt,
			GovernorKnown:   in.Runner.GovernorKnown,
			Allowed:         st.Allowed,
			Reason:          st.Reason,
			ReasonDetail:    verdict.Detail,
			Held:            st.Held,
			HeldBy:          st.HeldBy,
			HeldReason:      st.HeldReason,
			NextChange:      st.NextChange,
			Degraded:        st.Degraded,
			ScheduleSummary: describeSchedule(in.Schedule),
			SignalsSummary:  describeSignals(in.Schedule),
		},
		Governor: GovernorView{
			Status:   st,
			Known:    in.Runner.GovernorKnown,
			Why:      verdict.Detail,
			Resumes:  st.NextChange,
			CanPause: in.Runner.Running && in.Runner.GovernorKnown,
		},
		Device: DeviceView{
			Paired:        in.Paired,
			DeviceID:      in.DeviceID,
			Name:          in.DeviceName,
			Workspace:     in.Workspace,
			WorkspaceName: in.WorkspaceName,
			PairedByEmail: in.PairedByEmail,
			GatewayURL:    in.GatewayURL,
			KeyStore:      in.KeyStore,
			Revoked:       in.Revoked,
		},
		Runtime:  in.Runtime,
		Limits:   in.Limits,
		Schedule: in.Schedule,
		Settings: in.Settings,
		Cache:    in.Cache,
		Update:   in.Update,
		Jobs:     jobViews(in.Jobs, now, in.DashboardURL),
		Warnings: append([]string(nil), in.Warnings...),
	}
	if in.Current != nil {
		jv := jobView(*in.Current, now, in.DashboardURL)
		v.Current = &jv
	}

	state, headline, detail, remedy := classify(in, verdict, in.Current)
	v.State, v.Headline, v.Detail, v.Remedy = state, headline, detail, remedy
	return v
}

// classify picks the state, the sentence and the remedy.
//
// The order of the checks IS the policy, and it is the order a person would use:
//
//  1. Nothing can work, for a reason no amount of waiting fixes (unpaired, the
//     device was removed, nothing this computer can run, the runner crashed).
//     Those are errors and unpaired, and they are tested before anything else
//     because a broken machine behind a green "idle" is the failure this whole
//     file exists to prevent.
//  2. Nothing is running. That is not an error — it is the state a fresh
//     install is in before anything starts.
//  3. A gate said no. The reason is the governor's, in plain words.
//  4. Work, or none.
//
// Every sentence here is read by a creator, not an engineer: no file names, no
// component names, and each remedy is a short button label.
func classify(in Inputs, verdict verdictText, current *Job) (TrayState, string, string, *Remedy) {
	unpaired := !in.Paired || in.Revoked

	if in.Revoked {
		return StateUnpaired, "This computer was removed from ReelBolt",
			"Someone removed it from your ReelBolt account, so it stopped taking work.",
			&Remedy{Kind: RemedyPair, Label: "Pair this computer",
				Detail: "Pair it again to put it back to work."}
	}
	if unpaired {
		return StateUnpaired, "Not connected to ReelBolt",
			"Pair this computer with your ReelBolt account so it can make videos for you.",
			&Remedy{Kind: RemedyPair, Label: "Pair this computer",
				Detail: "You'll get a short code to approve in ReelBolt."}
	}
	// Only an answered probe may call the machine incapable. A shell that has
	// not probed yet — or one watching a runner another process started — must
	// not report a machine it has not measured as broken.
	if !in.Runtime.Capable() && (in.Runtime.Known || in.Runtime.Failure != "") {
		return StateError, "This computer can't make videos yet",
			noRuntimeReason(in.Runtime),
			&Remedy{Kind: RemedyRuntime, Label: "Set up video rendering",
				Detail: "Video rendering needs Docker or Podman. The setup guide walks you through it."}
	}
	if in.DaemonErr != "" {
		return StateError, "ReelBolt stopped because of a problem",
			"It stopped unexpectedly on this computer.",
			&Remedy{Kind: RemedyStart, Label: "Start",
				Detail: "If it keeps stopping, save a diagnostics report in Settings and send it to support."}
	}
	if !in.Runner.Running {
		if in.Restarting {
			return StateStopped, "Applying your changes",
				"ReelBolt is restarting on this computer with your new settings.",
				&Remedy{Kind: RemedyNone}
		}
		return StateStopped, "Not running",
			"This computer isn't taking any work right now.",
			&Remedy{Kind: RemedyStart, Label: "Start",
				Detail: "Videos sent to this computer wait until you start it."}
	}
	// A hold placed from ReelBolt is a pause the person here did not ask for,
	// so the remedy is not Resume: only ReelBolt can release it, and the runner
	// is holding the connection open precisely so it can hear that. Offering a
	// Resume button here would be a button that does nothing.
	if in.Runner.GovernorKnown && !in.Runner.Governor.Allowed {
		if in.Runner.Governor.Held && in.Runner.Governor.HeldBy == governor.SourceRemote {
			return StatePaused, "Paused by ReelBolt",
				"Someone paused this computer from ReelBolt. It starts again when they resume it there.",
				&Remedy{Kind: RemedyNone}
		}
		if in.Runner.Governor.Held {
			return StatePaused, "Paused", verdict.Detail,
				&Remedy{Kind: RemedyResume, Label: "Resume",
					Detail: "This computer starts taking work again right away."}
		}
		// Not a hold: the schedule, the power source or idle time is the gate.
		// The remedy is the schedule because that is the one the person set on
		// purpose, and the one they can change. Resume would do nothing here.
		return StatePaused, "Waiting for your schedule", verdict.Detail,
			&Remedy{Kind: RemedySchedule, Label: "Change schedule",
				Detail: "You chose when this computer works. You can change that in Settings."}
	}
	if current != nil {
		return StateWorking, workingHeadline(*current), "", nil
	}
	// Ready, but video rendering is off. The machine still does media work,
	// so this is not an error; it is the one thing worth a button.
	if detail, remedy, ok := renderingNotReady(in.Runtime); ok {
		return StateIdle, "Ready for work", detail, remedy
	}
	return StateIdle, "Ready for work", verdict.Detail, nil
}

// renderingNotReady explains an idle machine that cannot render videos yet,
// when the probe has said so.
func renderingNotReady(r RuntimeView) (string, *Remedy, bool) {
	if !r.Known || r.RemotionReady || r.Probing {
		return "", nil, false
	}
	if d := r.Download; d != nil && d.Active {
		if d.Percent != nil {
			return fmt.Sprintf("Downloading the video toolkit (%d%%). Until it's done, this computer only does media processing.", *d.Percent),
				nil, true
		}
		return "Downloading the video toolkit. Until it's done, this computer only does media processing.", nil, true
	}
	reason := r.Failure
	if d := r.Download; d != nil && d.Error != "" {
		reason = d.Error
	}
	if reason == "" && r.ContainerCLI != "" {
		reason = "The video toolkit isn't downloaded yet."
	}
	if reason == "" {
		reason = "Video rendering isn't set up yet."
	}
	return "This computer can't render videos yet, only process media. " + reason,
		&Remedy{Kind: RemedyRuntime, Label: "Set up video rendering",
			Detail: "Video rendering needs Docker or Podman and the video toolkit, which ReelBolt downloads once."}, true
}

// workingHeadline names the job, because "working" with no noun is the least
// useful thing a tray can say.
func workingHeadline(j Job) string {
	name := JobTitle(j.Type)
	if j.Percent != nil {
		return fmt.Sprintf("Working: %s (%d%%)", name, *j.Percent)
	}
	return "Working: " + name
}

// noRuntimeReason is the sentence under "This computer can't make videos yet".
// The probe's own explanation wins, because "Docker isn't running" and "nothing
// is installed" have different fixes.
func noRuntimeReason(r RuntimeView) string {
	if r.Failure != "" {
		return r.Failure
	}
	if r.Detail != "" {
		return r.Detail
	}
	if r.ContainerCLI != "" {
		return "The video toolkit isn't downloaded yet, and the media processing tools are missing."
	}
	return "Video rendering needs Docker or Podman, and media processing needs its own tools. Neither was found."
}
