'use client';

import { useState } from 'react';
import { Group, Text } from '@mantine/core';
import type { ReliabilityBucket } from '@/lib/types/decision-calibration';

/**
 * Reliability diagram: "when the model says it is X% sure, how often does it agree with the agent?"
 * Hand-rolled SVG (web/ has no chart library). Dots sit on the dashed diagonal when the model's
 * confidence is honest; below it the model is overconfident. Dot area = observations in the bucket,
 * the vertical bar = 95% Wilson range (long bars mean "too few rows to say").
 */
const W = 560;
const H = 340;
const L = 56;
const R = 16;
const T = 16;
const B = 52;
const TICKS = [0, 0.2, 0.4, 0.6, 0.8, 1];
const clamp = (v: number) => (Number.isFinite(v) ? Math.min(1, Math.max(0, v)) : 0);
const pct = (v: number) => `${Math.round(v * 100)}%`;

export function ReliabilityChart({ buckets }: { buckets: ReliabilityBucket[] }) {
  const [hover, setHover] = useState<number | null>(null);
  const x = (v: number) => L + clamp(v) * (W - L - R);
  const y = (v: number) => H - B - clamp(v) * (H - T - B);
  const points = buckets.filter((b) => b.count > 0);

  if (points.length === 0) {
    return (
      <Text size="sm" c="dimmed" role="note">
        No bucketed observations for this question: its answers are a single yes/no probability rather than
        a full distribution, so there is nothing to place on a reliability diagram. The agreement figures
        above still apply.
      </Text>
    );
  }
  const active = hover === null ? null : points[hover];
  return (
    <div style={{ position: 'relative' }}>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        role="img"
        aria-label="Reliability diagram: observed agreement against predicted confidence"
        style={{ width: '100%', maxWidth: W, height: 'auto', display: 'block' }}
      >
        {/* overconfident zone: below the diagonal */}
        <polygon
          points={`${x(0)},${y(0)} ${x(1)},${y(0)} ${x(1)},${y(1)}`}
          fill="var(--mantine-color-red-6)"
          fillOpacity="0.06"
        />
        {TICKS.map((t) => (
          <g key={t}>
            <line x1={x(t)} y1={y(0)} x2={x(t)} y2={y(1)} stroke="var(--mantine-color-default-border)" strokeWidth="0.75" />
            <line x1={x(0)} y1={y(t)} x2={x(1)} y2={y(t)} stroke="var(--mantine-color-default-border)" strokeWidth="0.75" />
            <text x={x(t)} y={y(0) + 18} textAnchor="middle" fontSize="12" fill="var(--mantine-color-dimmed)">
              {pct(t)}
            </text>
            <text x={x(0) - 8} y={y(t)} textAnchor="end" dominantBaseline="middle" fontSize="12" fill="var(--mantine-color-dimmed)">
              {pct(t)}
            </text>
          </g>
        ))}
        <line x1={x(0)} y1={y(0)} x2={x(1)} y2={y(1)} stroke="var(--mantine-color-dimmed)" strokeWidth="1.5" strokeDasharray="6 5" />
        <text x={x(0.98)} y={y(0.12)} textAnchor="end" fontSize="12" fill="var(--mantine-color-red-7)">
          overconfident zone
        </text>
        {points.map((p, i) => {
          const cx = x(p.predictedMean);
          const r = Math.min(14, 5 + 2.2 * Math.sqrt(p.count));
          const on = hover === i;
          return (
            <g
              key={p.lowerBound}
              className="reliability-bucket"
              onMouseEnter={() => setHover(i)}
              onMouseLeave={() => setHover(null)}
              tabIndex={0}
              onFocus={() => setHover(i)}
              onBlur={() => setHover(null)}
              style={{ cursor: 'var(--rb-cursor-pointer)', outline: 'none' }}
            >
              <line
                x1={cx}
                x2={cx}
                y1={y(p.confidenceIntervalHigh)}
                y2={y(p.confidenceIntervalLow)}
                stroke="var(--mantine-color-blue-5)"
                strokeWidth={on ? 3 : 2}
                strokeOpacity="0.6"
              />
              <circle
                cx={cx}
                cy={y(p.observedAgreementRate)}
                r={r}
                fill="var(--mantine-color-blue-6)"
                fillOpacity="0.85"
                stroke="var(--mantine-color-body)"
                strokeWidth={on ? 3 : 1.5}
              />
            </g>
          );
        })}
        <text x={(L + W - R) / 2} y={H - 8} textAnchor="middle" fontSize="13" fontWeight="600" fill="var(--mantine-color-text)">
          What the model said (predicted confidence)
        </text>
        <text
          transform={`rotate(-90 14 ${(T + H - B) / 2})`}
          x={14}
          y={(T + H - B) / 2}
          textAnchor="middle"
          fontSize="13"
          fontWeight="600"
          fill="var(--mantine-color-text)"
        >
          How often it agreed with the agent
        </text>
      </svg>
      {active ? (
        <div
          role="tooltip"
          style={{
            position: 'absolute',
            top: 8,
            right: 8,
            maxWidth: 240,
            padding: '8px 10px',
            borderRadius: 8,
            fontSize: 12,
            background: 'var(--mantine-color-body)',
            border: '1px solid var(--mantine-color-default-border)',
            boxShadow: 'var(--mantine-shadow-md)',
          }}
        >
          <strong>
            Confidence {pct(active.lowerBound)}–{pct(active.lowerBound + 0.1)}
          </strong>
          <div>
            {active.count} observation{active.count === 1 ? '' : 's'}; model said about {pct(active.predictedMean)}, agreed{' '}
            {pct(active.observedAgreementRate)} of the time.
          </div>
          <div style={{ color: 'var(--mantine-color-dimmed)' }}>
            Plausible range {pct(active.confidenceIntervalLow)}–{pct(active.confidenceIntervalHigh)}
          </div>
        </div>
      ) : null}
      <Group gap="lg" mt={6} wrap="wrap">
        <Group gap={6}>
          <svg width="26" height="8" aria-hidden>
            <line x1="0" y1="4" x2="26" y2="4" stroke="var(--mantine-color-dimmed)" strokeWidth="1.5" strokeDasharray="5 4" />
          </svg>
          <Text size="xs" c="dimmed">Perfectly honest model</Text>
        </Group>
        <Group gap={6}>
          <svg width="12" height="12" aria-hidden>
            <circle cx="6" cy="6" r="5" fill="var(--mantine-color-blue-6)" fillOpacity="0.85" />
          </svg>
          <Text size="xs" c="dimmed">A confidence bucket (bigger = more observations)</Text>
        </Group>
        <Group gap={6}>
          <svg width="8" height="14" aria-hidden>
            <line x1="4" y1="0" x2="4" y2="14" stroke="var(--mantine-color-blue-5)" strokeWidth="2" strokeOpacity="0.6" />
          </svg>
          <Text size="xs" c="dimmed">95% plausible range (long = few rows)</Text>
        </Group>
      </Group>
    </div>
  );
}

export default ReliabilityChart;
