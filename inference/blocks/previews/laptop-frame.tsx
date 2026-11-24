import React from 'react';
import {AbsoluteFill} from 'remotion';
import {LaptopFrame} from '../laptop-frame/LaptopFrame.tsx';

const Page: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(160deg, #eef2ff, #fce7f3)', fontFamily: 'Inter, sans-serif', padding: 90, boxSizing: 'border-box'}}>
    <div style={{fontSize: 44, fontWeight: 800, color: '#1e1b4b'}}>Dashboard</div>
    <div style={{display: 'flex', gap: 20, marginTop: 24}}>
      {['#6366f1', '#ec4899', '#14b8a6'].map((c) => (<div key={c} style={{flex: 1, height: 140, borderRadius: 16, background: c}} />))}
    </div>
    <div style={{marginTop: 24, height: 120, borderRadius: 16, background: '#fff'}} />
  </AbsoluteFill>
);

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0f172a'}}><LaptopFrame width={880}><Page /></LaptopFrame></AbsoluteFill>
);
