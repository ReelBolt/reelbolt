import React from 'react';
import {AbsoluteFill} from 'remotion';
import {MapPinPulse} from '../map-pin-pulse/MapPinPulse.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0b1220'}}>
    <MapPinPulse />
  </AbsoluteFill>
);
