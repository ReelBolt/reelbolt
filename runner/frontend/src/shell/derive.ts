import type {
  FolderLink, FolderState, FoldersView, JobOutcome, JobView, RemedyKind, RuntimeView, Schedule,
  TrayState, View,
} from "./types";

// The window's pure logic. Every screen renders from these, and every one of
// them is tested: a colour that says "fine" over a broken machine is the failure
// this whole shell exists to prevent (build_test in Go makes the same point
// about the tray).

// Tone is the window's small colour vocabulary. It maps onto the CSS tokens in
// styles/app.css (data-tone), never onto a raw colour.
export type Tone = "ok" | "working" | "idle" | "warn" | "bad";

// statusTone maps the shell's state onto a Mantine colour. The mapping is
// deliberately NOT "green when idle" for everything: a machine that cannot work
// is red, and a machine a person paused is grey - paused is a choice, not a
// fault.
export function statusTone(state: TrayState): string {
  switch (state) {
    case "idle":
      return "teal";
    case "working":
      return "violet";
    case "paused":
      return "gray";
    case "stopped":
      return "yellow";
    case "unpaired":
      return "orange";
    case "error":
      return "red";
    default:
      return "gray";
  }
}

// stateTone is statusTone for the window's own tokens.
export function stateTone(state: TrayState): Tone {
  switch (state) {
    case "idle":
      return "ok";
    case "working":
      return "working";
    case "paused":
      return "idle";
    case "stopped":
    case "unpaired":
      return "warn";
    case "error":
      return "bad";
    default:
      return "idle";
  }
}

// stateLabel is the short word on the state pill.
export function stateLabel(state: TrayState): string {
  switch (state) {
    case "idle":
      return "Ready";
    case "working":
      return "Working";
    case "paused":
      return "Paused";
    case "stopped":
      return "Stopped";
    case "unpaired":
      return "Not connected";
    case "error":
      return "Needs attention";
    default:
      return "Unknown";
  }
}

// isProblem reports whether the icon should ask for attention. Idle and working
// are the only two states where nothing is wrong.
export function isProblem(state: TrayState): boolean {
  return state !== "idle" && state !== "working";
}

export function formatDuration(seconds: number): string {
  if (!isFinite(seconds) || seconds < 0) return "-";
  const s = Math.round(seconds);
  if (s < 60) return s + "s";
  const m = Math.floor(s / 60);
  if (m < 60) return m + "m " + String(s % 60).padStart(2, "0") + "s";
  const h = Math.floor(m / 60);
  return h + "h " + String(m % 60).padStart(2, "0") + "m";
}

