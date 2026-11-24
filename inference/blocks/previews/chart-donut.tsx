import React from 'react';
import {AbsoluteFill} from 'remotion';
import {ChartDonut} from '../chart-donut/ChartDonut.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(160deg,#0f172a,#1e293b)'}}>
    <ChartDonut centerLabel="Visitors (k)" data={[{label: 'Search', value: 420}, {label: 'Social', value: 280}, {label: 'Direct', value: 190}, {label: 'Email', value: 110}]} />
  </AbsoluteFill>
);
