// ReelBolt block "gradient-mesh" (MIT). Background of slowly drifting radial colour blobs over a base colour.
import React from 'react';
import {AbsoluteFill, random, useCurrentFrame} from 'remotion';

export type GradientMeshProps = {
  children?: React.ReactNode;
  /** Blob colours (CSS). Default blue, violet, pink, cyan. */
  colors?: string[];
  /** Base colour under the blobs. Default '#0b1020'. */
  background?: string;
  /** Drift speed multiplier. Default 1. */
  speed?: number;
  /** Seed for blob positions. Default 'mesh'. */
  seed?: string;
};

export const GradientMesh: React.FC<GradientMeshProps> = ({children, colors = ['#3b82f6', '#8b5cf6', '#ec4899', '#06b6d4'], background = '#0b1020', speed = 1, seed = 'mesh'}) => {
  const frame = useCurrentFrame();
  const t = (frame / 30) * speed;
  const layers = colors.map((c, i) => {
    const x = 20 + random(`${seed}-x-${i}`) * 60 + Math.sin(t * 0.5 + i * 1.7) * 18;
    const y = 20 + random(`${seed}-y-${i}`) * 60 + Math.cos(t * 0.4 + i * 2.3) * 18;
    return `radial-gradient(circle at ${x}% ${y}%, ${c} 0%, transparent 45%)`;
  });
  return (
    <AbsoluteFill style={{background: `${layers.join(', ')}, ${background}`}}>{children}</AbsoluteFill>
  );
};
