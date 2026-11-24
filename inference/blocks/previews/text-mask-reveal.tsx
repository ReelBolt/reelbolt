import React from 'react';
import {AbsoluteFill} from 'remotion';
import {TextMaskReveal} from '../text-mask-reveal/TextMaskReveal.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0b1020'}}>
    <TextMaskReveal text="Summer sale" startFrame={2} />
  </AbsoluteFill>
);
