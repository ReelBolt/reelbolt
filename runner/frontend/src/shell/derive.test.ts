import { describe, expect, it } from "vitest";
import {
  daysLabel,
  formatBytes,
  formatDuration,
  formatWhen,
  isProblem,
  jobContext,
  jobRows,
  outcomeLabel,
  outcomeTone,
  remedyLabel,
  scheduleSummary,
  screenForState,
  screenFromShell,
  setupComplete,
  stageLabel,
  stateLabel,
  stateTone,
  statusSentence,
  statusTone,
} from "./derive";
import { demoView } from "./demo";
import type { JobView, TrayState, View } from "./types";

// The window's logic is pure and tested here, for the same reason the tray's is
// tested in Go: a colour that says "fine" over a broken machine is the failure
// this shell exists to prevent, and it is invisible until a user trusts it.

describe("statusTone", () => {
  it("never paints a machine that cannot work as healthy", () => {
    const bad: TrayState[] = ["error", "unpaired", "stopped", "paused"];
    for (const s of bad) {
      expect(statusTone(s)).not.toBe("teal");
      expect(isProblem(s)).toBe(true);
    }
  });

  it("treats idle and working as the only states where nothing is wrong", () => {
    expect(isProblem("idle")).toBe(false);
    expect(isProblem("working")).toBe(false);
    expect(statusTone("idle")).toBe("teal");
    expect(statusTone("working")).toBe("violet");
  });

  it("answers a colour for every state the shell can send", () => {
    const states: TrayState[] = ["idle", "working", "paused", "error", "unpaired", "stopped"];
    for (const s of states) expect(statusTone(s)).toMatch(/^[a-z]+$/);
  });
});

describe("formatDuration", () => {
  it("renders the ranges a job list has", () => {
    expect(formatDuration(0)).toBe("0s");
    expect(formatDuration(45)).toBe("45s");
    expect(formatDuration(70)).toBe("1m 10s");
    expect(formatDuration(3 * 3600 + 5 * 60)).toBe("3h 05m");
  });

  it("does not invent a duration it does not have", () => {
    expect(formatDuration(-1)).toBe("-");
    expect(formatDuration(Number.NaN)).toBe("-");
  });
});

describe("formatBytes", () => {
  it("scales", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(2048)).toBe("2 KB");
    expect(formatBytes(5.5 * 1024 * 1024)).toBe("5.5 MB");
    expect(formatBytes(10 * 1024 ** 3)).toBe("10 GB");
  });
});

describe("jobRows", () => {
  const job = (id: string, running: boolean, startedAt: string): JobView => ({
    id,
    type: "video.compile",
    title: "Video compile",
    startedAt,
    durationSeconds: 1,
    running,
  });

  it("puts the running job first and the rest newest first", () => {
    const rows = jobRows([
      job("a", false, "2026-10-08T08:00:00Z"),
      job("b", true, "2026-10-08T07:00:00Z"),
      job("c", false, "2026-10-08T09:00:00Z"),
    ]);
    expect(rows.map((r) => r.id)).toEqual(["b", "c", "a"]);
  });

  it("copes with a null list", () => {
    expect(jobRows(null)).toEqual([]);
    expect(jobRows(undefined)).toEqual([]);
  });
});

describe("remedyLabel", () => {
  it("offers nothing for the remedy that says there is nothing to do", () => {
    expect(remedyLabel("none")).toBeNull();
    expect(remedyLabel(undefined)).toBeNull();
  });

  it("offers the action for every other remedy", () => {
    for (const k of ["pair", "start", "resume", "schedule", "runtime", "update"] as const) {
      expect(remedyLabel(k)).toBe(k);
    }
  });
});

describe("screens", () => {
  it("always opens on Home, which carries both the status and the setup steps", () => {
    const v: View = { ...demoView, device: { ...demoView.device, paired: false } };
    expect(screenForState(v)).toBe("home");
    expect(screenForState(demoView)).toBe("home");
  });

  it("understands the screen names the shell sends, old ones included", () => {
    expect(screenFromShell("first-run")).toBe("home");
    expect(screenFromShell("jobs")).toBe("activity");
    expect(screenFromShell("device")).toBe("settings");
    expect(screenFromShell("folders")).toBe("folders");
    expect(screenFromShell(undefined)).toBeNull();
    expect(screenFromShell("nonsense")).toBeNull();
  });

  it("calls setup complete only for a connected computer that can render", () => {
    expect(setupComplete(demoView)).toBe(true);
    expect(setupComplete({ ...demoView, device: { ...demoView.device, paired: false } })).toBe(false);
    expect(setupComplete({ ...demoView, device: { ...demoView.device, revoked: true } })).toBe(false);
    expect(setupComplete({ ...demoView, runtime: { ...demoView.runtime, remotionReady: false } })).toBe(false);
  });
});

