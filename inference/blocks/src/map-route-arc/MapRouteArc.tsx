// ReelBolt block "map-route-arc" (MIT). A great-circle route between two cities that draws on, with a travelling dot (offline, d3-geo).
import React from 'react';
import {geoPath} from 'd3';
import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';
import {greatCircle, labelSide, landPath, worldProjection} from '../_shared/geo.ts';
import type {City} from '../_shared/geo.ts';
import {easeInOutCubic} from '../_shared/motion.ts';

export type MapRouteArcProps = {
  /** Start city. Default Lisbon. */
  from?: City;
  /** End city. Default Sao Paulo. */
  to?: City;
  /** Land colour. Default '#1e293b'. */
  landColor?: string;
  /** Land outline colour. Default '#334155'. */
  strokeColor?: string;
  /** Route colour. Default '#fbbf24'. */
  routeColor?: string;
  /** Label colour. Default '#f8fafc'. */
  textColor?: string;
  /** Frame the route starts drawing. Default 12. */
  startFrame?: number;
  /** Drawing length in frames. Default 45. */
  durationInFrames?: number;
  /** CSS font-family (installed font). Default 'Inter, sans-serif'. */
  fontFamily?: string;
};

export const MapRouteArc: React.FC<MapRouteArcProps> = ({
  from = {name: 'Lisbon', lon: -9.14, lat: 38.72},
  to = {name: 'Sao Paulo', lon: -46.63, lat: -23.55},
  landColor = '#1e293b',
  strokeColor = '#334155',
  routeColor = '#fbbf24',
  textColor = '#f8fafc',
  startFrame = 12,
  durationInFrames = 45,
  fontFamily = 'Inter, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {width, height, fps} = useVideoConfig();
  const projection = React.useMemo(() => worldProjection(width, height), [width, height]);
  const land = React.useMemo(() => landPath(projection), [projection]);
  const circle = React.useMemo(() => greatCircle([from.lon, from.lat], [to.lon, to.lat]), [from, to]);
  const route = geoPath(projection)({type: 'LineString', coordinates: circle}) ?? '';
  const p = easeInOutCubic(interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
  const head = projection(circle[Math.round(p * (circle.length - 1))]);
  const a = projection([from.lon, from.lat]);
  const b = projection([to.lon, to.lat]);
  const arrive = spring({frame: frame - startFrame - durationInFrames, fps, config: {damping: 12}});
  const fs = Math.round(height * 0.034);
  const r = height * 0.011;
  const label = (c: City, xy: [number, number] | null, o: number) => {
    if (!xy) return null;
    const side = labelSide(xy[0], width);
    return (
      <div
        key={c.name}
        style={{position: 'absolute', top: xy[1] - fs * 0.7, fontSize: fs, fontWeight: 700, color: textColor, whiteSpace: 'nowrap', opacity: o, textShadow: '0 2px 8px rgba(0,0,0,0.7)', ...(side === 'right' ? {left: xy[0] + r * 2.4} : {right: width - xy[0] + r * 2.4})}}
      >
        {c.name}
      </div>
    );
  };
  return (
    <AbsoluteFill style={{fontFamily}}>
      <svg width={width} height={height} style={{position: 'absolute', inset: 0}}>
        <path d={land} fill={landColor} stroke={strokeColor} strokeWidth={1} />
        <path d={route} fill="none" stroke={routeColor} strokeWidth={Math.max(3, height * 0.005)} strokeLinecap="round" pathLength={1} strokeDasharray={1} strokeDashoffset={1 - p} />
        {a ? <circle cx={a[0]} cy={a[1]} r={r} fill={routeColor} /> : null}
        {b ? <circle cx={b[0]} cy={b[1]} r={r * Math.min(1, arrive)} fill={routeColor} stroke="#fff" strokeWidth={2} /> : null}
        {head && p > 0 && p < 1 ? <circle cx={head[0]} cy={head[1]} r={r * 1.3} fill="#fff" /> : null}
      </svg>
      {label(from, a, 1)}
      {label(to, b, Math.min(1, arrive))}
    </AbsoluteFill>
  );
};
