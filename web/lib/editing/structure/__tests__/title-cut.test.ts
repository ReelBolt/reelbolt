import { describe, expect, it } from 'vitest';
import fixture from '../../__fixtures__/contract/title-position-in-compound-cut.json';
import { flattenTimeline } from '../flatten';
import type { EditTimeline, TitleClip } from '../../types';

describe('flattening a compound that holds a title', () => {
  it('a cut title keeps its pad position, template and transform', () => {
    const flat = flattenTimeline(fixture.document as unknown as EditTimeline);
    const titles = flat.tracks.flatMap((t) => t.items).filter((i): i is TitleClip => i.type === 'titleClip');
    expect(titles).toHaveLength(1);
    expect(titles[0]).toMatchObject({ text: 'Hello', position: 'topLeft', template: 'lowerThird' });
    expect(titles[0].transform?.positionX?.value).toBe(0.05);
    expect(titles[0].transform?.scale?.value).toBe(1.2);
    expect(titles[0].recordOut - titles[0].recordIn).toBeLessThanOrEqual(100);
  });
});
