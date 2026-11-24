// ReelBolt block "caption-pop-word" (MIT). One word at a time pops in at the centre of the frame.
import React from 'react';
import type {Caption} from '@remotion/captions';
import {AbsoluteFill, spring, useCurrentFrame, useVideoConfig} from 'remotion';
import {wordText} from '../_shared/captions.ts';

export type CaptionPopWordProps = {
  /** Word-level captions (the Caption type of @remotion/captions). */
  captions: Caption[];
  /** Text colour. Default '#ffffff'. */
  color?: string;
  /** Colour of the accent bar under the word. Default '#ff4d6d'. */
  accentColor?: string;
  /** Font size in px. Default 14% of the frame height, shrunk to fit long words in the frame width. */
  fontSize?: number;
  /** Convert to upper case. Default true. */
  uppercase?: boolean;
  /** CSS font-family (installed font). Default 'Anton, sans-serif'. */
  fontFamily?: string;
};

export const CaptionPopWord: React.FC<CaptionPopWordProps> = ({
  captions,
  color = '#ffffff',
  accentColor = '#ff4d6d',
  fontSize,
  uppercase = true,
  fontFamily = 'Anton, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {fps, width, height} = useVideoConfig();
  const ms = (frame / fps) * 1000;
  const word = [...captions].reverse().find((w) => ms >= w.startMs);
  if (!word || ms > word.endMs + 200) return null;
  const pop = spring({frame: frame - Math.round((word.startMs / 1000) * fps), fps, config: {damping: 11, stiffness: 220}});
  const text = uppercase ? wordText(word).toUpperCase() : wordText(word);
  // Default size: 14% of the height, shrunk so the word fits 80% of the frame width (Anton is about 0.57em per character).
  const size = fontSize ?? Math.round(Math.min(height * 0.14, (width * 0.8) / (Math.max(text.length, 1) * 0.57)));
  return (
    <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center', padding: '0 8%'}}>
      <div style={{transform: `scale(${0.5 + 0.5 * pop})`, opacity: Math.min(1, pop * 2), textAlign: 'center'}}>
        <div style={{fontFamily, fontSize: size, lineHeight: 1.1, color, letterSpacing: size * 0.02, textShadow: '0 6px 24px rgba(0,0,0,0.55)'}}>
          {text}
        </div>
        <div style={{height: size * 0.08, width: `${40 * pop}%`, margin: '0 auto', borderRadius: 99, background: accentColor}} />
      </div>
    </AbsoluteFill>
  );
};
