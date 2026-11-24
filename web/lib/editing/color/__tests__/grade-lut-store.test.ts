import { describe, expect, it } from 'vitest';
import { createGradeLutStore } from '../grade-lut-store';
import type { AssetRef } from '../../types';

const CUBE = ['LUT_3D_SIZE 2', ...Array.from({ length: 8 }, (_, i) => `${i & 1} ${(i >> 1) & 1} ${(i >> 2) & 1}`)].join('\n');
const ref = (id: string) => ({ projectFileId: id }) as unknown as AssetRef;
const tick = () => new Promise((r) => setTimeout(r, 30));

describe('grade LUT store', () => {
  it('does not reload evicted files when only the status is asked (more files than the cap)', async () => {
    let fetches = 0;
    let ready = 0;
    const files = Array.from({ length: 9 }, (_, i) => ref(`f${i}`));
    const store = createGradeLutStore({
      fetchText: async () => {
        fetches += 1;
        return CUBE;
      },
      onReady: () => {
        ready += 1;
        files.forEach((f) => store.status(f));
      },
    });
    files.forEach((f) => store.status(f));
    for (let i = 0; i < 10; i += 1) await tick();
    expect(fetches).toBe(9);
    expect(ready).toBe(9);
    store.dispose();
  });

  it('answers failed and signals when a file is refused', async () => {
    let ready = 0;
    const store = createGradeLutStore({
      fetchText: async () => 'not a cube',
      onReady: () => {
        ready += 1;
      },
    });
    expect(store.status(ref('bad'))).toBe('loading');
    for (let i = 0; i < 5; i += 1) await tick();
    expect(store.status(ref('bad'))).toBe('failed');
    expect(ready).toBe(1);
    store.dispose();
  });
});
