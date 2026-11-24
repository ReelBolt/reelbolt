// ReelBolt block "arrow-draw" (MIT). Hand-drawn-style arrow that draws itself along the given points.
import React from 'react';
import {AbsoluteFill, interpolate, random, useCurrentFrame} from 'remotion';

export type ArrowDrawProps = {
  children?: React.ReactNode;
  /** Path points in px (at least 2). Default is a gentle curve across the frame. */
  points?: [number, number][];
  /** Frame the drawing starts. Default 5. */
  startFrame?: number;
  /** Frames to draw the shaft. Default 30. */
  durationInFrames?: number;
  color?: string;
  strokeWidth?: number;
  /** Hand-drawn wobble in px (0 = clean). Default 4. */
  wobble?: number;
  /** Seed for the wobble. Default 'arrow'. */
  seed?: string;
};

export const ArrowDraw: React.FC<ArrowDrawProps> = ({children, points = [[300, 800], [600, 600], [1000, 560], [1400, 380]], startFrame = 5, durationInFrames = 30, color = '#facc15', strokeWidth = 10, wobble = 4, seed = 'arrow'}) => {
  const frame = useCurrentFrame();
  const pts = points.map(([x, y], i) => [x + (random(`${seed}-x-${i}`) - 0.5) * 2 * wobble, y + (random(`${seed}-y-${i}`) - 0.5) * 2 * wobble] as [number, number]);
  // Smooth through the points with quadratic curves between midpoints.
  let d = `M ${pts[0][0]} ${pts[0][1]}`;
  for (let i = 1; i < pts.length - 1; i++) {
    const mx = (pts[i][0] + pts[i + 1][0]) / 2;
    const my = (pts[i][1] + pts[i + 1][1]) / 2;
    d += ` Q ${pts[i][0]} ${pts[i][1]} ${mx} ${my}`;
  }
  const last = pts[pts.length - 1];
  const prev = pts[pts.length - 2] ?? pts[0];
  d += ` L ${last[0]} ${last[1]}`;
  const ang = Math.atan2(last[1] - prev[1], last[0] - prev[0]);
  const head = strokeWidth * 3.5;
  const wing = (a: number) => `M ${last[0]} ${last[1]} L ${last[0] - Math.cos(a) * head} ${last[1] - Math.sin(a) * head}`;
  const draw = interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const headP = interpolate(frame, [startFrame + durationInFrames - 4, startFrame + durationInFrames + 6], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const common = {fill: 'none', stroke: color, strokeWidth, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const};
  return (
    <AbsoluteFill>
      {children}
      <svg width="100%" height="100%" viewBox="0 0 1920 1080" preserveAspectRatio="xMidYMid slice" style={{position: 'absolute'}}>
        <path d={d} pathLength={1} strokeDasharray="1 1" strokeDashoffset={1 - draw} {...common} />
        <g opacity={headP}>
          <path d={wing(ang - 0.45)} {...common} />
          <path d={wing(ang + 0.45)} {...common} />
        </g>
      </svg>
    </AbsoluteFill>
  );
};
