// ReelBolt block "caption-outline-bold" (MIT). Heavy upper-case captions with a thick outline; the spoken word turns accent colour.
import React from 'react';
import type {Caption} from '@remotion/captions';
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {activeWordIndex, groupIntoPages, pageAt, wordText} from '../_shared/captions.ts';

export type CaptionOutlineBoldProps = {
  /** Word-level captions (the Caption type of @remotion/captions). */
  captions: Caption[];
  /** Words per page. Default 3. */
  maxWordsPerPage?: number;
  /** Fill colour. Default '#ffffff'. */
  color?: string;
  /** Fill colour of the word being spoken. Default '#ffe14d'. */
  activeColor?: string;
  /** Outline colour. Default '#000000'. */
  outlineColor?: string;
  /** Font size in px. Default 9% of the shorter frame side. */
  fontSize?: number;
  /** CSS font-family (installed font). Default 'Anton, sans-serif'. */
  fontFamily?: string;
};

export const CaptionOutlineBold: React.FC<CaptionOutlineBoldProps> = ({
  captions,
  maxWordsPerPage = 3,
  color = '#ffffff',
  activeColor = '#ffe14d',
  outlineColor = '#000000',
  fontSize,
  fontFamily = 'Anton, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {fps, width, height} = useVideoConfig();
  const ms = (frame / fps) * 1000;
  const page = pageAt(groupIntoPages(captions, maxWordsPerPage), ms);
  const size = fontSize ?? Math.round(Math.min(height, width) * 0.09);
  if (!page) return null;
  const active = activeWordIndex(page, ms);
  const enter = interpolate(ms, [page.startMs, page.startMs + 150], [0, 1], {extrapolateRight: 'clamp'});
  return (
    <AbsoluteFill style={{justifyContent: 'flex-end', alignItems: 'center', padding: `0 8% ${Math.round(height * 0.1)}px`}}>
      <div
        style={{
          fontFamily,
          fontSize: size,
          lineHeight: 1.15,
          textAlign: 'center',
          textTransform: 'uppercase',
          letterSpacing: size * 0.03,
          opacity: enter,
          transform: `translateY(${(1 - enter) * size * 0.3}px)`,
          WebkitTextStroke: `${Math.round(size * 0.14)}px ${outlineColor}`,
          paintOrder: 'stroke fill',
        }}
      >
        {page.words.map((word, i) => (
          <span key={`${i}-${word.startMs}`} style={{color: i === active ? activeColor : color, margin: `0 ${size * 0.1}px`, display: 'inline-block'}}>
            {wordText(word)}
          </span>
        ))}
      </div>
    </AbsoluteFill>
  );
};
