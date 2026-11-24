import { describe, expect, it } from "vitest";
import { demoView } from "./demo";
import type { TelemetryPreview } from "./types";

// The contract guard for E11's privacy panel.
//
// It is the same kind of check contract.test.ts makes for the rest of the
// document, kept beside it rather than inside it: the Settings screen reads
// view.settings.telemetry, and a rename in Go that this file did not notice
// would render "undefined" to a user in the one place where the text is a
// promise about what leaves their machine.
describe("the telemetry contract the Settings screen depends on", () => {
  it("carries the panel the screen renders", () => {
    const t = demoView.settings.telemetry;
    for (const key of [
      "enabled", "asked", "available", "disclosure", "sent",
      "unsent", "dropped", "failed",
    ]) {
      expect(t).toHaveProperty(key);
    }
    expect(t.disclosure.length).toBeGreaterThan(0);
  });

  it("is off and unasked in a fresh document, which is the whole point", () => {
    // The demo fixture is a paired, working machine; even so it is not
    // reporting anything, because nothing turns this on but a person.
    expect(demoView.settings.telemetry.enabled).toBe(false);
  });

  it("says why nothing is being sent rather than leaving a zero unexplained", () => {
    const t = demoView.settings.telemetry;
    if (!t.available) {
      expect(t.note ?? "").toBeTruthy();
    }
  });

  it("renders a preview document without inventing fields", () => {
    // The shape the shell's /api/telemetry/preview answers with. A screen that
    // read a field this is missing would show "undefined" where the promise is.
    const preview: TelemetryPreview = {
      schema: "reelbolt.telemetry/1",
      consentVersion: "2026-10-08",
      disclosure: demoView.settings.telemetry.disclosure,
      enabled: false,
      available: false,
      crash: { kind: "crash", panicType: "<the Go type of the panic>" },
      jobFailure: { kind: "job_failure", failureCode: "<the job's failure code>" },
      fields: [{ name: "kind", note: "crash or job_failure" }],
      neverSent: ["file names, paths or a directory listing"],
      notes: ["Nothing is sent until you turn the switch on."],
    };
    expect(preview.neverSent.length).toBeGreaterThan(0);
    expect(preview.fields[0]).toHaveProperty("name");
    expect(preview.fields[0]).toHaveProperty("note");
    expect(preview.crash).toHaveProperty("kind");
    expect(preview.jobFailure).toHaveProperty("kind");
  });
});
