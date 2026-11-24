import { ActionIcon, Tooltip } from "@mantine/core";
import { IconCpu, IconExternalLink, IconMovie, IconScan, IconStack2 } from "@tabler/icons-react";
import type { JobView } from "../shell/types";
import { formatDuration, formatWhen, jobContext, outcomeLabel, outcomeTone, stageLabel } from "../shell/derive";
import { Chip, Meter } from "./kit";

// jobIcon picks a glyph for the kind of work, so a list of jobs can be scanned
// by shape before it is read.
function jobIcon(type: string) {
  if (type.startsWith("remotion.")) return IconMovie;
  if (type === "video.compile") return IconStack2;
  if (type.startsWith("video.analyze")) return IconScan;
  return IconCpu;
}

export function JobRow({ job, onOpen }: { job: JobView; onOpen?: (job: JobView) => void }) {
  const Icon = jobIcon(job.type);
  const context = jobContext(job);
  const when = formatWhen(job.startedAt);
  const timing = job.running
    ? (job.stage ? stageLabel(job.stage) + ", " : "") + formatDuration(job.durationSeconds) + " so far"
    : when + ", took " + formatDuration(job.durationSeconds);
  const meta = context ? context + "  ·  " + timing : timing;
  return (
    <div className="rb-job">
      <div className="rb-job-icon" aria-hidden>
        <Icon size={17} stroke={1.6} />
      </div>
      <div style={{ minWidth: 0 }}>
        <div className="rb-job-title">{job.title}</div>
        <div className="rb-job-meta rb-num" title={job.outcomeDetail || undefined}>
          {meta}
        </div>
        {job.running ? (
          <div style={{ marginTop: 8, maxWidth: 360 }}>
            <Meter value={job.percent} label={"Progress of " + job.title} />
          </div>
        ) : job.outcomeDetail && (job.outcome === "failed" || job.outcome === "timed-out") ? (
          <div className="rb-job-meta" style={{ color: "var(--rb-bad)", whiteSpace: "normal" }}>
            {job.outcomeDetail}
          </div>
        ) : null}
      </div>
      <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
        <Chip tone={outcomeTone(job)}>
          <span className="rb-num">{outcomeLabel(job)}</span>
        </Chip>
        {onOpen && job.dashboardUrl ? (
          <Tooltip label="Open in ReelBolt">
            <ActionIcon variant="subtle" color="gray" aria-label="Open in ReelBolt" onClick={() => onOpen(job)}>
              <IconExternalLink size={16} stroke={1.6} />
            </ActionIcon>
          </Tooltip>
        ) : null}
      </div>
    </div>
  );
}
