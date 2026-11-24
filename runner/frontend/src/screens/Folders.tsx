import { useCallback, useEffect, useState } from "react";
import { Button, Group, Menu, ActionIcon, Skeleton } from "@mantine/core";
import {
  IconDots, IconExternalLink, IconFolder, IconFolderPlus, IconPlayerPause, IconPlayerPlay, IconTrash,
} from "@tabler/icons-react";
import type { Act } from "../App";
import type { FolderLink, FoldersView } from "../shell/types";
import type { Bridge } from "../shell/bridge";
import {
  folderCanPause, folderCanResumeHere, folderChipTone, folderFilesLabel, folderLastChange, folderName,
  folderNeedsDeleteConfirm, folderSkipSummary, folderStatus,
} from "../shell/derive";
import { Chip, Notice, Page, Section, Surface } from "../ui/kit";

interface Props {
  bridge: Bridge;
  act: Act;
  busy: string | null;
}

// How often the screen re-reads the shell's folder document. It is slower than
// the window's own refresh: a copying file changes this document often, and the
// screen is not always open.
const FOLDERS_POLL_MS = 2000;

// Folders is the computer folder sync screen.
//
// It renders what the sync engine said and offers only the buttons that would
// do something: no Resume for a pause the dashboard owns, and no Pause for a
// folder ReelBolt already paused. Every sentence about WHY comes from the
// engine, so this screen and the dashboard cannot disagree.
export function Folders({ bridge, act, busy }: Props) {
  const [doc, setDoc] = useState<FoldersView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setDoc(await bridge.folders());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [bridge]);

  useEffect(() => {
    void refresh();
    const t = setInterval(() => void refresh(), FOLDERS_POLL_MS);
    return () => clearInterval(t);
  }, [refresh]);

  const run = useCallback(
    async (action: string, arg?: string) => {
      await act(action, arg);
      // The action has already refreshed the window's view; this screen's own
      // document is fetched again so a pause shows immediately.
      await refresh();
      setConfirming(null);
    },
    [act, refresh],
  );

  const links = doc?.links ?? [];
  const addButton = (
    <Button
      leftSection={<IconFolderPlus size={16} stroke={1.6} />}
      disabled={!doc?.ready || !doc?.pickerAvailable}
      loading={busy === "folders-add"}
      onClick={() => void run("folders-add")}
    >
      Add a folder
    </Button>
  );

  return (
    <Page
      title="Folders"
      lead="Files you put in these folders are copied into a ReelBolt project automatically. ReelBolt never changes or deletes anything on this computer."
      actions={links.length > 0 ? addButton : undefined}
    >
      {error && !doc ? (
        <div style={{ marginTop: 20 }}>
          <Notice tone="bad" title="Your folders could not be loaded">
            {error}
          </Notice>
        </div>
      ) : null}

      {!doc && !error ? (
        <div style={{ marginTop: 28, display: "grid", gap: 10 }}>
          <Skeleton height={72} radius="lg" />
          <Skeleton height={72} radius="lg" />
        </div>
      ) : null}

      {doc ? (
        <>
          {doc.problem || !doc.pickerAvailable ? (
            <div style={{ display: "grid", gap: 8, marginTop: 20 }}>
              {doc.problem ? (
                <Notice tone="warn" title="Folder sync is not running">
                  {doc.problem}
                </Notice>
              ) : null}
              {!doc.pickerAvailable ? (
                <Notice tone="plain" title="Folder picker not available">
                  This computer cannot show a folder picker, so folders cannot be added from this window.
                </Notice>
              ) : null}
            </div>
          ) : null}

          {links.length === 0 ? (
            <div className="rb-surface rb-enter" style={{ marginTop: 28, padding: "40px 32px", textAlign: "center" }}>
              <IconFolder size={30} stroke={1.4} style={{ color: "var(--rb-text-faint)" }} aria-hidden />
              <div className="rb-row-label" style={{ marginTop: 12 }}>No folders yet</div>
              <div className="rb-row-help" style={{ marginInline: "auto", maxWidth: 420 }}>
                Pick a folder, for example the one your camera or phone saves to, and choose the project
                its files should go to.
              </div>
              <Group justify="center" mt="lg">
                {addButton}
              </Group>
            </div>
          ) : (
            <Section
              title="Synced folders"
              aside={doc.summary && !doc.problem ? (
                <span className="rb-faint" style={{ fontSize: 13 }}>{doc.summary.replace(/^Folder sync:\s*/, "")}</span>
              ) : undefined}
            >
              <Surface>
                {links.map((link) => (
                  <FolderRow
                    key={link.linkId}
                    link={link}
                    busy={busy}
                    confirming={confirming === link.linkId}
                    onAskRemove={() => setConfirming(link.linkId)}
                    onCancelRemove={() => setConfirming(null)}
                    run={run}
                  />
                ))}
              </Surface>
            </Section>
          )}
        </>
      ) : null}
    </Page>
  );
}

