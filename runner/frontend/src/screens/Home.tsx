import { useEffect, useState } from "react";
import { Anchor, Button, Group } from "@mantine/core";
import {
  IconCheck, IconChevronRight, IconDownload, IconExternalLink, IconPlayerPause, IconPlayerPlay, IconRefresh,
} from "@tabler/icons-react";
import type { Act } from "../App";
import { remedyFor } from "../App";
import type { Bridge } from "../shell/bridge";
import {
  canRender, capabilityChips, containerName, formatBytes, jobContext, jobRows, scheduleSummary,
  setupComplete, stageLabel, stateLabel, stateTone, statusSentence, type Screen,
} from "../shell/derive";
import type { PairingView, View } from "../shell/types";
import { JobRow } from "../ui/JobRow";
import { Facts, Meter, Notice, Section, StatePill, Surface } from "../ui/kit";

interface Props {
  view: View;
  bridge: Bridge;
  act: Act;
  busy: string | null;
  go: (screen: Screen) => void;
}

// Home answers the two questions a person opens this window with: is my
// computer working for ReelBolt right now, and if not, what do I do. Until the
// computer is fully set up, the setup steps sit directly under the status.
export function Home({ view, bridge, act, busy, go }: Props) {
  const ready = setupComplete(view);
  const askCrashReports = !view.settings.telemetry.asked;
  const recent = jobRows(view.jobs).filter((j) => !j.running).slice(0, 3);

  return (
    <div className="rb-page">
      <h1 className="rb-visually-hidden">Home</h1>
      <div>
        <StatusCard view={view} act={act} busy={busy} go={go} />
      </div>

      {(view.warnings ?? []).length > 0 ? (
        <div style={{ display: "grid", gap: 8, marginTop: 12 }}>
          {(view.warnings ?? []).map((w) => (
            <Notice key={w} tone="warn">
              {w}
            </Notice>
          ))}
        </div>
      ) : null}

      {!ready || askCrashReports ? (
        <Section
          title={ready ? "One more thing" : "Set up this computer"}
          lead={
            ready
              ? undefined
              : "Your computer can render videos and process footage for your ReelBolt workspace, so they finish without waiting in the cloud queue."
          }
        >
          <Surface>
            {!ready ? (
              <>
                <ConnectStep view={view} bridge={bridge} act={act} busy={busy} />
                <RenderStep view={view} act={act} busy={busy} />
              </>
            ) : null}
            {askCrashReports ? <CrashReportStep act={act} busy={busy} index={ready ? 1 : 3} /> : null}
          </Surface>
        </Section>
      ) : null}

      {view.device.paired ? (
        <Section
          title="This computer"
          aside={
            <Anchor component="button" size="sm" onClick={() => go("settings")}>
              Change
            </Anchor>
          }
        >
          <Surface>
            <Facts
              items={[
                {
                  label: "Works for",
                  value: view.device.workspaceName || "Your workspace",
                  title: view.device.pairedByEmail ? "Connected by " + view.device.pairedByEmail : undefined,
                },
                { label: "Takes work", value: scheduleSummary(view.settings.schedule) },
                {
                  label: "At the same time",
                  value: view.settings.slots + (view.settings.slots === 1 ? " job" : " jobs"),
                },
                { label: "Can do", value: capabilityChips(view.runtime).slice(0, 2).join(", ") },
              ]}
            />
          </Surface>
        </Section>
      ) : null}

      {view.device.paired ? (
        <Section
          title="Recent jobs"
          aside={
            recent.length > 0 ? (
              <Anchor component="button" size="sm" onClick={() => go("activity")}>
                See all
              </Anchor>
            ) : undefined
          }
        >
          {recent.length === 0 ? (
            <Surface>
              <div style={{ padding: "22px 20px" }}>
                <div className="rb-row-label">Nothing yet</div>
                <div className="rb-row-help">
                  When a workflow in your workspace needs a render or footage processed, it shows up here.
                </div>
              </div>
            </Surface>
          ) : (
            <Surface>
              {recent.map((j) => (
                <JobRow key={j.id} job={j} onOpen={(job) => void act("open-job", job.id)} />
              ))}
            </Surface>
          )}
        </Section>
      ) : null}
    </div>
  );
}

