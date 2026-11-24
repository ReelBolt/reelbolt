import React from 'react';
import {AbsoluteFill} from 'remotion';
import {SAMPLE_CAPTIONS} from '../_shared/captions.ts';
import {CaptionBoxed} from '../caption-boxed/CaptionBoxed.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(160deg,#334155,#0f172a)'}}>
    <CaptionBoxed captions={SAMPLE_CAPTIONS} />
  </AbsoluteFill>
);
