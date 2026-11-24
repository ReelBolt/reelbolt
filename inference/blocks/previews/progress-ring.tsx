import React from 'react';
import {AbsoluteFill} from 'remotion';
import {ProgressRing} from '../progress-ring/ProgressRing.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(160deg,#0f172a,#1e293b)'}}>
    <ProgressRing percent={78} label="Goal reached" />
  </AbsoluteFill>
);
