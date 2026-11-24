import React from 'react';
import {AbsoluteFill} from 'remotion';
import {Sparkline} from '../sparkline/Sparkline.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(160deg,#0f172a,#1e293b)'}}>
    <Sparkline label="Weekly signups" values={[4, 6, 5, 9, 8, 12, 11, 16, 15, 21]} />
  </AbsoluteFill>
);