interface RowProps {
  link: FolderLink;
  busy: string | null;
  confirming: boolean;
  onAskRemove: () => void;
  onCancelRemove: () => void;
  run: (action: string, arg?: string) => Promise<void>;
}

function FolderRow({ link, busy, confirming, onAskRemove, onCancelRemove, run }: RowProps) {
  const files = folderFilesLabel(link);
  const changed = folderLastChange(link);
  const skipped = folderSkipSummary(link);
  const projectName = link.projectName || "a project";
  const status = folderStatus(link);
  const is = (action: string) => busy === action + ":" + link.linkId;
  const facts = [files, changed ? "last change " + changed : ""].filter(Boolean).join("  ·  ");

  return (
    <div className="rb-folder">
      <div style={{ display: "grid", gridTemplateColumns: "32px minmax(0,1fr) auto", gap: 14, alignItems: "start" }}>
        <div className="rb-job-icon" aria-hidden>
          <IconFolder size={17} stroke={1.6} />
        </div>
        <div style={{ minWidth: 0 }}>
          <div className="rb-job-title">{folderName(link.localPath)}</div>
          <div className="rb-job-meta" title={link.localPath}>
            Into <span style={{ color: "var(--rb-text)" }}>{projectName}</span>, folder {link.targetFolder}
          </div>
          {status !== link.headline ? (
            <div className="rb-row-help" style={{ marginTop: 6 }}>{status}</div>
          ) : null}
          {facts ? <div className="rb-job-meta rb-num" style={{ marginTop: 4 }}>{facts}</div> : null}
        </div>
        <Group gap={6} wrap="nowrap">
          <Chip tone={folderChipTone(link.state)}>{link.headline}</Chip>
          <Menu position="bottom-end" withinPortal shadow="sm">
            <Menu.Target>
              <ActionIcon variant="subtle" color="gray" aria-label={"More for " + folderName(link.localPath)}>
                <IconDots size={16} stroke={1.6} />
              </ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item
                leftSection={<IconExternalLink size={15} stroke={1.6} />}
                onClick={() => void run("folders-open-project", link.linkId)}
              >
                Open project
              </Menu.Item>
              {folderCanPause(link) ? (
                <Menu.Item
                  leftSection={<IconPlayerPause size={15} stroke={1.6} />}
                  onClick={() => void run("folders-pause", link.linkId)}
                >
                  Pause syncing
                </Menu.Item>
              ) : null}
              <Menu.Divider />
              <Menu.Item color="red" leftSection={<IconTrash size={15} stroke={1.6} />} onClick={onAskRemove}>
                Stop syncing
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </Group>
      </div>

      {folderCanResumeHere(link) ? (
        <div style={{ marginTop: 12, marginLeft: 46 }}>
          <Button
            size="xs"
            leftSection={<IconPlayerPlay size={14} stroke={1.6} />}
            loading={is("folders-resume")}
            onClick={() => void run("folders-resume", link.linkId)}
          >
            {folderNeedsDeleteConfirm(link) ? "Resume and remove deleted files" : "Resume"}
          </Button>
        </div>
      ) : null}

      {skipped ? (
        <div style={{ marginTop: 12, marginLeft: 46 }}>
          <Notice tone="plain">
            {skipped}
            {link.skipped && link.skipped[0]?.examples?.length ? (
              <span className="rb-faint"> For example: {link.skipped[0].examples.join(", ")}</span>
            ) : null}
          </Notice>
        </div>
      ) : null}

      {link.note ? (
        <div className="rb-row-help rb-faint" style={{ marginTop: 8, marginLeft: 46 }}>
          {link.note}
        </div>
      ) : null}

      {/* The confirmation is inline rather than a modal: it is one promise to
          read ("the files already copied stay in the project"), and a modal
          would hide the folder it is about. */}
      {confirming ? (
        <div style={{ marginTop: 12, marginLeft: 46 }}>
          <Notice
            tone="bad"
            title="Stop syncing this folder?"
            action={
              <Group gap="xs">
                <Button size="xs" color="red" loading={is("folders-remove")} onClick={() => void run("folders-remove", link.linkId)}>
                  Stop syncing
                </Button>
                <Button size="xs" variant="default" onClick={onCancelRemove}>
                  Keep syncing
                </Button>
              </Group>
            }
          >
            Files already copied stay in the project. New files in this folder will not be copied.
          </Notice>
        </div>
      ) : null}
    </div>
  );
}
