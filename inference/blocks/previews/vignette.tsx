import React from 'react';
import {AbsoluteFill} from 'remotion';
import {Vignette} from '../vignette/Vignette.tsx';

const Backdrop: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(135deg, #1e3a8a, #9333ea 60%, #f97316)', justifyContent: 'center', alignItems: 'center', fontFamily: 'Inter, sans-serif'}}>
    <div style={{color: '#fff', fontSize: 120, fontWeight: 800}}>Launch day</div>
    <div style={{color: '#fff', fontSize: 40, marginTop: 12}}>Everything you shipped this year</div>
  </AbsoluteFill>
);

export const Preview: React.FC = () => (
  <Vignette intensity={0.9} pulse={0.5}><Backdrop /></Vignette>
);
