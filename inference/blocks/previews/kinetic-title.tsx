import React from 'react';
import {AbsoluteFill} from 'remotion';
import {KineticTitle} from '../kinetic-title/KineticTitle.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0f172a'}}>
    <KineticTitle text="Ship the cut today" />
  </AbsoluteFill>
);