// formatBytes uses the units people see in their file manager (GB, not GiB),
// computed on the binary scale the runner's limits use.
export function formatBytes(bytes: number): string {
  if (!isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  const digits = i === 0 || v >= 100 ? 0 : v >= 10 ? 1 : 1;
  return v.toFixed(digits).replace(/\.0$/, "") + " " + units[i];
}

// formatWhen renders a timestamp relative to now when it is recent, and as a
// date otherwise. It never throws on a malformed stamp: it hands it back.
export function formatWhen(iso: string | undefined, now: Date = new Date()): string {
  if (!iso) return "";
  const at = new Date(iso);
  if (isNaN(at.getTime())) return iso;
  const secs = Math.round((now.getTime() - at.getTime()) / 1000);
  if (secs >= 0 && secs < 45) return "just now";
  if (secs >= 0 && secs < 3600) return Math.max(1, Math.round(secs / 60)) + " min ago";
  const sameDay = at.toDateString() === now.toDateString();
  const time = at.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  if (sameDay) return "today at " + time;
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  if (at.toDateString() === yesterday.toDateString()) return "yesterday at " + time;
  return at.toLocaleDateString([], { day: "numeric", month: "short" }) + " at " + time;
}

// canRender reports whether this computer can make videos (not just process
// media). It is what the setup checklist's second step is about.
export function canRender(runtime: RuntimeView): boolean {
  return runtime.remotionReady;
}

// capabilityChips are what this computer says it can do, in plain words.
export function capabilityChips(runtime: RuntimeView): string[] {
  const chips: string[] = [];
  if (runtime.remotionReady) chips.push("Renders videos");
  else if (runtime.containerCli) chips.push("Video rendering not ready");
  else chips.push("No video rendering");
  if (runtime.ffmpegVersion) chips.push("Processes media");
  else chips.push("No media processing");
  chips.push(runtime.slots + (runtime.slots === 1 ? " job at a time" : " jobs at a time"));
  return chips;
}

// containerName is Docker or Podman, named the way their makers name them.
export function containerName(cli: string | undefined): string {
  if (!cli) return "Docker or Podman";
  const c = cli.toLowerCase();
  if (c.includes("podman")) return "Podman";
  if (c.includes("docker")) return "Docker";
  return cli;
}

// remedyLabel is the button text, or null when the state offers no action. The
// window and the tray read the same remedy from the same view, so they cannot
// disagree about what to do.
export function remedyLabel(remedy: RemedyKind | undefined): string | null {
  if (!remedy || remedy === "none") return null;
  return remedy;
}

// jobRows sorts the list the way a person reads it: running first, then most
// recent, and it never hides a row. The list is the runner's own memory, and a
// window that silently dropped a failure would be worse than an empty one.
export function jobRows(jobs: JobView[] | null | undefined): JobView[] {
  const rows = [...(jobs ?? [])];
  rows.sort((a, b) => {
    if (a.running !== b.running) return a.running ? -1 : 1;
    return b.startedAt.localeCompare(a.startedAt);
  });
  return rows;
}

// outcomeLabel and outcomeTone describe how this computer's part of a job
// ended. They say what the computer saw, and no more: "Done" means ReelBolt
// closed the job normally, not that the whole video came out right.
export function outcomeLabel(job: JobView): string {
  if (job.running) return job.percent != null ? job.percent + "%" : "Working";
  switch (job.outcome) {
    case "finished":
      return "Done";
    case "cancelled":
      return "Cancelled";
    case "timed-out":
      return "Took too long";
    case "failed":
      return "Failed";
    case "interrupted":
      return "Interrupted";
    default:
      return "Ended";
  }
}

export function outcomeTone(job: JobView): Tone {
  if (job.running) return "working";
  const o: JobOutcome | undefined = job.outcome;
  switch (o) {
    case "finished":
      return "ok";
    case "failed":
    case "timed-out":
      return "bad";
    case "interrupted":
      return "warn";
    default:
      return "idle";
  }
}

// stageLabel turns a job's own progress word into something a person reads.
// Unknown words are shown as they are, capitalised: they come from ReelBolt's
// own code, so they are never paths or ids.
export function stageLabel(stage: string | undefined): string {
  if (!stage) return "";
  const s = stage.trim().toLowerCase();
  const known: Record<string, string> = {
    bundling: "Preparing",
    staging: "Downloading files",
    stage: "Downloading files",
    rendering: "Rendering",
    render: "Rendering",
    encoding: "Encoding",
    publishing: "Uploading",
    publish: "Uploading",
    uploading: "Uploading",
  };
  return known[s] ?? s.charAt(0).toUpperCase() + s.slice(1);
}

// jobContext is the "where is this from" line: project, then workflow, then
// workspace, whichever ReelBolt sent.
export function jobContext(job: JobView): string {
  const parts = [job.projectName, job.workflowName].filter(Boolean) as string[];
  if (parts.length === 0 && job.workspaceName) parts.push(job.workspaceName);
  return parts.join(" / ");
}

export const SCREENS = ["home", "activity", "folders", "settings"] as const;
export type Screen = (typeof SCREENS)[number];

// screenFromShell maps the screen names the Go shell sends (older names
// included) onto this window's screens.
export function screenFromShell(name: string | undefined): Screen | null {
  switch (name) {
    case "home":
    case "first-run":
      return "home";
    case "activity":
    case "jobs":
      return "activity";
    case "folders":
      return "folders";
    case "settings":
    case "device":
      return "settings";
    default:
      return null;
  }
}

// screenForState is where the window opens when nothing else said otherwise.
// Home carries both the setup steps and the live status, so it is always the
// right place to start.
export function screenForState(_view: View): Screen {
  return "home";
}

// setupComplete is true once the computer is connected and can render.
export function setupComplete(view: View): boolean {
  return view.device.paired && !view.device.revoked && view.runtime.known === true && canRender(view.runtime);
}

// statusSentence is the one line under the headline. It prefers the remedy's
// own sentence, because that is the actionable half.
export function statusSentence(view: View): string {
  if (view.remedy && view.remedy.kind !== "none" && view.remedy.detail) {
    return view.remedy.detail;
  }
  return view.detail ?? "";
}

// ---- schedule ----

export const DAYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] as const;
const DAY_NAMES: Record<string, string> = {
  mon: "Mon", tue: "Tue", wed: "Wed", thu: "Thu", fri: "Fri", sat: "Sat", sun: "Sun",
};

// normalizeDay accepts what runner.json accepts (three letters or the full
// name, any case) and returns the three-letter key, or "".
export function normalizeDay(d: string): string {
  const k = d.trim().toLowerCase().slice(0, 3);
  return (DAYS as readonly string[]).includes(k) ? k : "";
}

// daysLabel names a set of days the way a person says them.
export function daysLabel(days: string[] | undefined): string {
  const set = new Set((days ?? []).map(normalizeDay).filter(Boolean));
  if (set.size === 0 || set.size === 7) return "Every day";
  const weekdays = ["mon", "tue", "wed", "thu", "fri"];
  if (set.size === 5 && weekdays.every((d) => set.has(d))) return "Weekdays";
  if (set.size === 2 && set.has("sat") && set.has("sun")) return "Weekends";
  return DAYS.filter((d) => set.has(d)).map((d) => DAY_NAMES[d]).join(", ");
}

