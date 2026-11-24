import { describe, expect, it } from "vitest";
import { demoView } from "./demo";
import { capabilityChips } from "./derive";

// The contract guard. The Go side (runner/internal/ui/view.go) defines the
// document; this file asserts the fields the SCREENS actually read are present
// on a real document, so a rename in Go fails here rather than rendering
// "undefined" to a user. It is not a type-level check - TypeScript cannot see
// Go - it is a shape check against the fixture, which is written to the same
// contract the shell emits.
describe("the view contract the screens depend on", () => {
  it("carries every top-level section the window renders", () => {
    for (const key of [
      "state", "headline", "runner", "device", "runtime",
      "governor", "limits", "schedule", "settings", "cache", "version",
    ]) {
      expect(demoView).toHaveProperty(key);
    }
  });

  it("carries the governor's verdict verbatim, so the window cannot disagree with status", () => {
    expect(demoView.governor.status).toHaveProperty("allowed");
    expect(demoView.governor.status).toHaveProperty("reason");
    expect(demoView.governor.status).toHaveProperty("held");
  });

  it("marks the runtime as known, which is the difference between 'cannot work' and 'not asked'", () => {
    expect(demoView.runtime.known).toBe(true);
    expect(demoView.runtime.jobTypes.length).toBeGreaterThan(0);
  });

  it("carries the downloaded-media cache the Storage section shows", () => {
    expect(demoView.cache.capped).toBe(true);
    for (const key of ["bytes", "entries", "maxBytes", "hits", "misses", "savedBytes"]) {
      expect(demoView.cache.media).toHaveProperty(key);
    }
  });

  it("carries the editable limits and the host, so the editors can offer real ranges", () => {
    for (const key of ["memory", "cpus", "priority", "diskQuotaBytes"]) {
      expect(demoView.settings.limits).toHaveProperty(key);
    }
    expect(demoView.settings.host?.cpus).toBeGreaterThan(0);
    expect(demoView.settings.host?.memoryBytes).toBeGreaterThan(0);
  });

  it("carries the names ReelBolt sends with a job, and an outcome once it ended", () => {
    const ended = (demoView.jobs ?? []).filter((j) => !j.running);
    expect(ended.length).toBeGreaterThan(0);
    for (const j of ended) expect(j.outcome).toBeTruthy();
    expect(demoView.currentJob?.projectName).toBeTruthy();
    expect(demoView.device.workspaceName).toBeTruthy();
  });

  it("describes what the computer can do in plain words", () => {
    const chips = capabilityChips(demoView.runtime);
    expect(chips).toContain("Renders videos");
    expect(chips).toContain("Processes media");
  });

  it("says when video rendering is missing", () => {
    const chips = capabilityChips({
      jobTypes: [], remotionReady: false, slots: 1, ffmpegVersion: "7.1",
    });
    expect(chips).toContain("No video rendering");
  });
});
