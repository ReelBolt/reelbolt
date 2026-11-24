import React from 'react';
import {AbsoluteFill} from 'remotion';
import {LowerThirdGlass} from '../lower-third-glass/LowerThirdGlass.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(135deg, #1e3a8a, #0f766e)'}}>
    <LowerThirdGlass name="Ada Lovelace" subtitle="Founder, Analytical Co." startFrame={2} />
  </AbsoluteFill>
);
