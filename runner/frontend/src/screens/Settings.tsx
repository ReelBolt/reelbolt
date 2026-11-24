import { useEffect, useMemo, useState } from "react";
import {
  Anchor, Button, Collapse, Group, Modal, NumberInput, SegmentedControl, Select, Slider, Switch,
  TextInput,
} from "@mantine/core";
import {
  IconBatteryCharging, IconCalendarTime, IconClockPause, IconDownload, IconFolderOpen, IconInfinity,
  IconPlus, IconRefresh, IconTrash, IconX,
} from "@tabler/icons-react";
import type { Act } from "../App";
import type { Bridge, SaveResult } from "../shell/bridge";
import { DAYS, formatBytes, formatWhen, normalizeDay, scheduleSummary } from "../shell/derive";
import type { LimitsConfig, Schedule, SettingsPatch, TelemetryPreview, View } from "../shell/types";
import { LocalModelSection } from "./LocalModel";
import { Facts, Notice, Page, Row, Section, Surface } from "../ui/kit";

interface Props {
  view: View;
  bridge: Bridge;
  act: Act;
  busy: string | null;
  onSaved: (res: SaveResult) => void;
}

const GB = 1024 ** 3;

// Settings is everything a person can change about how this computer works for
// ReelBolt. Each group saves on its own, so a mistake in one never throws away
// a change made in another.
export function Settings({ view, bridge, act, busy, onSaved }: Props) {
  const save = async (patch: SettingsPatch): Promise<boolean> => {
    try {
      const res = await bridge.saveSettings(patch);
      onSaved(res);
      return res.ok;
    } catch (err) {
      onSaved({ ok: false, message: err instanceof Error ? err.message : String(err) });
      return false;
    }
  };

  return (
    <Page title="Settings">
      <nav className="rb-jump" aria-label="Settings sections">
        <a href="#computer">This computer</a>
        <a href="#schedule">When to work</a>
        <a href="#resources">How much to use</a>
        <a href="#storage">Storage</a>
        <a href="#local-model">Local model</a>
        <a href="#general">Startup and updates</a>
        <a href="#privacy">Privacy</a>
      </nav>

      <ComputerSection view={view} act={act} busy={busy} />
      <ScheduleSection view={view} save={save} />
      <ResourcesSection view={view} save={save} />
      <StorageSection view={view} act={act} busy={busy} save={save} />
      <LocalModelSection bridge={bridge} paused={view.state === "paused"} />
      <GeneralSection view={view} act={act} busy={busy} save={save} />
      <PrivacySection view={view} bridge={bridge} act={act} busy={busy} save={save} />
    </Page>
  );
}

type Save = (patch: SettingsPatch) => Promise<boolean>;

// ---- this computer ----

function ComputerSection({ view, act, busy }: { view: View; act: Act; busy: string | null }) {
  const [confirming, setConfirming] = useState(false);
  const [forgetKey, setForgetKey] = useState(false);
  const d = view.device;

  return (
    <Section id="computer" title="This computer">
      <Surface>
        {d.paired ? (
          <>
            <Facts
              items={[
                { label: "Name", value: d.name || "This computer" },
                { label: "Workspace", value: d.workspaceName || "Connected" },
                { label: "Connected by", value: d.pairedByEmail || "Not known yet" },
                { label: "App version", value: <span className="rb-num">{view.version}</span> },
              ]}
            />
            <Row
              label="Remove from workspace"
              help="This computer stops taking jobs and disappears from your workspace's list of computers. Your folders stop syncing. Files already in ReelBolt stay there."
              control={
                <Button variant="default" color="red" onClick={() => setConfirming(true)}>
                  Remove
                </Button>
              }
            />
          </>
        ) : (
          <Row
            label="Not connected"
            help="Connect this computer to a workspace from Home."
          />
        )}
      </Surface>

      <Modal
        opened={confirming}
        onClose={() => setConfirming(false)}
        title="Remove this computer from your workspace?"
        centered
        radius="lg"
      >
        <div className="rb-row-help" style={{ maxWidth: "none" }}>
          It stops taking jobs right away and your synced folders stop updating. You can connect it again
          at any time from Home.
        </div>
        <Switch
          mt="md"
          size="sm"
          checked={forgetKey}
          onChange={(e) => setForgetKey(e.currentTarget.checked)}
          label="Also delete this computer's saved identity"
          description="Choose this if you are giving the computer to someone else."
        />
        <Group justify="flex-end" mt="xl">
          <Button variant="default" onClick={() => setConfirming(false)}>
            Cancel
          </Button>
          <Button
            color="red"
            loading={busy?.startsWith("unpair") ?? false}
            onClick={async () => {
              await act("unpair", forgetKey ? "forget-key" : undefined);
              setConfirming(false);
            }}
          >
            Remove computer
          </Button>
        </Group>
      </Modal>
    </Section>
  );
}

