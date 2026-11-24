import React from 'react';
import {AbsoluteFill} from 'remotion';
import {SAMPLE_CAPTIONS} from '../_shared/captions.ts';
import {CaptionOutlineBold} from '../caption-outline-bold/CaptionOutlineBold.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(160deg,#334155,#0f172a)'}}>
    <CaptionOutlineBold captions={SAMPLE_CAPTIONS} />
  </AbsoluteFill>
);
