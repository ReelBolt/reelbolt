// ReelBolt block "typewriter-cursor" (MIT). Text types out character by character behind a blinking cursor.
import React from 'react';
import {AbsoluteFill, useCurrentFrame, useVideoConfig} from 'remotion';

export type TypewriterCursorProps = {
  text: string;
  color?: string;
  /** Cursor colour. Default '#38bdf8'. */
  cursorColor?: string;
  fontSize?: number;
  /** CSS font-family; must be an installed family. Default 'JetBrains Mono, monospace'. */
  fontFamily?: string;
  /** Frame where typing starts. Default 0. */
  startFrame?: number;
  /** Typing speed in characters per second. Default 18. */
  charsPerSecond?: number;
  /** Cursor blink period in frames. Default 16. */
  blinkFrames?: number;
};

export const TypewriterCursor: React.FC<TypewriterCursorProps> = ({
  text,
  color = '#ffffff',
  cursorColor = '#38bdf8',
  fontSize = 64,
  fontFamily = 'JetBrains Mono, monospace',
  startFrame = 0,
  charsPerSecond = 18,
  blinkFrames = 16,
}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const typed = Math.max(0, Math.min(text.length, Math.floor(((frame - startFrame) * charsPerSecond) / fps) + 1));
  const typing = typed < text.length && frame >= startFrame;
  // The cursor stays solid while typing, then blinks.
  const cursorOn = typing || Math.floor((frame - startFrame) / (blinkFrames / 2)) % 2 === 0;
  return (
    <AbsoluteFill style={{justifyContent: 'center', padding: '0 8%', fontFamily, fontSize, color, lineHeight: 1.35}}>
      <div style={{whiteSpace: 'pre-wrap', wordBreak: 'break-word'}}>
        <span>{frame >= startFrame ? text.slice(0, typed) : ''}</span>
        <span
          style={{
            display: 'inline-block',
            width: fontSize * 0.1,
            height: fontSize * 1.05,
            marginLeft: fontSize * 0.08,
            verticalAlign: 'text-bottom',
            background: cursorColor,
            opacity: cursorOn ? 1 : 0,
          }}
        />
        {/* The untyped rest is laid out invisibly so lines do not re-wrap while typing. */}
        <span style={{visibility: 'hidden'}}>{frame >= startFrame ? text.slice(typed) : text}</span>
      </div>
    </AbsoluteFill>
  );
};
