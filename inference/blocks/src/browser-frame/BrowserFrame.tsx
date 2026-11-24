// ReelBolt block "browser-frame" (MIT). Browser window chrome (traffic lights, tab, address bar) with a content slot.
import React from 'react';
import {AbsoluteFill, useVideoConfig} from 'remotion';

export type BrowserFrameProps = {
  /** Page content shown in the viewport. */
  children?: React.ReactNode;
  /** Address bar text. Default 'example.com'. */
  url?: string;
  /** Tab title. Default 'Untitled'. */
  title?: string;
  /** Window width in px. Default min(1400, 90% of the frame width). */
  width?: number;
  /** Window height in px. Default 800. */
  height?: number;
  /** Dark chrome. Default false. */
  dark?: boolean;
  /** Font family. Default 'Inter'. */
  font?: string;
};

export const BrowserFrame: React.FC<BrowserFrameProps> = ({children, url = 'example.com', title = 'Untitled', width: widthProp, height: heightProp, dark = false, font = 'Inter'}) => {
  const {width: frameW} = useVideoConfig();
  const width = widthProp ?? Math.min(1400, Math.round(frameW * 0.9));
  const height = heightProp ?? Math.round(width * (800 / 1400));
  const chrome = dark ? '#2a2d34' : '#e8eaee';
  const bar = dark ? '#3b3f48' : '#ffffff';
  const fg = dark ? '#e5e7eb' : '#374151';
  return (
    <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center'}}>
      <div style={{width, height, borderRadius: 16, overflow: 'hidden', background: '#fff', boxShadow: '0 30px 80px rgba(0,0,0,0.45)', display: 'flex', flexDirection: 'column', fontFamily: `${font}, sans-serif`}}>
        <div style={{height: 72, flex: 'none', background: chrome, display: 'flex', alignItems: 'center', padding: '0 18px', gap: 14}}>
          <div style={{display: 'flex', gap: 8}}>
            {['#ff5f57', '#febc2e', '#28c840'].map((c) => (
              <div key={c} style={{width: 16, height: 16, borderRadius: '50%', background: c}} />
            ))}
          </div>
          <div style={{padding: '8px 20px', borderRadius: 8, background: bar, color: fg, fontSize: 24, fontWeight: 600}}>{title}</div>
          <div style={{flex: 1, height: 44, borderRadius: 22, background: bar, color: fg, fontSize: 24, display: 'flex', alignItems: 'center', padding: '0 16px', opacity: 0.9}}>{url}</div>
        </div>
        <div style={{flex: 1, position: 'relative', overflow: 'hidden'}}>{children}</div>
      </div>
    </AbsoluteFill>
  );
};
