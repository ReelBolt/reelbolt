import React from 'react';
import {AbsoluteFill} from 'remotion';
import {ZoomThrough} from '../zoom-through/ZoomThrough.tsx';

const Scene: React.FC<{a: string; b: string; label: string}> = ({a, b, label}) => (
  <AbsoluteFill style={{background: `linear-gradient(135deg, ${a}, ${b})`, justifyContent: 'center', alignItems: 'center', fontFamily: 'Inter, sans-serif', fontSize: 64, fontWeight: 800, color: '#fff'}}>
    <div style={{position: 'absolute', left: '5%', right: '5%', top: '12%', height: 8, background: 'rgba(255,255,255,0.5)'}} />
    {label}
  </AbsoluteFill>
);

export const Preview: React.FC = () => (
  <ZoomThrough from={<Scene a="#1d4ed8" b="#0ea5e9" label="Scene A" />} to={<Scene a="#be123c" b="#f97316" label="Scene B" />} startFrame={4} zoom={3} />
);
