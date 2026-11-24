'use client';

import React from 'react';
import { Box } from '@mantine/core';
import type { MotionPresetId } from '@/lib/editing/clip-depth';
import classes from './clip-depth.module.css';

/** A looping mini preview of a motion preset (a coloured block moving the way the preset moves it). Decorative. */
export function MotionThumb({ id }: { id: MotionPresetId }) {
  return (
    <Box className={classes.thumb} aria-hidden data-testid={`motion-thumb-${id}`}>
      <Box className={`${classes.motionBox} ${classes[id]}`} />
    </Box>
  );
}

/** A looping mini preview of a transition word: clip A is replaced by clip B the way the word does it. Decorative. */
export function TransitionThumb({ type }: { type: string }) {
  const known = ['dissolve', 'fadeblack', 'fadewhite', 'wipeleft', 'wiperight', 'slideleft', 'slideright', 'circleopen'];
  const cls = known.includes(type) ? classes[type] : classes.dissolve;
  return (
    <Box className={`${classes.thumb} ${cls}`} aria-hidden data-testid={`transition-thumb-${type}`}>
      <Box className={classes.thumbA} />
      <Box className={classes.thumbB} />
    </Box>
  );
}
