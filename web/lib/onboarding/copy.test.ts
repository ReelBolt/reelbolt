import { describe, expect, it } from 'vitest';
import { onboardingCopy as c } from './copy';

describe('onboarding copy', () => {
  it('interpolates through functions and keeps jargon out', () => {
    expect(c.tour.progress(2, 8)).toBe('Step 2 of 8');
    expect(c.checklist.howLabel('Upload a file')).toContain('Upload a file');
    expect(JSON.stringify(c)).not.toMatch(/workflow engine|remotion|ffmpeg/i);
    expect(c.checklist.items.open_editor).toMatch(/Early preview/);
  });
});
