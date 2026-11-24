import React from 'react';
import {AbsoluteFill} from 'remotion';
import {LowerThirdBar} from '../lower-third-bar/LowerThirdBar.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(135deg, #1e3a8a, #0f766e)'}}>
    <LowerThirdBar name="Ada Lovelace" subtitle="Founder, Analytical Co." startFrame={2} />
  </AbsoluteFill>
);