// StatusCard is the live state: one word, one sentence, and the one or two
// buttons that change it.
function StatusCard({ view, act, busy, go }: { view: View; act: Act; busy: string | null; go: (s: Screen) => void }) {
  const tone = stateTone(view.state);
  const job = view.currentJob;
  const remedy = view.remedy && view.remedy.kind !== "none" ? view.remedy : null;
  // Connecting and setting up rendering have their own steps on this screen.
  const offerRemedy = remedy && remedy.kind !== "pair" && remedy.kind !== "runtime";
  const sentence = statusSentence(view);
  const canPause = view.runner.running && view.governor.canPause && !view.runner.held;
  const canResume = view.runner.running && view.runner.held && view.runner.heldBy !== "gateway";

  const onRemedy = () => {
    if (!remedy) return;
    if (remedy.kind === "schedule") {
      go("settings");
      return;
    }
    const action = remedyFor(view);
    if (action) void act(action);
  };

  return (
    <div className="rb-surface rb-hero" data-tone={tone}>
      <div>
        <StatePill tone={tone}>{stateLabel(view.state)}</StatePill>
        <h2 className="rb-hero-title">{view.headline}</h2>
        {sentence ? <p className="rb-hero-detail">{sentence}</p> : null}

        {job ? (
          <div style={{ marginTop: 18, maxWidth: 520 }}>
            <Group justify="space-between" gap="xs" mb={8} wrap="nowrap">
              <div style={{ minWidth: 0 }}>
                {job.title !== view.headline ? <div className="rb-job-title">{job.title}</div> : null}
                <div className="rb-job-meta">
                  {[jobContext(job), stageLabel(job.stage)].filter(Boolean).join("  ·  ") || "Working on it"}
                </div>
              </div>
              {job.percent != null ? (
                <span className="rb-num" style={{ fontWeight: 600, fontSize: 14 }}>{job.percent}%</span>
              ) : null}
            </Group>
            <Meter value={job.percent} label={"Progress of " + job.title} />
          </div>
        ) : null}

        {offerRemedy || canPause || canResume ? (
          <Group gap="sm" mt={20}>
            {offerRemedy ? (
              <Button onClick={onRemedy} loading={busy !== null && busy === remedyFor(view)}>
                {remedy!.label}
              </Button>
            ) : null}
            {canPause ? (
              <Button
                variant="default"
                leftSection={<IconPlayerPause size={16} stroke={1.6} />}
                loading={busy === "pause"}
                onClick={() => void act("pause")}
              >
                Pause
              </Button>
            ) : null}
            {canResume ? (
              <Button
                leftSection={<IconPlayerPlay size={16} stroke={1.6} />}
                loading={busy === "resume"}
                onClick={() => void act("resume")}
              >
                Resume
              </Button>
            ) : null}
          </Group>
        ) : null}
      </div>
    </div>
  );
}

// ---- setup steps ----

function Step({ index, title, done, current, children }: {
  index: number;
  title: string;
  done: boolean;
  current: boolean;
  children?: React.ReactNode;
}) {
  return (
    <div className="rb-step" data-done={done} data-current={current && !done}>
      <div className="rb-step-mark" aria-hidden>
        {done ? <IconCheck size={15} stroke={2} /> : index}
      </div>
      <div style={{ minWidth: 0 }}>
        <div className="rb-step-title">
          {title}
          {done ? <span className="rb-faint" style={{ fontWeight: 400 }}>  Done</span> : null}
        </div>
        {children ? <div style={{ marginTop: 4 }}>{children}</div> : null}
      </div>
    </div>
  );
}

