import { describe, expect, it } from "vitest";
import { demoFolders, demoView } from "./demo";
import {
  folderCanPause, folderCanResumeHere, folderFilesLabel, folderLastChange,
  folderNeedsDeleteConfirm, folderSkipSummary, folderStatus, folderTone,
} from "./derive";
import type { FolderLink } from "./types";

// The Folders screen's pure logic. The cases are the four states the design
// names plus the two pauses, because confusing those two is the mistake that
// would put a dead button on screen.

function link(mutate: (l: FolderLink) => void = () => {}): FolderLink {
  const l: FolderLink = {
    linkId: "l1", localPath: "/home/ann/Footage", projectId: "p1", projectName: "Launch",
    targetFolder: "Footage", state: "uptodate", headline: "Up to date", fileCount: 12,
  };
  mutate(l);
  return l;
}

describe("folder states", () => {
  it("uses a colour that distinguishes a choice from a fault", () => {
    expect(folderTone("uptodate")).toBe("teal");
    expect(folderTone("syncing")).toBe("violet");
    expect(folderTone("paused")).toBe("gray");
    expect(folderTone("attention")).toBe("red");
  });

  it("shows the reason for a pause or a problem, and the headline otherwise", () => {
    expect(folderStatus(link())).toBe("Up to date");
    expect(folderStatus(link((l) => {
      l.state = "attention"; l.headline = "Needs attention";
      l.reason = "Storage is full (15.0 GB of 15.0 GB used)";
    }))).toBe("Storage is full (15.0 GB of 15.0 GB used)");
    expect(folderStatus(link((l) => {
      l.state = "paused"; l.reason = "Paused in ReelBolt. Resume it there.";
    }))).toBe("Paused in ReelBolt. Resume it there.");
  });
});

describe("the two pauses are not the same", () => {
  it("offers Resume here for a pause this computer made", () => {
    const l = link((x) => { x.state = "paused"; x.pausedBy = "computer"; });
    expect(folderCanResumeHere(l)).toBe(true);
    expect(folderNeedsDeleteConfirm(l)).toBe(false);
  });

  it("offers Resume here, with a confirmation, for the mass-delete guard", () => {
    const l = link((x) => { x.state = "paused"; x.pausedBy = "guard"; x.canConfirmDelete = true; });
    expect(folderCanResumeHere(l)).toBe(true);
    expect(folderNeedsDeleteConfirm(l)).toBe(true);
  });

  it("offers nothing here for a dashboard pause: the dashboard owns it", () => {
    const l = link((x) => { x.state = "paused"; x.pausedBy = "dashboard"; });
    expect(folderCanResumeHere(l)).toBe(false);
    expect(folderNeedsDeleteConfirm(l)).toBe(false);
  });

  it("does not offer Pause for a link the server already paused", () => {
    expect(folderCanPause(link())).toBe(true);
    expect(folderCanPause(link((x) => { x.state = "paused"; }))).toBe(false);
  });
});

describe("what a folder row says", () => {
  it("names files copied and files still going", () => {
    expect(folderFilesLabel(link((l) => { l.fileCount = 1; }))).toBe("1 file copied");
    expect(folderFilesLabel(link((l) => { l.fileCount = 128; l.pending = 3; })))
      .toBe("Syncing 3 files, 128 copied");
    expect(folderFilesLabel(link((l) => { l.fileCount = 0; }))).toBe("");
  });

  it("folds the skip groups into one sentence, biggest first", () => {
    expect(folderSkipSummary(link())).toBe("");
    const summary = folderSkipSummary(link((l) => {
      l.skipped = [
        { reason: "Hidden files and folders are not copied", count: 2 },
        { reason: "System files (.DS_Store, Thumbs.db, desktop.ini) are not copied", count: 5 },
      ];
    }));
    expect(summary).toBe("7 items were not copied: System files (.DS_Store, Thumbs.db, desktop.ini) are not copied");
  });

  it("renders a change timestamp, and falls back to the raw stamp rather than dropping it", () => {
    expect(folderLastChange(link())).toBe("");
    expect(folderLastChange(link((l) => { l.lastChangeAt = "not a date"; }))).toBe("not a date");
    expect(folderLastChange(link((l) => { l.lastChangeAt = "2026-10-11T09:00:00Z"; })))
      .not.toBe("");
  });
});

describe("the demo fixture keeps the contract honest", () => {
  it("shows one of every state, so a preview cannot hide a wording", () => {
    const states = new Set((demoFolders.links ?? []).map((l) => l.state));
    for (const s of ["uptodate", "syncing", "paused", "attention"]) {
      expect(states.has(s as never)).toBe(true);
    }
    const by = new Set((demoFolders.links ?? []).map((l) => l.pausedBy));
    expect(by.has("guard")).toBe(true);
    expect(by.has("dashboard")).toBe(true);
  });

  it("is a real document: every link carries what the screen reads", () => {
    for (const l of demoFolders.links ?? []) {
      expect(l.linkId).toBeTruthy();
      expect(l.localPath).toBeTruthy();
      expect(l.projectId).toBeTruthy();
      expect(l.targetFolder).toBeTruthy();
      expect(l.headline).toBeTruthy();
      expect(typeof l.fileCount).toBe("number");
    }
  });

  it("has a view fixture too, so the shell contract stays checked", () => {
    expect(demoView.state).toBeTruthy();
  });
});
