import React from 'react';
import {AbsoluteFill} from 'remotion';
import {MapRegionHighlight} from '../map-region-highlight/MapRegionHighlight.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0b1220'}}>
    <MapRegionHighlight code="BRA" />
  </AbsoluteFill>
);
