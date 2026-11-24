// ReelBolt block "kpi-ticker" (MIT). A big number that counts up to its value, with a label and an optional change chip.
import React from 'react';
import {format} from 'd3';
import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';
import {easeOutCubic} from '../_shared/motion.ts';

export type KpiTickerProps = {
  /** The number to count to. */
  value: number;
  /** Label under the number. */
  label: string;
  /** Number the count starts from. Default 0. */
  from?: number;
  /** Text before the number, e.g. '$'. Default ''. */
  prefix?: string;
  /** Text after the number, e.g. 'k' or '%'. Default ''. */
  suffix?: string;
  /** d3-format string. Default ',.0f'. */
  valueFormat?: string;
  /** Change chip text, e.g. '+12.4%'. Default none. */
  delta?: string;
  /** Chip colour (use green for good, red for bad). Default '#34d399'. */
  deltaColor?: string;
  /** Number colour. Default '#ffffff'. */
  color?: string;
  /** Frame the count starts. Default 5. */
  startFrame?: number;
  /** Count length in frames. Default 40. */
  durationInFrames?: number;
  /** Number font size in px. Default: 26% of the frame height, shrunk so the whole value fits the frame width. */
  fontSize?: number;
  /** CSS font-family (installed font). Default 'Space Grotesk, sans-serif'. */
  fontFamily?: string;
};

export const KpiTicker: React.FC<KpiTickerProps> = ({
  value,
  label,
  from = 0,
  prefix = '',
  suffix = '',
  valueFormat = ',.0f',
  delta,
  deltaColor = '#34d399',
  color = '#ffffff',
  startFrame = 5,
  durationInFrames = 40,
  fontSize,
  fontFamily = 'Space Grotesk, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {fps, width, height} = useVideoConfig();
  const p = easeOutCubic(interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
  const chip = spring({frame: frame - startFrame - durationInFrames, fps, config: {damping: 14}});
  // Size by the widest the value gets (its final text), about 0.62em per character.
  const widest = `${prefix}${format(valueFormat)(value)}${suffix}`.length;
  const size = fontSize ?? Math.round(Math.min(height * 0.26, (width * 0.84) / (Math.max(widest, from === value ? 1 : `${prefix}${format(valueFormat)(from)}${suffix}`.length) * 0.62)));
  return (
    <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center', fontFamily, color, textAlign: 'center', padding: '0 8%'}}>
      <div style={{fontSize: size, fontWeight: 700, lineHeight: 1, fontVariantNumeric: 'tabular-nums'}}>
        {prefix}
        {format(valueFormat)(from + (value - from) * p)}
        {suffix}
      </div>
      <div style={{fontSize: size * 0.2, marginTop: size * 0.12, opacity: 0.75, letterSpacing: size * 0.01}}>{label}</div>
      {delta ? (
        <div
          style={{
            marginTop: size * 0.16,
            padding: `${size * 0.04}px ${size * 0.12}px`,
            borderRadius: 999,
            fontSize: size * 0.17,
            fontWeight: 700,
            color: deltaColor,
            border: `2px solid ${deltaColor}`,
            opacity: Math.min(1, chip),
            transform: `scale(${0.8 + 0.2 * chip})`,
          }}
        >
          {delta}
        </div>
      ) : null}
    </AbsoluteFill>
  );
};
