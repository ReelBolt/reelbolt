import React from 'react';
import {AbsoluteFill} from 'remotion';
import {ChartLineDraw} from '../chart-line-draw/ChartLineDraw.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(160deg,#0f172a,#1e293b)'}}>
    <ChartLineDraw title="Monthly revenue" values={[12, 15, 14, 19, 24, 22, 31, 38]} labels={['Jan', '', '', '', '', '', '', 'Aug']} />
  </AbsoluteFill>
);
