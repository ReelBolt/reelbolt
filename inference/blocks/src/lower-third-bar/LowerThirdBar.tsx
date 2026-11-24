// ReelBolt block "lower-third-bar" (MIT). Name and role on a solid bar with an accent stripe.
import React from 'react';
import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';

export type LowerThirdBarProps = {
  name: string;
  subtitle?: string;
  /** Accent stripe colour. Default '#f43f5e'. */
  accentColor?: string;
  /** Bar background. Default '#0f172a'. */
  barColor?: string;
  textColor?: string;
  /** CSS font-family; must be an installed family. Default 'Montserrat, sans-serif'. */
  fontFamily?: string;
  /** Frame where it enters. Default 0. */
  startFrame?: number;
  /** Frame where it leaves; omit to stay. */
  endFrame?: number;
};

export const LowerThirdBar: React.FC<LowerThirdBarProps> = ({
  name,
  subtitle = '',
  accentColor = '#f43f5e',
  barColor = '#0f172a',
  textColor = '#ffffff',
  fontFamily = 'Montserrat, sans-serif',
  startFrame = 0,
  endFrame,
}) => {
  const frame = useCurrentFrame();
  const {fps, height} = useVideoConfig();
  const enter = spring({frame: frame - startFrame, fps, config: {damping: 200}, durationInFrames: 20});
  const exit = endFrame === undefined ? 0 : spring({frame: frame - endFrame, fps, config: {damping: 200}, durationInFrames: 14});
  const p = enter - exit;
  const text = interpolate(p, [0.4, 1], [0, 1], {extrapolateLeft: 'clamp'});
  return (
    <AbsoluteFill style={{justifyContent: 'flex-end', alignItems: 'flex-start', padding: `0 5% ${height * 0.07}px 5%`, fontFamily}}>
      <div style={{display: 'flex', clipPath: `inset(0 ${(1 - p) * 100}% 0 0)`, maxWidth: '70%'}}>
        <div style={{width: 14, background: accentColor}} />
        <div style={{background: barColor, color: textColor, padding: '18px 40px 18px 28px'}}>
          <div style={{fontSize: 44, fontWeight: 800, opacity: text, transform: `translateX(${(1 - text) * -24}px)`}}>{name}</div>
          {subtitle ? (
            <div style={{fontSize: 26, fontWeight: 500, marginTop: 4, opacity: text * 0.8, transform: `translateX(${(1 - text) * -24}px)`}}>{subtitle}</div>
          ) : null}
        </div>
      </div>
    </AbsoluteFill>
  );
};