// scheduleSummary is the one-line answer to "when does this computer work".
export function scheduleSummary(s: Schedule | undefined): string {
  if (!s) return "Any time";
  let base: string;
  switch ((s.mode || "always").toLowerCase()) {
    case "idle":
      base = "When you have been away for " + (s.idleMinutes ?? 5) + " min";
      break;
    case "ac":
      base = "Only when plugged in";
      break;
    case "windows":
      base = "Only during set hours";
      break;
    default:
      base = "Any time";
  }
  const windows = s.windows ?? [];
  if (windows.length > 0) {
    const w = windows[0];
    const more = windows.length > 1 ? " (+" + (windows.length - 1) + " more)" : "";
    const hours = daysLabel(w.days) + ", " + w.start + " to " + w.end + more;
    base = (s.mode || "always") === "windows" ? hours : base + ", " + hours;
  }
  if ((s.onBattery ?? "allow") === "pause" && (s.mode ?? "") !== "ac") base += ", not on battery";
  return base;
}

// ---- folder sync ----
//
// The Folders screen's pure logic. Every branch is tested: a folder shown as
// "Up to date" while nothing is copying is exactly the kind of quiet lie this
// window must not tell.

// folderTone maps a link's state onto a colour. Paused is grey because it is a
// choice, not a fault; attention is red because it is.
export function folderTone(state: FolderState): string {
  switch (state) {
    case "uptodate":
      return "teal";
    case "syncing":
      return "violet";
    case "paused":
      return "gray";
    case "attention":
      return "red";
    default:
      return "gray";
  }
}

// folderChipTone is folderTone for the window's own tokens.
export function folderChipTone(state: FolderState): Tone {
  switch (state) {
    case "uptodate":
      return "ok";
    case "syncing":
      return "working";
    case "attention":
      return "bad";
    default:
      return "idle";
  }
}

// folderStatus is the one line a person reads: the engine's headline, plus its
// reason when there is one to act on. A paused link's reason says WHERE to
// resume it, which is the whole difference between the two pauses.
export function folderStatus(link: FolderLink): string {
  if (link.reason && (link.state === "attention" || link.state === "paused")) {
    return link.reason;
  }
  return link.headline;
}

// folderCanResumeHere is true only when a Resume this computer performs would
// actually do something. A dashboard pause is resumed in the dashboard; the
// guard's pause is resumed here, and only with the deletion confirmation.
export function folderCanResumeHere(link: FolderLink): boolean {
  if (link.state !== "paused") return false;
  return link.pausedBy !== "dashboard";
}

// folderNeedsDeleteConfirm is the guard's pause: Resume must carry the
// confirmation or the server would hold the deletions back again.
export function folderNeedsDeleteConfirm(link: FolderLink): boolean {
  return link.state === "paused" && link.pausedBy === "guard" && link.canConfirmDelete === true;
}

// folderCanPause is false for a link the server already paused: there is
// nothing left for this computer to pause.
export function folderCanPause(link: FolderLink): boolean {
  return link.state !== "paused";
}

// folderFilesLabel names what was copied, and what is still going, in the
// words a person uses. It returns "" when there is nothing to say, so a caller
// can render nothing rather than "0 files".
export function folderFilesLabel(link: FolderLink): string {
  const files = link.fileCount ?? 0;
  const pending = link.pending ?? 0;
  if (pending > 0) {
    return "Syncing " + pending + (pending === 1 ? " file" : " files") +
      (files > 0 ? ", " + files + " copied" : "");
  }
  if (files === 0) return "";
  return files + (files === 1 ? " file" : " files") + " copied";
}

// folderSkipSummary folds the engine's skip groups into one sentence, naming
// the biggest group first and the total. The engine's own wording is kept: it
// is written for people and it says what to do.
export function folderSkipSummary(link: FolderLink): string {
  const groups = [...(link.skipped ?? [])].filter((g) => g.count > 0);
  if (groups.length === 0) return "";
  groups.sort((a, b) => (b.count !== a.count ? b.count - a.count : a.reason.localeCompare(b.reason)));
  const total = groups.reduce((n, g) => n + g.count, 0);
  return total + (total === 1 ? " item was not copied: " : " items were not copied: ") + groups[0].reason;
}

// folderLastChange renders when the folder last reached the project, in the
// same shape the rest of the window uses for a timestamp.
export function folderLastChange(link: FolderLink, now?: Date): string {
  if (!link.lastChangeAt) return "";
  const at = new Date(link.lastChangeAt);
  if (isNaN(at.getTime())) return link.lastChangeAt;
  return formatWhen(link.lastChangeAt, now);
}

// folderProblems is a screen-level warning list: why folder sync is off, in the
// shell's own words, when it is.
export function folderProblems(doc: FoldersView | null): string[] {
  if (!doc) return [];
  return doc.problem ? [doc.problem] : [];
}

// folderName is the last segment of a local path, for a title; the full path
// stays visible underneath.
export function folderName(path: string): string {
  const parts = path.split(/[\\/]+/).filter(Boolean);
  return parts[parts.length - 1] ?? path;
}