// ---- when to work ----

type Mode = "always" | "idle" | "ac" | "windows";

const MODES: { id: Mode; title: string; help: string; Icon: typeof IconInfinity }[] = [
  { id: "always", title: "Any time", help: "Take jobs whenever the app is open.", Icon: IconInfinity },
  { id: "idle", title: "When I'm away", help: "Only after you stop using the computer for a while.", Icon: IconClockPause },
  { id: "ac", title: "When plugged in", help: "Only while the computer is on mains power.", Icon: IconBatteryCharging },
  { id: "windows", title: "During set hours", help: "Only inside the hours you choose below.", Icon: IconCalendarTime },
];

interface Window {
  days: string[];
  start: string;
  end: string;
}

function toMode(m: string | undefined): Mode {
  const v = (m || "always").toLowerCase();
  return v === "idle" || v === "ac" || v === "windows" ? v : "always";
}

function cloneSchedule(s: Schedule): { mode: Mode; idleMinutes: number; windows: Window[]; pauseOnBattery: boolean } {
  return {
    mode: toMode(s.mode),
    idleMinutes: s.idleMinutes && s.idleMinutes > 0 ? s.idleMinutes : 5,
    windows: (s.windows ?? []).map((w) => ({
      days: (w.days ?? []).map(normalizeDay).filter(Boolean),
      start: w.start,
      end: w.end,
    })),
    pauseOnBattery: (s.onBattery ?? "allow") === "pause",
  };
}