function ConnectStep({ view, bridge, act, busy }: { view: View; bridge: Bridge; act: Act; busy: string | null }) {
  const [pairing, setPairing] = useState<PairingView | null>(null);
  const paired = view.device.paired && !view.device.revoked;

  useEffect(() => {
    if (paired) return;
    let live = true;
    const tick = async () => {
      try {
        const p = await bridge.pairing();
        if (live) setPairing(p);
      } catch {
        /* the shell is not answering; App already says so */
      }
    };
    void tick();
    const t = setInterval(() => void tick(), 1500);
    return () => {
      live = false;
      clearInterval(t);
    };
  }, [bridge, paired]);

  return (
    <Step index={1} title="Connect to your workspace" done={paired} current={!paired}>
      {paired ? (
        <div className="rb-row-help">
          Connected to {view.device.workspaceName || "your workspace"}
          {view.device.pairedByEmail ? " by " + view.device.pairedByEmail : ""}.
        </div>
      ) : (
        <div style={{ display: "grid", gap: 12 }}>
          {view.device.revoked ? (
            <Notice tone="warn" title="This computer was removed from your workspace">
              Connect it again to keep working.
            </Notice>
          ) : null}
          {pairing?.active && pairing.code ? (
            <>
              <div className="rb-row-help">
                Your browser opens ReelBolt. Check that it shows this code, then press Approve.
              </div>
              <Group gap="sm" align="center">
                <span className="rb-code" aria-label={"Code " + pairing.code}>{pairing.code}</span>
                <Button
                  variant="default"
                  leftSection={<IconExternalLink size={16} stroke={1.6} />}
                  onClick={() => pairing.url && window.open(pairing.url, "_blank")}
                >
                  Open browser again
                </Button>
                <Button variant="subtle" color="gray" onClick={() => void bridge.cancelPairing().then(setPairing)}>
                  Cancel
                </Button>
              </Group>
              <div className="rb-row-help rb-faint">
                Only approve a code shown on a computer you own. If someone sent you a code, do not approve it.
              </div>
            </>
          ) : (
            <>
              <div className="rb-row-help">
                Sign in to ReelBolt in your browser and approve this computer. Nothing on your disk is
                shared with ReelBolt.
              </div>
              <Group gap="sm">
                <Button loading={busy === "pair" || (pairing?.active ?? false)} onClick={() => void act("pair")}>
                  Connect
                </Button>
              </Group>
              {pairing?.error ? <Notice tone="bad">{pairing.error}</Notice> : null}
              {pairing && !pairing.error && pairing.message ? (
                <div className="rb-row-help">{pairing.message}</div>
              ) : null}
            </>
          )}
        </div>
      )}
    </Step>
  );
}

