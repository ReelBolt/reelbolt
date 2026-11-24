import React from 'react';
import {AbsoluteFill} from 'remotion';
import {LowerThirdMinimal} from '../lower-third-minimal/LowerThirdMinimal.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(135deg, #1e3a8a, #0f766e)'}}>
    <LowerThirdMinimal name="Ada Lovelace" subtitle="Founder, Analytical Co." startFrame={2} />
  </AbsoluteFill>
);
