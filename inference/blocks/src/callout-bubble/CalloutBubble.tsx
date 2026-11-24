// ReelBolt block "callout-bubble" (MIT). Speech or annotation bubble with a pointer; pops in on a spring.
import React from 'react';
import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';

export type CalloutBubbleProps = {
  children?: React.ReactNode;
  text?: string;
  /** Bubble centre as fractions of the frame [x, y]. Default [0.5, 0.4]. */
  position?: [number, number];
  /** Where the pointer sits. Default 'bottom-left'. */
  pointer?: 'bottom-left' | 'bottom-right' | 'top-left' | 'top-right';
  startFrame?: number;
  background?: string;
  color?: string;
  fontSize?: number;
  font?: string;
};

export const CalloutBubble: React.FC<CalloutBubbleProps> = ({children, text = 'This is the important part', position = [0.5, 0.4], pointer = 'bottom-left', startFrame = 5, background = '#ffffff', color = '#111827', fontSize = 44, font = 'Inter'}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const p = spring({frame: frame - startFrame, fps, config: {damping: 13, stiffness: 170}});
  const op = interpolate(frame, [startFrame, startFrame + 5], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const bottom = pointer.startsWith('bottom');
  const left = pointer.endsWith('left');
  return (
    <AbsoluteFill>
      {children}
      <div style={{position: 'absolute', left: `${position[0] * 100}%`, top: `${position[1] * 100}%`, transform: `translate(-50%, -50%) scale(${p})`, transformOrigin: `${left ? '15%' : '85%'} ${bottom ? '100%' : '0%'}`, opacity: op, maxWidth: '70%'}}>
        <div style={{background, color, fontFamily: `${font}, sans-serif`, fontSize, fontWeight: 700, padding: '24px 36px', borderRadius: 28, boxShadow: '0 14px 36px rgba(0,0,0,0.3)', lineHeight: 1.2, textAlign: 'center'}}>{text}</div>
        <div
          style={{
            position: 'absolute',
            [bottom ? 'bottom' : 'top']: -26,
            [left ? 'left' : 'right']: 48,
            width: 0,
            height: 0,
            borderLeft: '22px solid transparent',
            borderRight: '22px solid transparent',
            [bottom ? 'borderTop' : 'borderBottom']: `30px solid ${background}`,
            transform: `skewX(${left ? -20 : 20}deg)`,
          }}
        />
      </div>
    </AbsoluteFill>
  );
};
