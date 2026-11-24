// ReelBolt block "counter-number" (MIT). A big number that counts up (or down) to its target, with a label.
import React from 'react';
import {AbsoluteFill, interpolate, useCurrentFrame} from 'remotion';
import {easeOutCubic} from '../_shared/motion.ts';

export type CounterNumberProps = {
  /** Value shown at startFrame. Default 0. */
  from?: number;
  /** Value reached after durationInFrames. Default 12840. */
  to?: number;
  /** Text before / after the number, e.g. '$' or '%'. */
  prefix?: string;
  suffix?: string;
  /** Caption under the number. */
  label?: string;
  /** Decimal places. Default 0. */
  decimals?: number;
  color?: string;
  /** Label colour. Default '#94a3b8'. */
  labelColor?: string;
  fontSize?: number;
  /** CSS font-family; must be an installed family. Default 'Space Grotesk, sans-serif'. */
  fontFamily?: string;
  startFrame?: number;
  /** Count length in frames. Default 45. */
  durationInFrames?: number;
};

export const CounterNumber: React.FC<CounterNumberProps> = ({
  from = 0,
  to = 12840,
  prefix = '',
  suffix = '',
  label = 'Videos rendered',
  decimals = 0,
  color = '#ffffff',
  labelColor = '#94a3b8',
  fontSize = 180,
  fontFamily = 'Space Grotesk, sans-serif',
  startFrame = 0,
  durationInFrames = 45,
}) => {
  const frame = useCurrentFrame();
  const p = easeOutCubic(interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
  const value = from + (to - from) * p;
  const shown = value.toLocaleString('en-US', {minimumFractionDigits: decimals, maximumFractionDigits: decimals});
  return (
    <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center', fontFamily, padding: '0 8%', textAlign: 'center'}}>
      <div style={{fontSize, fontWeight: 700, color, fontVariantNumeric: 'tabular-nums', lineHeight: 1.1, whiteSpace: 'nowrap'}}>
        {prefix}
        {shown}
        {suffix}
      </div>
      {label ? <div style={{fontSize: fontSize * 0.2, fontWeight: 500, color: labelColor, marginTop: 12, letterSpacing: 2, textTransform: 'uppercase'}}>{label}</div> : null}
    </AbsoluteFill>
  );
};
