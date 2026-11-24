import React from 'react';
import {AbsoluteFill} from 'remotion';
import {MarqueeTicker} from '../marquee-ticker/MarqueeTicker.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0b1020'}}>
    <MarqueeTicker />
  </AbsoluteFill>
);
