import React from 'react';
import {AbsoluteFill} from 'remotion';
import {CounterNumber} from '../counter-number/CounterNumber.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0b1020'}}>
    <CounterNumber to={12840} label="Videos rendered" startFrame={2} />
  </AbsoluteFill>
);
