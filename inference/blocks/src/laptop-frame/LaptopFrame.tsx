// ReelBolt block "laptop-frame" (MIT). Laptop with a screen slot for children and a base below it.
import React from 'react';
import {AbsoluteFill, useVideoConfig} from 'remotion';

export type LaptopFrameProps = {
  /** Screen content. */
  children?: React.ReactNode;
  /** Screen width in px (16:10). Default min(1200, 80% of the frame width). */
  width?: number;
  /** Body colour. Default '#c7cbd1'. */
  body?: string;
};

export const LaptopFrame: React.FC<LaptopFrameProps> = ({children, width: widthProp, body = '#c7cbd1'}) => {
  const {width: frameW} = useVideoConfig();
  const width = widthProp ?? Math.min(1200, Math.round(frameW * 0.8));
  const height = width * 0.625;
  const bezel = width * 0.012;
  return (
    <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center'}}>
      <div style={{display: 'flex', flexDirection: 'column', alignItems: 'center', filter: 'drop-shadow(0 30px 40px rgba(0,0,0,0.45))'}}>
        <div style={{width, height, background: '#0b0d12', borderRadius: `${width * 0.02}px ${width * 0.02}px 0 0`, padding: bezel, paddingTop: bezel * 2, boxSizing: 'border-box', boxShadow: `inset 0 0 0 2px ${body}`}}>
          <div style={{width: '100%', height: '100%', overflow: 'hidden', position: 'relative', background: '#fff', borderRadius: 4}}>{children}</div>
        </div>
        <div style={{width: width * 1.12, height: width * 0.025, background: `linear-gradient(180deg, ${body}, #9ca3af)`, borderRadius: `0 0 ${width * 0.03}px ${width * 0.03}px`, position: 'relative'}}>
          <div style={{position: 'absolute', left: '50%', top: 0, width: width * 0.16, height: width * 0.008, marginLeft: -width * 0.08, background: '#8b9099', borderRadius: '0 0 8px 8px'}} />
        </div>
      </div>
    </AbsoluteFill>
  );
};
