// ReelBolt block "vhs-glitch" (MIT). VHS glitch: RGB channel split plus horizontal tear bands, in seeded bursts.
import React from 'react';
import {AbsoluteFill, random, useCurrentFrame} from 'remotion';

export type VhsGlitchProps = {
  /** The content to glitch (rendered a few times; keep it light). */
  children: React.ReactNode;
  /** Burst probability and strength 0..1. Default 0.5. */
  intensity?: number;
  /** Max RGB offset in px. Default 14. */
  split?: number;
  /** Tear bands per glitching frame (0..4). Default 3. */
  bands?: number;
  /** Frames per glitch step. Default 3. */
  stepFrames?: number;
  /** Seed. Default 'vhs'. */
  seed?: string;
};

const CHANNELS: {id: string; matrix: string; sign: number}[] = [
  {id: 'r', matrix: '1 0 0 0 0  0 0 0 0 0  0 0 0 0 0  0 0 0 1 0', sign: -1},
  {id: 'g', matrix: '0 0 0 0 0  0 1 0 0 0  0 0 0 0 0  0 0 0 1 0', sign: 0},
  {id: 'b', matrix: '0 0 0 0 0  0 0 0 0 0  0 0 1 0 0  0 0 0 1 0', sign: 1},
];

export const VhsGlitch: React.FC<VhsGlitchProps> = ({children, intensity = 0.5, split = 14, bands = 3, stepFrames = 3, seed = 'vhs'}) => {
  const frame = useCurrentFrame();
  const step = Math.floor(frame / Math.max(1, stepFrames));
  const active = random(`${seed}-on-${step}`) < intensity;
  const amount = active ? (0.4 + random(`${seed}-amt-${step}`) * 0.6) * split * intensity * 2 : split * 0.15;
  const tears = active
    ? Array.from({length: Math.min(4, Math.max(0, bands))}, (_, i) => ({
        top: random(`${seed}-top-${step}-${i}`) * 90,
        height: 2 + random(`${seed}-h-${step}-${i}`) * 8,
        shift: (random(`${seed}-s-${step}-${i}`) - 0.5) * split * 8 * intensity,
      }))
    : [];
  return (
    <AbsoluteFill style={{background: '#000'}}>
      <svg width="0" height="0" style={{position: 'absolute'}}>
        {CHANNELS.map((c) => (
          <filter key={c.id} id={`reelbolt-vhs-${c.id}`} colorInterpolationFilters="sRGB">
            <feColorMatrix type="matrix" values={c.matrix} />
          </filter>
        ))}
      </svg>
      {CHANNELS.map((c) => (
        <AbsoluteFill key={c.id} style={{mixBlendMode: 'screen', filter: `url(#reelbolt-vhs-${c.id})`, transform: `translateX(${c.sign * amount}px)`}}>
          {children}
        </AbsoluteFill>
      ))}
      {tears.map((t, i) => (
        <AbsoluteFill key={i} style={{clipPath: `inset(${t.top}% 0 ${Math.max(0, 100 - t.top - t.height)}% 0)`, transform: `translateX(${t.shift}px)`}}>
          {children}
        </AbsoluteFill>
      ))}
    </AbsoluteFill>
  );
};
