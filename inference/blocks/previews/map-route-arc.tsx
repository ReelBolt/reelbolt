import React from 'react';
import {AbsoluteFill} from 'remotion';
import {MapRouteArc} from '../map-route-arc/MapRouteArc.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0b1220'}}>
    <MapRouteArc />
  </AbsoluteFill>
);