function ScheduleSection({ view, save }: { view: View; save: Save }) {
  const original = useMemo(() => cloneSchedule(view.settings.schedule), [view.settings.schedule]);
  const [draft, setDraft] = useState(original);
  const [saving, setSaving] = useState(false);
  const [limitHours, setLimitHours] = useState(original.windows.length > 0);

  useEffect(() => {
    setDraft(original);
    setLimitHours(original.windows.length > 0);
  }, [original]);

  const effectiveWindows = draft.mode === "windows" || limitHours ? draft.windows : [];
  const next: Schedule = {
    mode: draft.mode,
    idleMinutes: draft.idleMinutes,
    windows: effectiveWindows.map((w) => ({ days: w.days.length === 7 ? [] : w.days, start: w.start, end: w.end })),
    onBattery: draft.pauseOnBattery ? "pause" : "allow",
  };
  const dirty = JSON.stringify(next) !== JSON.stringify({
    mode: original.mode,
    idleMinutes: original.idleMinutes,
    windows: original.windows.map((w) => ({ days: w.days.length === 7 ? [] : w.days, start: w.start, end: w.end })),
    onBattery: original.pauseOnBattery ? "pause" : "allow",
  });
  const needsWindow = draft.mode === "windows" && draft.windows.length === 0;
  const badWindow = effectiveWindows.some((w) => !/^\d{2}:\d{2}$/.test(w.start) || !/^\d{2}:\d{2}$/.test(w.end) || w.start === w.end);

  const setWindow = (i: number, w: Partial<Window>) =>
    setDraft((d) => ({ ...d, windows: d.windows.map((x, j) => (j === i ? { ...x, ...w } : x)) }));
  const addWindow = () =>
    setDraft((d) => ({ ...d, windows: [...d.windows, { days: ["mon", "tue", "wed", "thu", "fri"], start: "18:00", end: "08:00" }] }));

  const showHours = draft.mode === "windows" || limitHours;

  return (
    <Section
      id="schedule"
      title="When to work"
      lead={"Right now: " + scheduleSummary(view.settings.schedule) + ". Pausing from Home always wins over this schedule."}
    >
      <Surface>
        <div style={{ padding: 16 }}>
          <div className="rb-modes" role="radiogroup" aria-label="When to take jobs">
            {MODES.map(({ id, title, help, Icon }) => (
              <button
                key={id}
                type="button"
                role="radio"
                aria-checked={draft.mode === id}
                className="rb-mode"
               
                onClick={() => {
                  setDraft((d) => ({ ...d, mode: id, windows: id === "windows" && d.windows.length === 0 ? [{ days: ["mon", "tue", "wed", "thu", "fri"], start: "18:00", end: "08:00" }] : d.windows }));
                }}
              >
                <Icon className="rb-mode-icon" size={18} stroke={1.6} aria-hidden />
                <span>
                  <span className="rb-mode-title" style={{ display: "block" }}>{title}</span>
                  <span className="rb-mode-help" style={{ display: "block" }}>{help}</span>
                </span>
              </button>
            ))}
          </div>
        </div>

        {draft.mode === "idle" ? (
          <Row
            label="Away for"
            help="How long the computer must be idle before a job can start."
            control={
              <NumberInput
                w={130}
                min={1}
                max={240}
                suffix=" min"
                value={draft.idleMinutes}
                onChange={(v) => setDraft((d) => ({ ...d, idleMinutes: typeof v === "number" && v >= 1 ? v : 1 }))}
                aria-label="Minutes away"
              />
            }
          />
        ) : null}

        {draft.mode !== "windows" ? (
          <Row
            label="Only during certain hours"
            help="Add hours on top of the choice above, for example weekday evenings only."
            control={
              <Switch
                checked={limitHours}
               
                onChange={(e) => {
                  const on = e.currentTarget.checked;
                  setLimitHours(on);
                  if (on && draft.windows.length === 0) addWindow();
                }}
                aria-label="Only during certain hours"
              />
            }
          />
        ) : null}

        <Collapse expanded={showHours}>
          <div className="rb-row rb-row-stack">
            <div>
              {draft.windows.map((w, i) => (
                <div className="rb-window" key={i}>
                  <DayPicker days={w.days} onChange={(days) => setWindow(i, { days })} />
                  <Group gap={6} wrap="nowrap">
                    <TextInput
                      type="time"
                      w={132}
                      value={w.start}
                      onChange={(e) => setWindow(i, { start: e.currentTarget.value })}
                      aria-label="From"
                    />
                    <span className="rb-muted" style={{ fontSize: 13 }}>to</span>
                    <TextInput
                      type="time"
                      w={132}
                      value={w.end}
                      onChange={(e) => setWindow(i, { end: e.currentTarget.value })}
                      aria-label="Until"
                    />
                  </Group>
                  {draft.windows.length > 1 || draft.mode !== "windows" ? (
                    <Button
                      variant="subtle"
                      color="gray"
                      size="compact-sm"
                      aria-label="Remove these hours"
                      onClick={() => setDraft((d) => ({ ...d, windows: d.windows.filter((_, j) => j !== i) }))}
                    >
                      <IconX size={15} stroke={1.6} />
                    </Button>
                  ) : null}
                </div>
              ))}
              <Button
                mt={8}
                variant="subtle"
                size="compact-sm"
                leftSection={<IconPlus size={14} stroke={1.6} />}
                onClick={addWindow}
              >
                Add hours
              </Button>
              <div className="rb-row-help rb-faint" style={{ marginTop: 6 }}>
                An end time earlier than the start time runs overnight into the next morning.
              </div>
            </div>
          </div>
        </Collapse>

        {draft.mode !== "ac" ? (
          <Row
            label="Pause on battery"
            help="Stop taking jobs when a laptop is unplugged."
            control={
              <Switch
                checked={draft.pauseOnBattery}
               
                onChange={(e) => {
                  const on = e.currentTarget.checked;
                  setDraft((d) => ({ ...d, pauseOnBattery: on }));
                }}
                aria-label="Pause on battery"
              />
            }
          />
        ) : null}

        {dirty ? (
          <div className="rb-savebar">
            {needsWindow ? <span className="rb-muted" style={{ fontSize: 13, marginRight: "auto" }}>Add at least one set of hours.</span> : null}
            {badWindow ? <span className="rb-muted" style={{ fontSize: 13, marginRight: "auto" }}>Each set of hours needs a different start and end time.</span> : null}
            <Button variant="default" onClick={() => { setDraft(original); setLimitHours(original.windows.length > 0); }}>
              Undo
            </Button>
            <Button
              loading={saving}
              disabled={needsWindow || badWindow}
              onClick={async () => {
                setSaving(true);
                await save({ schedule: next });
                setSaving(false);
              }}
            >
              Save schedule
            </Button>
          </div>
        ) : null}
      </Surface>
    </Section>
  );
}

