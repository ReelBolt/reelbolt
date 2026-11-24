import { Button, SegmentedControl } from "@mantine/core";
import { IconExternalLink, IconPlayerPlay } from "@tabler/icons-react";
import { useState } from "react";
import type { Act } from "../App";
import { jobRows } from "../shell/derive";
import type { JobView, View } from "../shell/types";
import { JobRow } from "../ui/JobRow";
import { Notice, Page, Section, Surface } from "../ui/kit";

interface Props {
  view: View;
  act: Act;
  busy: string | null;
}

type Filter = "all" | "problems";

// Activity is the work this computer has done since the app started: what is
// running now and the last jobs before it, each with how this computer's part
// of it ended. The full record of every run lives in the dashboard.
export function Activity({ view, act, busy }: Props) {
  const [filter, setFilter] = useState<Filter>("all");
  const rows = jobRows(view.jobs);
  const running = rows.filter((j) => j.running);
  const finished = rows.filter((j) => !j.running);
  const problems = finished.filter(isProblem);
  const shown = filter === "problems" ? problems : finished;
  const open = (job: JobView) => void act("open-job", job.id);

  return (
    <Page
      title="Activity"
      lead="Jobs this computer ran for your workspace since the app started."
      actions={
        <Button
          variant="default"
          rightSection={<IconExternalLink size={16} stroke={1.6} />}
          loading={busy === "open-dashboard"}
          onClick={() => void act("open-dashboard")}
        >
          Open ReelBolt
        </Button>
      }
    >
      {!view.runner.running && view.device.paired ? (
        <div style={{ marginTop: 20 }}>
          <Notice
            tone="warn"
            title="Not taking work"
            action={
              <Button
                size="xs"
                leftSection={<IconPlayerPlay size={14} stroke={1.6} />}
                loading={busy === "start-runner"}
                onClick={() => void act("start-runner")}
              >
                Start
              </Button>
            }
          >
            The app is open but not running jobs.
          </Notice>
        </div>
      ) : null}

      <Section title="Running now">
        {running.length === 0 ? (
          <Surface>
            <div style={{ padding: "18px 20px" }} className="rb-row-help">
              {view.runner.running && view.runner.allowed
                ? "Nothing right now. This computer is ready for the next job."
                : "Nothing right now."}
            </div>
          </Surface>
        ) : (
          <Surface>
            {running.map((j) => (
              <JobRow key={j.id} job={j} onOpen={open} />
            ))}
          </Surface>
        )}
      </Section>

      <Section
        title="Earlier"
        aside={
          finished.length > 0 ? (
            <SegmentedControl
              size="xs"
              value={filter}
              onChange={(v) => setFilter(v as Filter)}
              data={[
                { value: "all", label: "All" },
                { value: "problems", label: "Problems" + (problems.length ? " (" + problems.length + ")" : "") },
              ]}
            />
          ) : undefined
        }
      >
        {shown.length === 0 ? (
          <Surface>
            <div style={{ padding: "22px 20px" }}>
              <div className="rb-row-label">
                {filter === "problems" ? "No problems" : "No finished jobs yet"}
              </div>
              <div className="rb-row-help">
                {filter === "problems"
                  ? "Every job this computer ran ended normally."
                  : "Finished jobs appear here with how they ended."}
              </div>
            </div>
          </Surface>
        ) : (
          <Surface>
            {shown.map((j) => (
              <JobRow key={j.id} job={j} onOpen={open} />
            ))}
          </Surface>
        )}
        <p className="rb-section-lead" style={{ marginTop: 12 }}>
          "Done" means this computer finished its part. Whether the whole video came out the way you
          wanted is shown in ReelBolt.
        </p>
      </Section>
    </Page>
  );
}

function isProblem(j: JobView): boolean {
  return j.outcome === "failed" || j.outcome === "timed-out" || j.outcome === "interrupted";
}
