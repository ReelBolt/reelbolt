'use client';

export interface BarDatum { label: string; value: number | null }

/**
 * A hand-built horizontal bar chart in plain SVG. A null value is "not available" (the platform gave no
 * number) and draws no bar, never a zero-length one that would read as 0.
 */
export function BarChart({
  data, ariaLabel, unavailable, format,
}: { data: readonly BarDatum[]; ariaLabel: string; unavailable: string; format: (n: number) => string }) {
  const rowHeight = 28;
  const labelWidth = 120;
  const width = 480;
  const barArea = width - labelWidth - 70;
  const max = Math.max(1, ...data.map((d) => d.value ?? 0));
  return (
    <svg
      role="img" aria-label={ariaLabel} viewBox={`0 0 ${width} ${Math.max(1, data.length) * rowHeight}`}
      style={{ width: '100%', maxWidth: 560, height: 'auto' }} data-testid="bar-chart"
    >
      {data.map((d, i) => {
        const y = i * rowHeight;
        const w = d.value === null ? 0 : Math.max(2, (d.value / max) * barArea);
        return (
          <g key={`${d.label}-${i}`}>
            <text x={0} y={y + 18} fontSize={12} fill="currentColor">{d.label.length > 18 ? `${d.label.slice(0, 17)}…` : d.label}</text>
            {d.value === null ? (
              <text x={labelWidth} y={y + 18} fontSize={12} fill="currentColor" opacity={0.6}>{unavailable}</text>
            ) : (
              <>
                <rect x={labelWidth} y={y + 6} width={w} height={16} rx={3} fill="var(--mantine-color-blue-6)" />
                <text x={labelWidth + w + 6} y={y + 18} fontSize={12} fill="currentColor">{format(d.value)}</text>
              </>
            )}
          </g>
        );
      })}
    </svg>
  );
}
