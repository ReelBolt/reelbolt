import React from 'react';
import { Composition } from 'remotion';
import { Main } from './Main';

const defaults: any = {
  clip: 'default', fps: 30, width: 960, height: 540, frames: 90,
  theme: { bg: '#222' }, scenes: [{ from: 0, bg: '#222' }], elements: [],
};

export const Root: React.FC = () => (
  <Composition
    id="Main"
    component={Main as any}
    durationInFrames={defaults.frames}
    fps={defaults.fps}
    width={defaults.width}
    height={defaults.height}
    defaultProps={defaults}
    calculateMetadata={({ props }: any) => ({
      durationInFrames: props.frames, fps: props.fps, width: props.width, height: props.height,
    })}
  />
);
