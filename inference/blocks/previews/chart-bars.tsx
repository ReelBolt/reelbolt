import React from 'react';
import {AbsoluteFill} from 'remotion';
import {ChartBars} from '../chart-bars/ChartBars.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(160deg,#0f172a,#1e293b)'}}>
    <ChartBars title="Users by region" data={[{label: 'Asia', value: 610}, {label: 'Europe', value: 420}, {label: 'Americas', value: 380}, {label: 'Africa', value: 190}]} />
  </AbsoluteFill>
);
