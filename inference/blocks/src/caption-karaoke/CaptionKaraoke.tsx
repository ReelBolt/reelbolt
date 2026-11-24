// ReelBolt block "caption-karaoke" (MIT). Words fill with colour as they are spoken.
import React from 'react';
import type {Caption} from '@remotion/captions';
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {groupIntoPages, pageAt, wordText} from '../_shared/captions.ts';

export type CaptionKaraokeProps = {
  /** Word-level captions (the Caption type of @remotion/captions). */
  captions: Caption[];
  /** Words per page. Default 4. */
  maxWordsPerPage?: number;
  /** Colour of a word before it is spoken. Default 'rgba(255,255,255,0.55)'. */
  color?: string;
  /** Colour a word fills with. Default '#ffd54a'. */
  highlightColor?: string;
  /** Font size in px. Default 6.5% of the shorter frame side. */
  fontSize?: number;
  /** CSS font-family (installed font). Default 'Montserrat, sans-serif'. */
  fontFamily?: string;
};

export const CaptionKaraoke: React.FC<CaptionKaraokeProps> = ({
  captions,
  maxWordsPerPage = 4,
  color = 'rgba(255,255,255,0.55)',
  highlightColor = '#ffd54a',
  fontSize,
  fontFamily = 'Montserrat, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {fps, width, height} = useVideoConfig();
  const ms = (frame / fps) * 1000;
  const page = pageAt(groupIntoPages(captions, maxWordsPerPage), ms);
  const size = fontSize ?? Math.round(Math.min(height, width) * 0.065);
  if (!page) return null;
  return (
    <AbsoluteFill style={{justifyContent: 'flex-end', alignItems: 'center', padding: `0 8% ${Math.round(height * 0.09)}px`}}>
      <div style={{fontFamily, fontSize: size, fontWeight: 800, lineHeight: 1.2, textAlign: 'center', filter: 'drop-shadow(0 3px 8px rgba(0,0,0,0.6))'}}>
        {page.words.map((word, i) => {
          const fill = interpolate(ms, [word.startMs, word.endMs], [0, 100], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
          return (
            <span
              key={`${i}-${word.startMs}`}
              style={{
                display: 'inline-block',
                margin: `0 ${size * 0.12}px`,
                color: 'transparent',
                backgroundImage: `linear-gradient(90deg, ${highlightColor} ${fill}%, ${color} ${fill}%)`,
                WebkitBackgroundClip: 'text',
                backgroundClip: 'text',
              }}
            >
              {wordText(word)}
            </span>
          );
        })}
      </div>
    </AbsoluteFill>
  );
};
