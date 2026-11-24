// ReelBolt block "lower-third-glass" (MIT). Name and role on a frosted translucent card that floats up.
import React from 'react';
import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';

export type LowerThirdGlassProps = {
  name: string;
  subtitle?: string;
  /** Small dot / accent colour. Default '#38bdf8'. */
  accentColor?: string;
  textColor?: string;
  /** CSS font-family; must be an installed family. Default 'DM Sans, sans-serif'. */
  fontFamily?: string;
  startFrame?: number;
  /** Frame where it leaves; omit to stay. */
  endFrame?: number;
};

export const LowerThirdGlass: React.FC<LowerThirdGlassProps> = ({
  name,
  subtitle = '',
  accentColor = '#38bdf8',
  textColor = '#ffffff',
  fontFamily = 'DM Sans, sans-serif',
  startFrame = 0,
  endFrame,
}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const enter = spring({frame: frame - startFrame, fps, config: {damping: 18, stiffness: 120}});
  const exit = endFrame === undefined ? 0 : spring({frame: frame - endFrame, fps, config: {damping: 200}, durationInFrames: 14});
  const opacity = interpolate(enter, [0, 0.5], [0, 1], {extrapolateRight: 'clamp'}) * (1 - exit);
  return (
    <AbsoluteFill style={{justifyContent: 'flex-end', alignItems: 'flex-start', padding: '0 5% 7% 5%', fontFamily}}>
      <div
        style={{
          opacity,
          transform: `translateY(${(1 - enter) * 40 + exit * 20}px)`,
          maxWidth: '70%',
          display: 'flex',
          alignItems: 'center',
          gap: 22,
          padding: '22px 40px',
          borderRadius: 24,
          color: textColor,
          background: 'linear-gradient(135deg, rgba(255,255,255,0.28), rgba(255,255,255,0.10))',
          border: '1.5px solid rgba(255,255,255,0.35)',
          boxShadow: '0 12px 40px rgba(0,0,0,0.35)',
          backdropFilter: 'blur(18px)',
        }}
      >
        <div style={{width: 16, height: 16, borderRadius: 8, background: accentColor, boxShadow: `0 0 18px ${accentColor}`}} />
        <div>
          <div style={{fontSize: 42, fontWeight: 700}}>{name}</div>
          {subtitle ? <div style={{fontSize: 26, fontWeight: 400, opacity: 0.85, marginTop: 2}}>{subtitle}</div> : null}
        </div>
      </div>
    </AbsoluteFill>
  );
};
