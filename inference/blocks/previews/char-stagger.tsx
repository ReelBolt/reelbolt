import React from 'react';
import {AbsoluteFill} from 'remotion';
import {CharStagger} from '../char-stagger/CharStagger.tsx';

export const Preview: React.FC = () => (
  <AbsoluteFill style={{background: '#0b1020'}}>
    <CharStagger text="Hello world" startFrame={2} />
  </AbsoluteFill>
);
