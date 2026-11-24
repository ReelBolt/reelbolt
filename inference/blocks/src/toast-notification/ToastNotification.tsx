// ReelBolt block "toast-notification" (MIT). Notification card that slides in, holds, and slides out.
import React from 'react';
import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';

export type ToastNotificationProps = {
  children?: React.ReactNode;
  title?: string;
  message?: string;
  /** Frame the toast arrives. Default 10. */
  startFrame?: number;
  /** Frames it stays fully visible. Default 60. */
  holdFrames?: number;
  /** Corner position. Default 'top-right'. */
  position?: 'top-right' | 'top-left' | 'bottom-right' | 'bottom-left';
  /** Accent colour. Default '#22c55e'. */
  accent?: string;
  font?: string;
};

export const ToastNotification: React.FC<ToastNotificationProps> = ({children, title = 'Export complete', message = 'Your video is ready to share.', startFrame = 10, holdFrames = 60, position = 'top-right', accent = '#22c55e', font = 'Inter'}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const inP = spring({frame: frame - startFrame, fps, config: {damping: 16, stiffness: 140}});
  const outP = interpolate(frame, [startFrame + holdFrames, startFrame + holdFrames + 12], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const dir = position.endsWith('right') ? 1 : -1;
  const dx = (1 - inP + outP) * 700 * dir;
  const pos: React.CSSProperties = {position: 'absolute', [position.startsWith('top') ? 'top' : 'bottom']: '6%', [position.endsWith('right') ? 'right' : 'left']: '5%'};
  return (
    <AbsoluteFill>
      {children}
      <div style={{...pos, width: 620, transform: `translateX(${dx}px)`, opacity: Math.min(1, inP * 3) * (1 - outP), background: '#fff', borderRadius: 16, padding: '22px 28px', display: 'flex', gap: 16, alignItems: 'center', boxShadow: '0 16px 40px rgba(0,0,0,0.3)', fontFamily: `${font}, sans-serif`}}>
        <div style={{width: 56, height: 56, flex: 'none', borderRadius: '50%', background: accent, color: '#fff', fontSize: 32, fontWeight: 800, display: 'flex', justifyContent: 'center', alignItems: 'center'}}>{'✓'}</div>
        <div>
          <div style={{fontSize: 30, fontWeight: 700, color: '#111827'}}>{title}</div>
          <div style={{fontSize: 26, color: '#4b5563', marginTop: 2}}>{message}</div>
        </div>
      </div>
    </AbsoluteFill>
  );
};