const DAY_SHORT: Record<string, string> = { mon: "M", tue: "T", wed: "W", thu: "T", fri: "F", sat: "S", sun: "S" };
const DAY_LONG: Record<string, string> = {
  mon: "Monday", tue: "Tuesday", wed: "Wednesday", thu: "Thursday", fri: "Friday", sat: "Saturday", sun: "Sunday",
};

function DayPicker({ days, onChange }: { days: string[]; onChange: (days: string[]) => void }) {
  // An empty list means every day, which is how runner.json writes it.
  const set = new Set(days.length === 0 ? DAYS : days);
  return (
    <div className="rb-days" role="group" aria-label="Days">
      {DAYS.map((d) => (
        <button
          key={d}
          type="button"
          className="rb-day"
          aria-pressed={set.has(d)}
          aria-label={DAY_LONG[d]}
          title={DAY_LONG[d]}
          onClick={() => {
            const next = new Set(set);
            if (next.has(d)) next.delete(d);
            else next.add(d);
            if (next.size === 0) return; // at least one day
            onChange(DAYS.filter((x) => next.has(x)));
          }}
        >
          {DAY_SHORT[d]}
        </button>
      ))}
    </div>
  );
}

// ---- how much to use ----

const PRIORITIES: { value: LimitsConfig["priority"]; label: string }[] = [
  { value: "normal", label: "Normal" },
  { value: "below-normal", label: "Lower" },
  { value: "low", label: "Lowest" },
];

