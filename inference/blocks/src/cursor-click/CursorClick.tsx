// ReelBolt block "cursor-click" (MIT). Mouse cursor glides to a point, presses, and emits a click ripple.
import React from 'react';
import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';

export type CursorClickProps = {
  /** Content under the cursor. */
  children?: React.ReactNode;
  /** Start position in px. Default [200, 700]. */
  from?: [number, number];
  /** Click position in px. Default [960, 540]. */
  to?: [number, number];
  /** Frame the cursor starts moving. Default 5. */
  startFrame?: number;
  /** Frames the move takes. Default 30. */
  moveFrames?: number;
  /** Ripple colour. Default '#3b82f6'. */
  rippleColor?: string;
  /** Cursor scale. Default 1.6. */
  scale?: number;
};

export const CursorClick: React.FC<CursorClickProps> = ({children, from = [200, 700], to = [960, 540], startFrame = 5, moveFrames = 30, rippleColor = '#3b82f6', scale = 1.6}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const p = spring({frame: frame - startFrame, fps, durationInFrames: moveFrames, config: {damping: 200}});
  // Slight arc so the path is not a straight line.
  const x = interpolate(p, [0, 1], [from[0], to[0]]);
  const y = interpolate(p, [0, 1], [from[1], to[1]]) - Math.sin(p * Math.PI) * 40;
  const clickAt = startFrame + moveFrames + 2;
  const press = interpolate(frame, [clickAt, clickAt + 3, clickAt + 8], [1, 0.82, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const r = interpolate(frame, [clickAt, clickAt + 20], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const appear = interpolate(frame, [startFrame - 4, startFrame], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  return (
    <AbsoluteFill>
      {children}
      {frame >= clickAt && r < 1 && (
        <div style={{position: 'absolute', left: to[0] - 60, top: to[1] - 60, width: 120, height: 120, borderRadius: '50%', border: `4px solid ${rippleColor}`, background: `${rippleColor}33`, opacity: 1 - r, transform: `scale(${0.2 + r * 1.2})`}} />
      )}
      <svg width={32 * scale} height={32 * scale} viewBox="0 0 24 24" style={{position: 'absolute', left: x, top: y, opacity: appear, transform: `scale(${press})`, transformOrigin: '0 0', filter: 'drop-shadow(0 3px 4px rgba(0,0,0,0.4))'}}>
        <path d="M4 2 L4 19 L8.5 14.8 L11.4 21.5 L14 20.4 L11.2 13.8 L17.5 13.6 Z" fill="#fff" stroke="#111" strokeWidth={1.4} strokeLinejoin="round" />
      </svg>
    </AbsoluteFill>
  );
};
