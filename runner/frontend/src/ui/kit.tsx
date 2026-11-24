import type { ReactNode } from "react";
import {
  IconAlertTriangle, IconCircleCheck, IconInfoCircle, IconX,
} from "@tabler/icons-react";
import { CloseButton } from "@mantine/core";
import type { Tone } from "../shell/derive";

// The window's handful of building blocks. They exist so every screen has the
// same rhythm (title, lead, grouped rows) without each one re-deciding spacing.

export const ICON = { size: 18, stroke: 1.6 } as const;

export function Page({ title, lead, actions, children }: {
  title: string;
  lead?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className="rb-page">
      <header style={{ display: "flex", alignItems: "flex-start", gap: 16 }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <h1 className="rb-page-title">{title}</h1>
          {lead ? <p className="rb-page-lead">{lead}</p> : null}
        </div>
        {actions ? <div style={{ flexShrink: 0, paddingTop: 2 }}>{actions}</div> : null}
      </header>
      {children}
    </div>
  );
}

export function Section({ id, title, lead, children, aside }: {
  id?: string;
  title: string;
  lead?: ReactNode;
  aside?: ReactNode;
  children: ReactNode;
}) {
  return (
    <section className="rb-section rb-enter" id={id} aria-labelledby={id ? id + "-title" : undefined}>
      <div style={{ display: "flex", alignItems: "baseline", gap: 12 }}>
        <h2 className="rb-section-title" id={id ? id + "-title" : undefined} style={{ flex: 1 }}>
          {title}
        </h2>
        {aside}
      </div>
      {lead ? <p className="rb-section-lead">{lead}</p> : <div style={{ height: 12 }} />}
      {children}
    </section>
  );
}

export function Surface({ children, className, style }: {
  children: ReactNode;
  className?: string;
  style?: React.CSSProperties;
}) {
  return (
    <div className={"rb-surface" + (className ? " " + className : "")} style={style}>
      {children}
    </div>
  );
}

// Row is one setting: what it is on the left, the control on the right.
export function Row({ label, help, control, stack, children }: {
  label: ReactNode;
  help?: ReactNode;
  control?: ReactNode;
  stack?: boolean;
  children?: ReactNode;
}) {
  return (
    <div className={"rb-row" + (stack ? " rb-row-stack" : "")}>
      <div style={{ minWidth: 0 }}>
        <div className="rb-row-label">{label}</div>
        {help ? <div className="rb-row-help">{help}</div> : null}
      </div>
      {control ? <div>{control}</div> : null}
      {children}
    </div>
  );
}

export function StatePill({ tone, children }: { tone: Tone; children: ReactNode }) {
  return (
    <span className="rb-state" data-tone={tone}>
      {children}
    </span>
  );
}

export function Chip({ tone, children, title }: { tone?: Tone; children: ReactNode; title?: string }) {
  return (
    <span className="rb-chip" data-tone={tone} title={title}>
      {children}
    </span>
  );
}

export type NoticeTone = "info" | "ok" | "warn" | "bad" | "plain";

export function Notice({ tone = "plain", title, children, onClose, action }: {
  tone?: NoticeTone;
  title?: ReactNode;
  children?: ReactNode;
  onClose?: () => void;
  action?: ReactNode;
}) {
  const Icon = tone === "ok" ? IconCircleCheck : tone === "warn" || tone === "bad" ? IconAlertTriangle : IconInfoCircle;
  return (
    <div className="rb-notice" data-tone={tone} role={tone === "bad" ? "alert" : "status"}>
      <Icon className="rb-notice-icon" size={18} stroke={1.6} aria-hidden />
      <div style={{ minWidth: 0 }}>
        {title ? <div className="rb-notice-title">{title}</div> : null}
        {children ? <div className={title ? "rb-muted" : undefined} style={{ marginTop: title ? 2 : 0 }}>{children}</div> : null}
        {action ? <div style={{ marginTop: 10 }}>{action}</div> : null}
      </div>
      {onClose ? (
        <CloseButton size="sm" onClick={onClose} aria-label="Dismiss" icon={<IconX size={14} />} />
      ) : (
        <span />
      )}
    </div>
  );
}

// Meter is a thin progress line. Without a value it is indeterminate: it shows
// that something is happening without inventing how far along it is.
export function Meter({ value, label }: { value?: number | null; label: string }) {
  const known = value != null && isFinite(value);
  return (
    <div
      className="rb-meter"
      data-indeterminate={!known}
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={known ? Math.round(value!) : undefined}
    >
      <span style={known ? { width: Math.max(2, Math.min(100, value!)) + "%" } : undefined} />
    </div>
  );
}

export function Facts({ items }: { items: { label: string; value: ReactNode; title?: string }[] }) {
  return (
    <div className="rb-facts">
      {items.map((f) => (
        <div className="rb-fact" key={f.label}>
          <div className="rb-fact-label">{f.label}</div>
          <div className="rb-fact-value" title={f.title}>{f.value}</div>
        </div>
      ))}
    </div>
  );
}
