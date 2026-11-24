import React from 'react';
import {AbsoluteFill} from 'remotion';
import {WipeSlide} from '../wipe-slide/WipeSlide.tsx';

const Panel: React.FC<{color: string; label: string}> = ({color, label}) => (
  <AbsoluteFill style={{background: color, justifyContent: 'center', alignItems: 'center', fontFamily: 'Inter, sans-serif', fontSize: 140, fontWeight: 800, color: '#fff'}}>
    {label}
  </AbsoluteFill>
);

export const Preview: React.FC = () => (
  <WipeSlide from={<Panel color="#1d4ed8" label="A" />} to={<Panel color="#be123c" label="B" />} startFrame={5} durationInFrames={20} />
);
