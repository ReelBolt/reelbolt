// ReelBolt block "confetti" (MIT). Seeded confetti burst from a point; pieces fall with gravity and fade out.
import React from 'react';
import {AbsoluteFill, random, useCurrentFrame, useVideoConfig} from 'remotion';

export type ConfettiProps = {
  children?: React.ReactNode;
  /** Frame (in the surrounding Sequence) of the burst. Default 0. */
  startFrame?: number;
  /** Number of pieces (keep under 200). Default 90. */
  count?: number;
  /** Burst origin as fractions of the frame [x, y]. Default [0.5, 0.6]. */
  origin?: [number, number];
  /** Piece colours. */
  colors?: string[];
  /** Burst power in px per second. Default 1400. */
  power?: number;
  /** Frames the effect lasts. Default 90. */
  durationInFrames?: number;
  /** Seed. Default 'confetti'. */
  seed?: string;
};

export const Confetti: React.FC<ConfettiProps> = ({
  children,
  startFrame = 0,
  count = 90,
  origin = [0.5, 0.6],
  colors = ['#ff595e', '#ffca3a', '#8ac926', '#1982c4', '#6a4c93', '#ffffff'],
  power = 1400,
  durationInFrames = 90,
  seed = 'confetti',
}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const t = (frame - startFrame) / 30;
  const life = durationInFrames / 30;
  const n = Math.min(200, Math.max(0, count));
  return (
    <AbsoluteFill>
      {children}
      <AbsoluteFill style={{pointerEvents: 'none', overflow: 'hidden'}}>
        {t >= 0 && t <= life &&
          Array.from({length: n}, (_, i) => {
            const ang = -Math.PI / 2 + (random(`${seed}-a-${i}`) - 0.5) * Math.PI * 1.1;
            const sp = power * (0.35 + random(`${seed}-p-${i}`) * 0.65);
            const drag = Math.exp(-t * 2.2);
            const dist = (1 - drag) / 2.2;
            const x = origin[0] * width + Math.cos(ang) * sp * dist + Math.sin(t * 6 + i) * 14 * t;
            const y = origin[1] * height + Math.sin(ang) * sp * dist + 900 * t * t * 0.5 * (0.7 + random(`${seed}-g-${i}`) * 0.5);
            const w = 10 + random(`${seed}-w-${i}`) * 12;
            const rot = random(`${seed}-r-${i}`) * 360 + t * (200 + random(`${seed}-rs-${i}`) * 500);
            const fade = Math.min(1, Math.max(0, (life - t) / (life * 0.3)));
            return (
              <div
                key={i}
                style={{
                  position: 'absolute',
                  left: x,
                  top: y,
                  width: w,
                  height: w * 0.55,
                  background: colors[i % colors.length],
                  opacity: fade,
                  transform: `rotate(${rot}deg) rotateX(${t * 400 + i * 30}deg)`,
                  borderRadius: 2,
                }}
              />
            );
          })}
      </AbsoluteFill>
    </AbsoluteFill>
  );
};