function RenderStep({ view, act, busy }: { view: View; act: Act; busy: string | null }) {
  const rt = view.runtime;
  const paired = view.device.paired && !view.device.revoked;
  const done = rt.known === true && canRender(rt);
  const dl = rt.download;
  const runtimeName = containerName(rt.containerCli);

  let body: React.ReactNode;
  if (rt.probing && !dl?.active) {
    body = (
      <div style={{ display: "grid", gap: 10, maxWidth: 420 }}>
        <div className="rb-row-help">Checking what this computer can do. This can take a minute the first time.</div>
        <Meter label="Checking this computer" />
      </div>
    );
  } else if (done) {
    body = <div className="rb-row-help">This computer can render videos and process footage.</div>;
  } else if (!rt.containerCli) {
    body = (
      <div style={{ display: "grid", gap: 12 }}>
        <div className="rb-row-help">
          Rendering videos needs Docker Desktop or Podman Desktop, both free for personal use. Install
          one, open it once, then check again. Until then this computer can still process footage.
        </div>
        <Group gap="sm">
          <Button
            variant="default"
            rightSection={<IconChevronRight size={16} stroke={1.6} />}
            loading={busy === "setup-runtime"}
            onClick={() => void act("setup-runtime")}
          >
            How to install
          </Button>
          <Button
            variant="subtle"
            color="gray"
            leftSection={<IconRefresh size={16} stroke={1.6} />}
            loading={busy === "recheck-runtime"}
            onClick={() => void act("recheck-runtime")}
          >
            Check again
          </Button>
        </Group>
        {rt.failure ? <Notice tone="warn">{rt.failure}</Notice> : null}
      </div>
    );
  } else if (dl?.active) {
    const pct = dl.percent;
    body = (
      <div style={{ display: "grid", gap: 10, maxWidth: 460 }}>
        <Group justify="space-between">
          <span className="rb-row-help" style={{ margin: 0 }}>Downloading the video toolkit</span>
          <span className="rb-num rb-muted" style={{ fontSize: 13 }}>
            {pct != null ? pct + "%" : dl.layersTotal > 0 ? dl.layersDone + " of " + dl.layersTotal : ""}
          </span>
        </Group>
        <Meter value={pct} label="Downloading the video toolkit" />
        <div className="rb-row-help rb-faint">You can keep using your computer. This happens once.</div>
      </div>
    );
  } else if (rt.imageDownloadable === false) {
    body = (
      <div style={{ display: "grid", gap: 12 }}>
        <div className="rb-row-help">
          {runtimeName} is ready, but this copy of ReelBolt has no published video toolkit to download.
          Install the latest version of the app, then check again. Until then this computer can still
          process footage.
        </div>
        <Group gap="sm">
          <Button
            variant="default"
            leftSection={<IconRefresh size={16} stroke={1.6} />}
            loading={busy === "recheck-runtime"}
            onClick={() => void act("recheck-runtime")}
          >
            Check again
          </Button>
        </Group>
      </div>
    );
  } else {
    body = (
      <div style={{ display: "grid", gap: 12 }}>
        <div className="rb-row-help">
          {runtimeName} is ready. ReelBolt needs to download its video toolkit once
          {rt.imageBytes ? " (" + formatBytes(rt.imageBytes) + ")" : " (a few GB)"}.
        </div>
        {dl?.error ? <Notice tone="bad">{dl.error}</Notice> : null}
        {rt.failure && !dl?.error ? <Notice tone="warn">{rt.failure}</Notice> : null}
        <Group gap="sm">
          <Button
            leftSection={<IconDownload size={16} stroke={1.6} />}
            loading={busy === "download-runtime"}
            onClick={() => void act("download-runtime")}
          >
            {dl?.error ? "Try again" : "Download"}
          </Button>
          <Button
            variant="subtle"
            color="gray"
            leftSection={<IconRefresh size={16} stroke={1.6} />}
            loading={busy === "recheck-runtime"}
            onClick={() => void act("recheck-runtime")}
          >
            Check again
          </Button>
        </Group>
      </div>
    );
  }

  return (
    <Step index={2} title="Set up video rendering" done={done} current={paired && !done}>
      {body}
    </Step>
  );
}

function CrashReportStep({ act, busy, index }: { act: Act; busy: string | null; index: number }) {
  return (
    <Step index={index} title="Help us fix crashes" done={false} current>
      <div style={{ display: "grid", gap: 12 }}>
        <div className="rb-row-help">
          If the app crashes or a job cannot start, it can send ReelBolt a short report. Reports never
          include your files, names or projects. You can see exactly what is sent, and change this later
          in Settings.
        </div>
        <Group gap="sm">
          <Button loading={busy === "telemetry-on"} onClick={() => void act("telemetry-on")}>
            Send reports
          </Button>
          <Button variant="default" loading={busy === "telemetry-off"} onClick={() => void act("telemetry-off")}>
            No thanks
          </Button>
        </Group>
      </div>
    </Step>
  );
}
