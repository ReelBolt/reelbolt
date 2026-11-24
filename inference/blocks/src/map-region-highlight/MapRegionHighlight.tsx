// ReelBolt block "map-region-highlight" (MIT). Highlights one country on a world outline and eases the camera onto it (offline, d3-geo).
import React from 'react';
import {geoPath} from 'd3';
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {countryFeatures, findCountry, worldProjection} from '../_shared/geo.ts';
import {easeInOutCubic} from '../_shared/motion.ts';

export type MapRegionHighlightProps = {
  /** ISO 3166 alpha-3 (or alpha-2) code of the country. Default 'ITA'. */
  code?: string;
  /** Label shown at the country's centroid. Default: the country's name. */
  label?: string;
  /** Zoom toward the country (0 = stay on the whole world, 1 = fill the frame). Default 0.5. */
  zoom?: number;
  /** Land colour. Default '#1e293b'. */
  landColor?: string;
  /** Land outline colour. Default '#334155'. */
  strokeColor?: string;
  /** Highlight colour. Default '#38bdf8'. */
  highlightColor?: string;
  /** Label colour. Default '#f8fafc'. */
  textColor?: string;
  /** Frame the highlight starts. Default 10. */
  startFrame?: number;
  /** Highlight and zoom length in frames. Default 40. */
  durationInFrames?: number;
  /** CSS font-family (installed font). Default 'Inter, sans-serif'. */
  fontFamily?: string;
};

export const MapRegionHighlight: React.FC<MapRegionHighlightProps> = ({
  code = 'ITA',
  label,
  zoom = 0.5,
  landColor = '#1e293b',
  strokeColor = '#334155',
  highlightColor = '#38bdf8',
  textColor = '#f8fafc',
  startFrame = 10,
  durationInFrames = 40,
  fontFamily = 'Inter, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const country = findCountry(code);
  const p = easeInOutCubic(interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
  const world = React.useMemo(() => worldProjection(width, height), [width, height]);
  const path = React.useMemo(() => geoPath(world), [world]);
  const lands = React.useMemo(() => countryFeatures().filter((f) => f !== country).map((f) => path(f as never) ?? '').join(''), [path, country]);
  // Camera: scale and shift so the country's bounding box fills about 60% of the frame (capped at 12x).
  const [[x0, y0], [x1, y1]] = country ? path.bounds(country as never) : [[0, 0], [width, height]];
  const cx = (x0 + x1) / 2;
  const cy = (y0 + y1) / 2;
  const maxK = country ? Math.min(12, (width * 0.6) / Math.max(x1 - x0, 1), (height * 0.5) / Math.max(y1 - y0, 1)) : 1;
  const z = p * Math.max(0, Math.min(1, zoom));
  const k = 1 + (Math.max(maxK, 1) - 1) * z;
  const tx = cx + (width / 2 - cx) * z - k * cx;
  const ty = cy + (height * 0.45 - cy) * z - k * cy;
  const name = label ?? country?.properties.name ?? code;
  const fs = Math.round(Math.min(height, width) * 0.06);
  // Label sits at the country's projected centroid, following the camera (kept inside the frame).
  const [ccx, ccy] = country ? path.centroid(country as never) : [width / 2, height / 2];
  const lx = Math.min(width * 0.8, Math.max(width * 0.2, tx + k * ccx));
  const ly = Math.min(height * 0.9, Math.max(height * 0.1, ty + k * ccy));
  return (
    <AbsoluteFill style={{fontFamily}}>
      <svg width={width} height={height} style={{position: 'absolute', inset: 0}}>
        <g transform={`translate(${tx},${ty}) scale(${k})`}>
          <path d={lands} fill={landColor} stroke={strokeColor} strokeWidth={1} vectorEffect="non-scaling-stroke" />
          {country ? (
            <path d={path(country as never) ?? ''} fill={highlightColor} fillOpacity={0.15 + 0.85 * p} stroke={highlightColor} strokeWidth={2} vectorEffect="non-scaling-stroke" />
          ) : null}
        </g>
      </svg>
      <div style={{position: 'absolute', left: lx, top: ly, whiteSpace: 'nowrap', fontSize: fs, fontWeight: 800, color: textColor, textShadow: '0 2px 12px rgba(0,0,0,0.7)', opacity: p, transform: `translate(-50%, -50%) translateY(${(1 - p) * 20}px)`}}>
        {name}
      </div>
    </AbsoluteFill>
  );
};