function ResourcesSection({ view, save }: { view: View; save: Save }) {
  const host = view.settings.host;
  const cfg = view.settings.limits;
  const original = useMemo(
    () => ({
      slots: view.settings.slots,
      memoryGb: round1((cfg?.memory || view.limits.memoryBytes || 4 * GB) / GB),
      cpus: cfg?.cpus || view.limits.cpus || 2,
      priority: (cfg?.priority || (view.limits.priority as LimitsConfig["priority"]) || "below-normal"),
      diskGb: Math.round((cfg?.diskQuotaBytes || view.limits.diskQuotaBytes || 8 * GB) / GB),
    }),
    [view.settings.slots, cfg, view.limits],
  );
  const [draft, setDraft] = useState(original);
  const [saving, setSaving] = useState(false);
  useEffect(() => setDraft(original), [original]);

  const maxCpus = Math.max(1, host?.cpus || 16);
  const maxMemGb = host?.memoryBytes ? Math.max(1, Math.floor((host.memoryBytes * 0.8) / GB)) : 32;
  const dirty = JSON.stringify(draft) !== JSON.stringify(original);

  const totalMem = draft.memoryGb * draft.slots;
  const heavy = host?.memoryBytes ? totalMem * GB > host.memoryBytes * 0.8 : false;

  return (
    <Section
      id="resources"
      title="How much to use"
      lead="Keep these low on a computer you are working on, and higher on one that sits idle. Each job gets these limits on its own."
    >
      <Surface>
        <Row
          label="Jobs at the same time"
          help="One is best for a laptop. More only helps on a powerful computer."
          control={
            <NumberInput
              w={110}
              min={1}
              max={16}
              value={draft.slots}
             
              onChange={(v) => setDraft((d) => ({ ...d, slots: typeof v === "number" && v >= 1 ? Math.min(16, v) : 1 }))}
              aria-label="Jobs at the same time"
            />
          }
        />
        <Row
          stack
          label={<Group justify="space-between"><span>Memory per job</span><span className="rb-num rb-muted">{draft.memoryGb} GB</span></Group>}
          help={host?.memoryBytes ? "This computer has " + formatBytes(host.memoryBytes) + " of memory." : undefined}
        >
          <Slider
            min={1}
            max={maxMemGb}
            step={maxMemGb > 16 ? 1 : 0.5}
            value={Math.min(draft.memoryGb, maxMemGb)}
           
            onChange={(v) => setDraft((d) => ({ ...d, memoryGb: v }))}
            label={(v) => v + " GB"}
            aria-label="Memory per job"
            mb={4}
          />
        </Row>
        <Row
          stack
          label={<Group justify="space-between"><span>Processor cores per job</span><span className="rb-num rb-muted">{draft.cpus}</span></Group>}
          help={host?.cpus ? "This computer has " + host.cpus + " cores." : undefined}
        >
          <Slider
            min={1}
            max={maxCpus}
            step={1}
            value={Math.min(draft.cpus, maxCpus)}
           
            onChange={(v) => setDraft((d) => ({ ...d, cpus: v }))}
            aria-label="Processor cores per job"
            mb={4}
          />
        </Row>
        <Row
          label="Priority"
          help="Lower keeps your own apps responsive while a job runs."
          control={
            <SegmentedControl
              value={draft.priority}
             
              onChange={(v) => setDraft((d) => ({ ...d, priority: v as LimitsConfig["priority"] }))}
              data={PRIORITIES}
            />
          }
        />
        <Row
          label="Disk space per job"
          help="Room for the files one job works on. Big footage needs more."
          control={
            <NumberInput
              w={120}
              min={1}
              max={2048}
              suffix=" GB"
              value={draft.diskGb}
             
              onChange={(v) => setDraft((d) => ({ ...d, diskGb: typeof v === "number" && v >= 1 ? v : 1 }))}
              aria-label="Disk space per job"
            />
          }
        />
        {(view.limits.notes ?? []).length > 0 ? (
          <div style={{ padding: "0 20px 16px" }}>
            <Notice tone="plain">Some values were lowered to what this computer has.</Notice>
          </div>
        ) : null}
        {dirty ? (
          <div className="rb-savebar">
            {heavy ? (
              <span className="rb-muted" style={{ fontSize: 13, marginRight: "auto" }}>
                {draft.slots} jobs at {draft.memoryGb} GB each could use most of this computer's memory.
              </span>
            ) : null}
            <Button variant="default" onClick={() => setDraft(original)}>
              Undo
            </Button>
            <Button
              loading={saving}
             
              onClick={async () => {
                setSaving(true);
                const patch: SettingsPatch = {};
                if (draft.slots !== original.slots) patch.slots = draft.slots;
                const limits: Partial<LimitsConfig> = {};
                if (draft.memoryGb !== original.memoryGb) limits.memory = Math.round(draft.memoryGb * GB);
                if (draft.cpus !== original.cpus) limits.cpus = draft.cpus;
                if (draft.priority !== original.priority) limits.priority = draft.priority;
                if (draft.diskGb !== original.diskGb) limits.diskQuotaBytes = Math.round(draft.diskGb * GB);
                if (Object.keys(limits).length > 0) patch.limits = limits;
                await save(patch);
                setSaving(false);
              }}
            >
              Save
            </Button>
          </div>
        ) : null}
      </Surface>
    </Section>
  );
}

function round1(n: number): number {
  return Math.round(n * 2) / 2;
}

// ---- storage ----

const CACHE_SIZES = [2, 5, 10, 20, 50, 100];