describe("state words", () => {
  it("has a plain word and a tone for every state", () => {
    const states: TrayState[] = ["idle", "working", "paused", "error", "unpaired", "stopped"];
    for (const s of states) {
      expect(stateLabel(s)).not.toBe("Unknown");
      expect(stateTone(s)).toMatch(/^(ok|working|idle|warn|bad)$/);
    }
    expect(stateTone("error")).toBe("bad");
    expect(stateTone("paused")).toBe("idle");
  });
});

describe("job outcomes", () => {
  const ended = (outcome?: JobView["outcome"]): JobView => ({
    id: "j", type: "video.compile", title: "Putting the video together",
    startedAt: "2026-10-11T08:00:00Z", durationSeconds: 10, running: false, outcome,
  });

  it("says what this computer saw, never more", () => {
    expect(outcomeLabel(ended("finished"))).toBe("Done");
    expect(outcomeLabel(ended("failed"))).toBe("Failed");
    expect(outcomeLabel(ended("timed-out"))).toBe("Took too long");
    expect(outcomeLabel(ended("interrupted"))).toBe("Interrupted");
    expect(outcomeLabel(ended("cancelled"))).toBe("Cancelled");
    // An older app that sends no outcome is not painted as a success.
    expect(outcomeLabel(ended(undefined))).toBe("Ended");
    expect(outcomeTone(ended(undefined))).toBe("idle");
  });

  it("paints failures red and only a finished job green", () => {
    expect(outcomeTone(ended("finished"))).toBe("ok");
    expect(outcomeTone(ended("failed"))).toBe("bad");
    expect(outcomeTone(ended("timed-out"))).toBe("bad");
    expect(outcomeTone(ended("cancelled"))).toBe("idle");
  });

  it("shows a running job's percent when it has one", () => {
    expect(outcomeLabel({ ...ended(), running: true, percent: 42 })).toBe("42%");
    expect(outcomeLabel({ ...ended(), running: true })).toBe("Working");
  });

  it("names where a job came from with whatever ReelBolt sent", () => {
    expect(jobContext({ ...ended(), projectName: "Launch film", workflowName: "Quick promo" })).toBe("Launch film / Quick promo");
    expect(jobContext({ ...ended(), workspaceName: "Example Studio" })).toBe("Example Studio");
    expect(jobContext(ended())).toBe("");
  });

  it("turns progress words into plain ones", () => {
    expect(stageLabel("bundling")).toBe("Preparing");
    expect(stageLabel("Rendering")).toBe("Rendering");
    expect(stageLabel("thinking")).toBe("Thinking");
    expect(stageLabel(undefined)).toBe("");
  });
});

describe("schedule summary", () => {
  it("says when the computer works in one line", () => {
    expect(scheduleSummary({ mode: "always" })).toBe("Any time");
    expect(scheduleSummary({ mode: "ac" })).toBe("Only when plugged in");
    expect(scheduleSummary({ mode: "idle", idleMinutes: 10 })).toBe("When you have been away for 10 min");
    expect(scheduleSummary({ mode: "windows", windows: [{ days: ["mon", "tue", "wed", "thu", "fri"], start: "18:00", end: "08:00" }] }))
      .toBe("Weekdays, 18:00 to 08:00");
    expect(scheduleSummary({ mode: "always", onBattery: "pause" })).toBe("Any time, not on battery");
  });

  it("reads day names the way runner.json allows them", () => {
    expect(daysLabel([])).toBe("Every day");
    expect(daysLabel(["Saturday", "SUN"])).toBe("Weekends");
    expect(daysLabel(["mon", "wed"])).toBe("Mon, Wed");
  });
});

describe("formatWhen", () => {
  const now = new Date("2026-10-11T12:00:00Z");
  it("is relative when recent and never throws on a bad stamp", () => {
    expect(formatWhen("2026-10-11T11:59:30Z", now)).toBe("just now");
    expect(formatWhen("2026-10-11T11:30:00Z", now)).toBe("30 min ago");
    expect(formatWhen("not a date", now)).toBe("not a date");
    expect(formatWhen(undefined, now)).toBe("");
  });
});

describe("statusSentence", () => {
  it("prefers the remedy's own sentence, because that is the actionable half", () => {
    const v: View = {
      ...demoView,
      detail: "the schedule is holding it",
      remedy: { kind: "schedule", label: "Change schedule", detail: "Outside the hours you set. Change them in Settings." },
    };
    expect(statusSentence(v)).toBe("Outside the hours you set. Change them in Settings.");
  });

  it("falls back to the detail when there is no remedy", () => {
    const v: View = { ...demoView, detail: "nothing to do", remedy: { kind: "none", label: "" } };
    expect(statusSentence(v)).toBe("nothing to do");
  });
});
