import React from 'react';
import {AbsoluteFill} from 'remotion';
import {KpiTicker} from '../kpi-ticker/KpiTicker.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: 'linear-gradient(160deg,#0f172a,#1e293b)'}}>
    <KpiTicker value={128400} prefix="$" label="Monthly revenue" delta="+12.4% vs last month" />
  </AbsoluteFill>
);