function StorageSection({ view, act, busy, save }: {
  view: View; act: Act; busy: string | null; save: Save;
}) {
  const media = view.cache.media;
  const cap = view.settings.mediaCacheMaxBytes ?? media?.maxBytes ?? 0;
  const pct = media && media.maxBytes > 0 ? Math.min(100, (media.bytes / media.maxBytes) * 100) : 0;
  const options = [...new Set([...CACHE_SIZES, cap > 0 ? Math.round(cap / GB) : 10])].sort((a, b) => a - b);

  return (
    <Section
      id="storage"
      title="Storage"
      lead="Footage a job downloads is kept on this computer so the next job using it starts right away. The oldest files are removed when the limit is reached."
    >
      <Surface>
        <Row
          stack
          label={
            <Group justify="space-between">
              <span>Downloaded footage</span>
              <span className="rb-num rb-muted" style={{ fontWeight: 400, fontSize: 13 }}>
                {media ? formatBytes(media.bytes) + " of " + formatBytes(media.maxBytes) : "Not used yet"}
              </span>
            </Group>
          }
          help={
            media && media.savedBytes > 0
              ? "Saved " + formatBytes(media.savedBytes) + " of downloads since the app started."
              : undefined
          }
        >
          <div className="rb-usage" role="img" aria-label={Math.round(pct) + "% used"}>
            <span style={{ width: pct + "%" }} />
          </div>
        </Row>
        <Row
          label="Keep up to"
          help="Set it lower if this computer is short on space."
          control={
            <Group gap="sm" wrap="nowrap">
              <Select
                w={120}
               
                value={String(cap > 0 ? Math.round(cap / GB) : 10)}
                data={options.map((g) => ({ value: String(g), label: g + " GB" }))}
                onChange={(v) => v && void save({ mediaCacheMaxBytes: Number(v) * GB })}
                aria-label="Keep up to"
                allowDeselect={false}
              />
              <Button
                variant="default"
                leftSection={<IconTrash size={15} stroke={1.6} />}
                disabled={!media || media.entries === 0}
                loading={busy === "clear-media-cache"}
                onClick={() => void act("clear-media-cache")}
              >
                Empty
              </Button>
            </Group>
          }
        />
        <Row
          label="Leftover job files"
          help={
            view.cache.bytes > 0
              ? formatBytes(view.cache.bytes) + " left behind by jobs that ended. Safe to remove while nothing is running."
              : "Nothing left behind."
          }
          control={
            <Button
              variant="default"
              disabled={view.cache.bytes === 0 || !!view.currentJob}
              loading={busy === "clear-cache"}
              onClick={() => void act("clear-cache")}
            >
              Clean up
            </Button>
          }
        />
      </Surface>
    </Section>
  );
}

// ---- startup and updates ----

function GeneralSection({ view, act, busy, save }: {
  view: View; act: Act; busy: string | null; save: Save;
}) {
  const s = view.settings;
  const u = view.update;
  return (
    <Section id="general" title="Startup and updates">
      <Surface>
        <Row
          label="Open when I sign in"
          help={
            s.autostartKnown
              ? "Starts quietly in the system tray, without opening this window."
              : "This option is not available on this computer."
          }
          control={
            <Switch
              checked={s.autostart}
              disabled={!s.autostartKnown}
              onChange={(e) => void save({ autostart: e.currentTarget.checked })}
              aria-label="Open when I sign in"
            />
          }
        />
        <Row
          label="Install updates automatically"
          help="Updates only install when no job is running."
          control={
            <Switch
              checked={s.autoUpdate}
             
              onChange={(e) => void save({ autoUpdate: e.currentTarget.checked })}
              aria-label="Install updates automatically"
            />
          }
        />
        <Row
          label="Get early versions"
          help="Try new features before everyone else. They may have more bugs."
          control={
            <Switch
              checked={s.updateChannel === "beta"}
             
              onChange={(e) => void save({ updateChannel: e.currentTarget.checked ? "beta" : "stable" })}
              aria-label="Get early versions"
            />
          }
        />
        <Row
          label={"Version " + view.version}
          help={updateSentence(view)}
          control={
            <Button
              variant="default"
              leftSection={<IconRefresh size={15} stroke={1.6} />}
             
              loading={busy === "check-for-update"}
              onClick={() => void act("check-for-update")}
            >
              Check now
            </Button>
          }
        />
        {u?.stopTakingJobs ? (
          <div style={{ padding: "0 20px 16px" }}>
            <Notice tone="bad" title="Update needed">
              This version is too old to take jobs. Install the update to keep working.
            </Notice>
          </div>
        ) : null}
      </Surface>
    </Section>
  );
}

function updateSentence(view: View): string {
  const u = view.update;
  if (!u) return "Not checked yet.";
  const when = u.checkedAt ? " Checked " + formatWhen(u.checkedAt) + "." : "";
  if (u.restartRequired) return "An update is installed. Quit and reopen the app to finish." + when;
  if (u.releaseVersion && u.releaseVersion !== u.currentVersion && u.action !== "none") {
    return "Version " + u.releaseVersion + " is available." + when;
  }
  return "You have the latest version." + when;
}

// ---- privacy ----

