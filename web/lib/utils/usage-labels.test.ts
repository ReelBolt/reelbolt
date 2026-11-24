import { describe, expect, it } from 'vitest';

import { USAGE_KINDS } from '@/lib/types/pricing';

import { usageKindLabel, usageModeLabel } from './usage-labels';

describe('usageKindLabel', () => {
  it.each(USAGE_KINDS)('labels %s in plain language rather than passing the raw unit through', (kind) => {
    const label = usageKindLabel(kind);
    expect(label).not.toBe(kind);
    expect(label.toLowerCase()).not.toBe(kind.toLowerCase());
    expect(label.charAt(0)).toBe(label.charAt(0).toUpperCase());
  });

  it('labels the picture units the usage page shows', () => {
    expect(usageKindLabel('ImageGenImages')).toBe('Generated images');
    expect(usageKindLabel('ImageEditImages')).toBe('Edited images');
    expect(usageKindLabel('SegmentationImages')).toBe('Segmented images');
    expect(usageKindLabel('DepthImages')).toBe('Depth-mapped images');
  });

  it('labels the video-segmentation unit, which the picture units are not', () => {
    expect(usageKindLabel('VideoSegmentationFrames')).toBe('Segmented video frames');
  });

  it('passes a kind this build has never heard of through unchanged', () => {
    expect(usageKindLabel('SomeFutureKind')).toBe('SomeFutureKind');
  });
});

describe('usageModeLabel', () => {
  it('names who the provider bill lands on', () => {
    expect(usageModeLabel('Platform')).toBe('ReelBolt providers');
    expect(usageModeLabel('Byo')).toBe('Your own providers');
  });
});
