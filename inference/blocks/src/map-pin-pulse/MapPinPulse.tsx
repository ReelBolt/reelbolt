// ReelBolt block "map-pin-pulse" (MIT). World outline with pins that drop in and send out pulsing rings (offline, d3-geo).
import React from 'react';
import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';
import {labelSide, landPath, worldProjection} from '../_shared/geo.ts';
import type {City} from '../_shared/geo.ts';

export type MapPinPulseProps = {
  /** Places to pin. Default: London, New York, Tokyo. */
  pins?: City[];
  /** Land colour. Default '#1e293b'. */
  landColor?: string;
  /** Land outline colour. Default '#334155'. */
  strokeColor?: string;
  /** Pin and pulse colour. Default '#f43f5e'. */
  pinColor?: string;
  /** Label colour. Default '#f8fafc'. */
  textColor?: string;
  /** Frames between pins appearing. Default 10. */
  staggerFrames?: number;
  /** CSS font-family (installed font). Default 'Inter, sans-serif'. */
  fontFamily?: string;
};

const DEFAULT_PINS: City[] = [
  {name: 'London', lon: -0.13, lat: 51.5},
  {name: 'New York', lon: -74, lat: 40.7},
  {name: 'Tokyo', lon: 139.7, lat: 35.7},
];

export const MapPinPulse: React.FC<MapPinPulseProps> = ({
  pins = DEFAULT_PINS,
  landColor = '#1e293b',
  strokeColor = '#334155',
  pinColor = '#f43f5e',
  textColor = '#f8fafc',
  staggerFrames = 10,
  fontFamily = 'Inter, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {width, height, fps} = useVideoConfig();
  const projection = React.useMemo(() => worldProjection(width, height), [width, height]);
  const land = React.useMemo(() => landPath(projection), [projection]);
  const fs = Math.round(height * 0.032);
  const r = height * 0.011;
  return (
    <AbsoluteFill style={{fontFamily}}>
      <svg width={width} height={height} style={{position: 'absolute', inset: 0}}>
        <path d={land} fill={landColor} stroke={strokeColor} strokeWidth={1} />
        {pins.map((pin, i) => {
          const xy = projection([pin.lon, pin.lat]);
          if (!xy) return null;
          const local = frame - i * staggerFrames - 5;
          const drop = spring({frame: local, fps, config: {damping: 12}});
          const cycle = (((local % 45) + 45) % 45) / 45;
          return (
            <g key={pin.name} transform={`translate(${xy[0]},${xy[1]})`} opacity={Math.min(1, drop)}>
              {local > 0 ? (
                <circle r={r + cycle * r * 5} fill="none" stroke={pinColor} strokeWidth={3} opacity={interpolate(cycle, [0, 1], [0.8, 0])} />
              ) : null}
              <circle r={r * drop} fill={pinColor} stroke="#fff" strokeWidth={2} />
            </g>
          );
        })}
      </svg>
      {pins.map((pin, i) => {
        const xy = projection([pin.lon, pin.lat]);
        if (!xy) return null;
        const side = labelSide(xy[0], width);
        const o = interpolate(frame - i * staggerFrames - 8, [0, 10], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
        return (
          <div
            key={pin.name}
            style={{
              position: 'absolute',
              top: xy[1] - fs * 0.7,
              fontSize: fs,
              fontWeight: 700,
              color: textColor,
              whiteSpace: 'nowrap',
              opacity: o,
              textShadow: '0 2px 8px rgba(0,0,0,0.7)',
              ...(side === 'right' ? {left: xy[0] + r * 2.2} : {right: width - xy[0] + r * 2.2}),
            }}
          >
            {pin.name}
          </div>
        );
      })}
    </AbsoluteFill>
  );
};
