import React from 'react';
import {AbsoluteFill} from 'remotion';
import {WordReveal} from '../word-reveal/WordReveal.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0b1020'}}>
    <WordReveal text="Make every frame count" startFrame={2} />
  </AbsoluteFill>
);
