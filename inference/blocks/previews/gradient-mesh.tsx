import React from 'react';
import {AbsoluteFill} from 'remotion';
import {GradientMesh} from '../gradient-mesh/GradientMesh.tsx';

const Center: React.FC = () => (
  <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center', fontFamily: 'Inter, sans-serif'}}>
    <div style={{color: '#fff', fontSize: 110, fontWeight: 800}}>Your title</div>
    <div style={{color: '#fff', fontSize: 40, marginTop: 12}}>A subtitle goes here</div>
  </AbsoluteFill>
);

export const Preview: React.FC = () => (
  <GradientMesh><Center /></GradientMesh>
);