function PrivacySection({ view, bridge, act, busy, save }: {
  view: View; bridge: Bridge; act: Act; busy: string | null; save: Save;
}) {
  const t = view.settings.telemetry;
  const [open, setOpen] = useState(false);
  const [payload, setPayload] = useState<TelemetryPreview | null>(null);
  const [payloadError, setPayloadError] = useState<string | null>(null);
  const [raw, setRaw] = useState(false);

  useEffect(() => {
    if (!open || payload) return;
    let live = true;
    bridge
      .telemetryPreview()
      .then((p) => live && setPayload(p))
      .catch((err) => live && setPayloadError(err instanceof Error ? err.message : String(err)));
    return () => {
      live = false;
    };
  }, [open, payload, bridge]);

  return (
    <Section id="privacy" title="Privacy">
      <Surface>
        <Row
          label="Send crash reports"
          help={
            <>
              If the app crashes or a job cannot start, send ReelBolt a short report so we can fix it.
              Reports never include your files, names or projects.{" "}
              <Anchor component="button" size="sm" onClick={() => setOpen(true)}>
                See what is sent
              </Anchor>
            </>
          }
          control={
            <Switch
              checked={t.enabled}
             
              onChange={(e) => void save({ telemetry: e.currentTarget.checked })}
              aria-label="Send crash reports"
            />
          }
        />
        {t.enabled && !t.available ? (
          <div style={{ padding: "0 20px 16px" }}>
            <Notice tone="plain">
              Your choice is saved. This version cannot send reports yet, so nothing leaves your computer.
            </Notice>
          </div>
        ) : null}
        {t.enabled && t.sent + t.failed > 0 ? (
          <div style={{ padding: "0 20px 16px" }} className="rb-row-help">
            {t.sent} {t.sent === 1 ? "report" : "reports"} sent{t.failed ? ", " + t.failed + " could not be sent" : ""}.
          </div>
        ) : null}
        <Row
          label="Diagnostics file"
          help="Saves a file you can attach when you contact support. Your name, email and folders are removed from it, and it is never uploaded for you."
          control={
            <Group gap="xs" wrap="nowrap">
              <Button
                variant="default"
                leftSection={<IconDownload size={15} stroke={1.6} />}
               
                loading={busy === "export-diagnostics"}
                onClick={() => void act("export-diagnostics")}
              >
                Save
              </Button>
              <Button
                variant="subtle"
                color="gray"
                leftSection={<IconFolderOpen size={15} stroke={1.6} />}
                disabled={!t.logsDir}
                onClick={() => void act("open-diagnostics")}
              >
                Show
              </Button>
            </Group>
          }
        />
      </Surface>

      <Modal opened={open} onClose={() => setOpen(false)} title="What a crash report contains" size="lg" centered radius="lg">
        {payloadError ? <Notice tone="bad">{payloadError}</Notice> : null}
        {!payload && !payloadError ? <div className="rb-row-help">Loading…</div> : null}
        {payload ? (
          <div style={{ display: "grid", gap: 16 }}>
            <div>
              <div className="rb-row-label">Sent</div>
              <ul className="rb-row-help" style={{ margin: "6px 0 0", paddingLeft: 18, maxWidth: "none" }}>
                {payload.fields.map((f) => (
                  <li key={f.name}>{f.note}</li>
                ))}
              </ul>
            </div>
            <div>
              <div className="rb-row-label">Never sent</div>
              <ul className="rb-row-help" style={{ margin: "6px 0 0", paddingLeft: 18, maxWidth: "none" }}>
                {payload.neverSent.map((n) => (
                  <li key={n}>{n}</li>
                ))}
              </ul>
            </div>
            <div>
              <Anchor component="button" size="sm" onClick={() => setRaw((r) => !r)}>
                {raw ? "Hide the exact report" : "Show the exact report"}
              </Anchor>
              <Collapse expanded={raw}>
                <div style={{ display: "grid", gap: 10, marginTop: 10 }}>
                  <div className="rb-row-help">If the app crashes:</div>
                  <pre className="rb-pre">{JSON.stringify(payload.crash, null, 2)}</pre>
                  <div className="rb-row-help">If a job cannot start:</div>
                  <pre className="rb-pre">{JSON.stringify(payload.jobFailure, null, 2)}</pre>
                </div>
              </Collapse>
            </div>
          </div>
        ) : null}
      </Modal>
    </Section>
  );
}
