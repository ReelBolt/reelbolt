// ReelBolt block "particle-field" (MIT). Seeded drifting dots with optional connecting lines (constellation look).
import React from 'react';
import {AbsoluteFill, random, useCurrentFrame, useVideoConfig} from 'remotion';

export type ParticleFieldProps = {
  children?: React.ReactNode;
  /** Number of dots (keep under 90). Default 60. */
  count?: number;
  /** Draw lines between near dots. Default true. */
  connect?: boolean;
  /** Max distance (px) for a connecting line. Default 180. */
  linkDistance?: number;
  /** Dot and line colour. Default '#7dd3fc'. */
  color?: string;
  /** Background colour. Default '#07101f'. */
  background?: string;
  /** Drift speed in px per second. Default 25. */
  speed?: number;
  /** Seed. Default 'particles'. */
  seed?: string;
};

export const ParticleField: React.FC<ParticleFieldProps> = ({children, count = 60, connect = true, linkDistance = 180, color = '#7dd3fc', background = '#07101f', speed = 25, seed = 'particles'}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const n = Math.min(90, Math.max(0, count));
  const t = frame / 30;
  const wrap = (v: number, m: number) => ((v % m) + m) % m;
  const pts = Array.from({length: n}, (_, i) => {
    const a = random(`${seed}-d-${i}`) * Math.PI * 2;
    const v = speed * (0.4 + random(`${seed}-v-${i}`));
    return {
      x: wrap(random(`${seed}-x-${i}`) * width + Math.cos(a) * v * t, width),
      y: wrap(random(`${seed}-y-${i}`) * height + Math.sin(a) * v * t, height),
      r: 2 + random(`${seed}-r-${i}`) * 3,
    };
  });
  const lines: React.ReactNode[] = [];
  if (connect) {
    for (let i = 0; i < n; i++) {
      for (let j = i + 1; j < n; j++) {
        const d = Math.hypot(pts[i].x - pts[j].x, pts[i].y - pts[j].y);
        if (d < linkDistance) {
          lines.push(<line key={`${i}-${j}`} x1={pts[i].x} y1={pts[i].y} x2={pts[j].x} y2={pts[j].y} stroke={color} strokeOpacity={0.35 * (1 - d / linkDistance)} strokeWidth={1.5} />);
        }
      }
    }
  }
  return (
    <AbsoluteFill style={{background}}>
      <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} style={{position: 'absolute'}}>
        {lines}
        {pts.map((p, i) => (
          <circle key={i} cx={p.x} cy={p.y} r={p.r} fill={color} fillOpacity={0.8} />
        ))}
      </svg>
      {children}
    </AbsoluteFill>
  );
};
