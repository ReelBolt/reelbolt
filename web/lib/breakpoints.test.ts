import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { deviceClassOf } from '@/lib/editing/device-class';
import { PHONE_QUERY, PHONE_MAX_WIDTH, TABLET_DOWN_QUERY, TABLET_MAX_WIDTH } from './breakpoints';

describe('breakpoints', () => {
  it('match the editor device classes', () => {
    expect(PHONE_MAX_WIDTH).toBe(600);
    expect(TABLET_MAX_WIDTH).toBe(1100);
    expect(deviceClassOf({ width: 599, height: 900, coarsePointer: false })).toBe('phone');
    expect(deviceClassOf({ width: 600, height: 900, coarsePointer: false })).toBe('tablet');
    expect(TABLET_DOWN_QUERY).toBe('(max-width: 1100px)');
  });

  it('are the queries the run page stylesheet mirrors', () => {
    const css = readFileSync(join(__dirname, '../components/workflow-apps/AppRunPage.module.css'), 'utf8');
    expect(css).toContain(`@media ${PHONE_QUERY} {`);
    // The two-column layout starts at 70em (the 250 px navigation takes its share); below it the page is one column.
    expect(css).toContain('@media (min-width: 70em) {');
  });
});
