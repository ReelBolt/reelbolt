// ReelBolt block "caption-boxed" (MIT). Captions on a rounded dark panel; the spoken word sits on an accent chip.
import React from 'react';
import type {Caption} from '@remotion/captions';
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {activeWordIndex, groupIntoPages, pageAt, wordText} from '../_shared/captions.ts';

export type CaptionBoxedProps = {
  /** Word-level captions (the Caption type of @remotion/captions). */
  captions: Caption[];
  /** Words per page. Default 5. */
  maxWordsPerPage?: number;
  /** Panel background. Default 'rgba(10,14,28,0.82)'. */
  boxColor?: string;
  /** Text colour. Default '#ffffff'. */
  color?: string;
  /** Background of the spoken word's chip. Default '#3b82f6'. */
  chipColor?: string;
  /** Font size in px. Default 5% of the shorter frame side. */
  fontSize?: number;
  /** CSS font-family (installed font). Default 'Inter, sans-serif'. */
  fontFamily?: string;
};

export const CaptionBoxed: React.FC<CaptionBoxedProps> = ({
  captions,
  maxWordsPerPage = 5,
  boxColor = 'rgba(10,14,28,0.82)',
  color = '#ffffff',
  chipColor = '#3b82f6',
  fontSize,
  fontFamily = 'Inter, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {fps, width, height} = useVideoConfig();
  const ms = (frame / fps) * 1000;
  const page = pageAt(groupIntoPages(captions, maxWordsPerPage), ms);
  const size = fontSize ?? Math.round(Math.min(height, width) * 0.05);
  if (!page) return null;
  const active = activeWordIndex(page, ms);
  const enter = interpolate(ms, [page.startMs, page.startMs + 180], [0, 1], {extrapolateRight: 'clamp'});
  return (
    <AbsoluteFill style={{justifyContent: 'flex-end', alignItems: 'center', padding: `0 8% ${Math.round(height * 0.09)}px`}}>
      <div
        style={{
          fontFamily,
          fontSize: size,
          fontWeight: 700,
          lineHeight: 1.3,
          color,
          textAlign: 'center',
          background: boxColor,
          borderRadius: size * 0.5,
          padding: `${size * 0.35}px ${size * 0.6}px`,
          opacity: enter,
          transform: `scale(${0.94 + 0.06 * enter})`,
        }}
      >
        {page.words.map((word, i) => (
          <span
            key={`${i}-${word.startMs}`}
            style={{display: 'inline-block', padding: `0 ${size * 0.2}px`, borderRadius: size * 0.25, background: i === active ? chipColor : 'transparent'}}
          >
            {wordText(word)}
          </span>
        ))}
      </div>
    </AbsoluteFill>
  );
};
