'use client';

import { useMediaQuery } from '@mantine/hooks';

/**
 * True on a touch screen (the main pointer is a finger). The picture editor uses it for 44 px
 * targets, a tappable list instead of a dropdown, and more room around the fitted picture.
 */
export function useCoarsePointer(): boolean {
  return useMediaQuery('(pointer: coarse)') ?? false;
}
