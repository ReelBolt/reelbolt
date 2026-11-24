'use client';

import { useState } from 'react';
import { Group, Text } from '@mantine/core';
import type { ThresholdSweepPoint } from '@/lib/types/decision-calibration';

/**
 * Threshold sweep: what raising or lowering Accept-at does. Blue = share of decisions the model would
 * handle alone; green = how often it still agrees with the agent on those. The dashed line is the 95%
 * agreement bar the recommendation uses; the grey vertical marks the threshold currently selected.
 */
const W = 560;
const H = 320;
const L = 56;
const R = 20;
const T = 16;
const B = 52;
const TARGET = 0.95;
const pct = (v: number) => `${Math.round(v * 100)}%`;
const BLUE = 'var(--mantine-color-blue-6)';
const GREEN = 'var(--mantine-color-teal-7)';

export function SweepChart({ sweep, acceptAt }: { sweep: ThresholdSweepPoint[]; acceptAt: number }) {
  const [hover, setHover] = useState<number | null>(null);
  if (sweep.length === 0) return <Text size="sm" c="dimmed">No sweep data.</Text>;
  const lo = sweep[0].acceptAt;
  const hi = sweep[sweep.length - 1].acceptAt;
  const x = (v: number) => L + ((v - lo) / (hi - lo || 1)) * (W - L - R);
  const y = (v: number) => H - B - Math.min(1, Math.max(0, v)) * (H - T - B);
  const rate = sweep.map((p) => `${x(p.acceptAt)},${y(p.acceptRate)}`).join(' ');
  const withOutcome = sweep.filter((p) => p.acceptedWithOutcomeCount > 0);
  const agree = withOutcome.map((p) => `${x(p.acceptAt)},${y(p.acceptedAgreementRate)}`).join(' ');
  const active = hover === null ? null : sweep[hover];
  const step = (W - L - R) / (sweep.length - 1);

  return (
    <div style={{ position: 'relative' }}>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        role="img"
        aria-label="Threshold sweep: share of decisions accepted and agreement among accepted, against Accept-at"
        style={{ width: '100%', maxWidth: W, height: 'auto', display: 'block' }}
        onMouseLeave={() => setHover(null)}
      >
        {[0, 0.25, 0.5, 0.75, 1].map((t) => (
          <g key={t}>
            <line x1={L} x2={W - R} y1={y(t)} y2={y(t)} stroke="var(--mantine-color-default-border)" strokeWidth="0.75" />
            <text x={L - 8} y={y(t)} textAnchor="end" dominantBaseline="middle" fontSize="12" fill="var(--mantine-color-dimmed)">
              {pct(t)}
            </text>
          </g>
        ))}
        {sweep.filter((_, i) => i % 2 === 0).map((p) => (
          <text key={p.acceptAt} x={x(p.acceptAt)} y={y(0) + 18} textAnchor="middle" fontSize="12" fill="var(--mantine-color-dimmed)">
            {p.acceptAt.toFixed(2)}
          </text>
        ))}
        <line x1={L} x2={W - R} y1={y(TARGET)} y2={y(TARGET)} stroke={GREEN} strokeDasharray="5 4" strokeWidth="1" strokeOpacity="0.7" />
        <text x={W - R - 4} y={y(TARGET) - 5} textAnchor="end" fontSize="11" fill={GREEN}>95% agreement bar</text>
        <line x1={x(acceptAt)} x2={x(acceptAt)} y1={y(0)} y2={y(1)} stroke="var(--mantine-color-dimmed)" strokeWidth="1.5" />
        <text x={x(acceptAt)} y={T - 3} textAnchor="middle" fontSize="11" fill="var(--mantine-color-dimmed)">selected</text>
        <polyline points={rate} fill="none" stroke={BLUE} strokeWidth="2.5" />
        {agree ? <polyline points={agree} fill="none" stroke={GREEN} strokeWidth="2.5" /> : null}
        {sweep.map((p) => (
          <circle key={`r${p.acceptAt}`} cx={x(p.acceptAt)} cy={y(p.acceptRate)} r="3.5" fill={BLUE} />
        ))}
        {withOutcome.map((p) => (
          <circle key={`a${p.acceptAt}`} cx={x(p.acceptAt)} cy={y(p.acceptedAgreementRate)} r="3.5" fill={GREEN} />
        ))}
        {sweep.map((p, i) => (
          <rect
            key={`h${p.acceptAt}`}
            x={x(p.acceptAt) - step / 2}
            y={T}
            width={step}
            height={H - T - B}
            fill={hover === i ? 'var(--mantine-color-gray-6)' : 'transparent'}
            fillOpacity="0.1"
            onMouseEnter={() => setHover(i)}
          />
        ))}
        <text x={(L + W - R) / 2} y={H - 8} textAnchor="middle" fontSize="13" fontWeight="600" fill="var(--mantine-color-text)">
          Accept-at confidence (how sure the model must be)
        </text>
      </svg>
      {active ? (
        <div
          role="tooltip"
          style={{
            position: 'absolute', top: 8, right: 8, maxWidth: 250, padding: '8px 10px', borderRadius: 8, fontSize: 12,
            background: 'var(--mantine-color-body)', border: '1px solid var(--mantine-color-default-border)',
            boxShadow: 'var(--mantine-shadow-md)',
          }}
        >
          <strong>Accept-at {active.acceptAt.toFixed(2)}</strong>
          <div>Model handles {pct(active.acceptRate)} of decisions ({active.acceptedCount}).</div>
          <div>
            {active.acceptedWithOutcomeCount > 0
              ? `Agrees on ${pct(active.acceptedAgreementRate)} of ${active.acceptedWithOutcomeCount} accepted rows with an outcome.`
              : 'No accepted rows with a recorded outcome.'}
          </div>
          <div style={{ color: 'var(--mantine-color-dimmed)' }}>Projected tokens saved: {active.tokensSaved.toLocaleString('en-US')}</div>
        </div>
      ) : null}
      <Group gap="lg" mt={6} wrap="wrap">
        <Group gap={6}><svg width="22" height="8" aria-hidden><line x1="0" y1="4" x2="22" y2="4" stroke={BLUE} strokeWidth="2.5" /></svg><Text size="xs" c="dimmed">Share of decisions the model would handle</Text></Group>
        <Group gap={6}><svg width="22" height="8" aria-hidden><line x1="0" y1="4" x2="22" y2="4" stroke={GREEN} strokeWidth="2.5" /></svg><Text size="xs" c="dimmed">Agreement with the agent on those</Text></Group>
      </Group>
    </div>
  );
}

export default SweepChart;
