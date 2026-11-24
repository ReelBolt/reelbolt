// ReelBolt block "perspective-grid" (MIT). Retro synthwave grid floor scrolling toward the viewer.
import React from 'react';
import {AbsoluteFill, useCurrentFrame} from 'remotion';

export type PerspectiveGridProps = {
  children?: React.ReactNode;
  /** Grid line colour. Default '#ff4fd8'. */
  lineColor?: string;
  /** Sky gradient [top, horizon]. Default deep purple to magenta. */
  sky?: [string, string];
  /** Cell size in px on the floor plane. Default 90. */
  cell?: number;
  /** Scroll speed in px per second. Default 120. */
  speed?: number;
  /** Horizon position as a fraction of height. Default 0.5. */
  horizon?: number;
};

export const PerspectiveGrid: React.FC<PerspectiveGridProps> = ({children, lineColor = '#ff4fd8', sky = ['#120a2e', '#6b1b7a'], cell = 90, speed = 120, horizon = 0.5}) => {
  const frame = useCurrentFrame();
  const off = ((frame / 30) * speed) % cell;
  const h = horizon * 100;
  return (
    <AbsoluteFill style={{background: `linear-gradient(180deg, ${sky[0]} 0%, ${sky[1]} ${h}%, #0a0418 ${h}%)`, overflow: 'hidden'}}>
      <div style={{position: 'absolute', left: 0, right: 0, top: `${h}%`, bottom: 0, perspective: 500, perspectiveOrigin: '50% 0%', overflow: 'hidden'}}>
        <div
          style={{
            position: 'absolute',
            left: '-100%',
            width: '300%',
            top: 0,
            height: '300%',
            transformOrigin: '50% 0%',
            transform: 'rotateX(72deg)',
            backgroundImage: `linear-gradient(${lineColor} 3px, transparent 3px), linear-gradient(90deg, ${lineColor} 3px, transparent 3px)`,
            backgroundSize: `${cell}px ${cell}px`,
            backgroundPosition: `0px ${off}px`,
          }}
        />
      </div>
      <div style={{position: 'absolute', left: 0, right: 0, top: `${h}%`, height: '18%', background: `linear-gradient(180deg, ${sky[1]}, transparent)`}} />
      {children}
    </AbsoluteFill>
  );
};
