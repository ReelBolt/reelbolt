import React from 'react';
import {AbsoluteFill} from 'remotion';
import {TypewriterCursor} from '../typewriter-cursor/TypewriterCursor.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0b1020'}}>
    <TypewriterCursor text="npx create-video@latest --blank" startFrame={2} />
  </AbsoluteFill>
);
