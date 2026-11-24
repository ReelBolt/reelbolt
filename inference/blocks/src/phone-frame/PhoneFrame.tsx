// ReelBolt block "phone-frame" (MIT). Smartphone bezel with notch and a screen slot for children.
import React from 'react';
import {AbsoluteFill} from 'remotion';

export type PhoneFrameProps = {
  /** Screen content. */
  children?: React.ReactNode;
  /** Phone outer width in px (height is 2.05x). Default 400. */
  width?: number;
  /** Bezel colour. Default '#111827'. */
  bezel?: string;
  /** Tilt in degrees. Default 0. */
  rotate?: number;
};

export const PhoneFrame: React.FC<PhoneFrameProps> = ({children, width = 400, bezel = '#111827', rotate = 0}) => {
  const height = width * 2.05;
  const pad = width * 0.032;
  return (
    <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center'}}>
      <div style={{width, height, borderRadius: width * 0.14, background: bezel, padding: pad, boxSizing: 'border-box', transform: `rotate(${rotate}deg)`, boxShadow: '0 30px 80px rgba(0,0,0,0.5), inset 0 0 0 2px rgba(255,255,255,0.12)', position: 'relative'}}>
        <div style={{width: '100%', height: '100%', borderRadius: width * 0.11, overflow: 'hidden', position: 'relative', background: '#fff'}}>{children}</div>
        <div style={{position: 'absolute', top: pad + width * 0.02, left: '50%', width: width * 0.28, height: width * 0.075, marginLeft: -width * 0.14, borderRadius: width * 0.04, background: bezel}} />
      </div>
    </AbsoluteFill>
  );
};
