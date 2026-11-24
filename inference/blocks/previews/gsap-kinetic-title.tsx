import React from 'react';
import {AbsoluteFill} from 'remotion';
import {GsapKineticTitle} from '../gsap-kinetic-title/GsapKineticTitle.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0f172a'}}>
    <GsapKineticTitle text="Ship the cut today" />
  </AbsoluteFill>
);
