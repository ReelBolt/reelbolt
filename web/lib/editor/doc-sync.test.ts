import { describe, expect, it } from 'vitest';
import { applyWrite, diffDocuments, type DocWrite } from './doc-sync';
import type { EditorDocState, EditorLayer } from './model';

function mk(id: string, zOrder: number, over: Partial<EditorLayer> = {}): EditorLayer {
  return {
    id,
    kind: 'image',
    role: 'overlay',
    label: id,
    zOrder,
    visible: true,
    locked: false,
    opacity: 1,
    blendMode: 'normal',
    transform: { x: 0, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false },
    width: 100,
    height: 100,
    imageUrl: `/i/${id}`,
    matteUrl: null,
    content: null,
    segmentationPrompt: null,
    ...over,
  };
}

const doc = (layers: EditorLayer[], over: Partial<EditorDocState> = {}): EditorDocState => ({
  canvasWidth: 1280,
  canvasHeight: 720,
  backgroundColor: null,
  layers,
  ...over,
});

describe('diffDocuments', () => {
  it('is empty for identical documents', () => {
    const s = doc([mk('a', 1), mk('b', 2)]);
    expect(diffDocuments(s, structuredClone(s))).toEqual([]);
  });

  it('sends only the changed layer fields, and the whole transform when any part moved', () => {
    const server = doc([mk('a', 1)]);
    const local = doc([
      mk('a', 1, {
        opacity: 0.5,
        blendMode: 'screen',
        transform: { x: 10, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false },
      }),
    ]);
    expect(diffDocuments(server, local)).toEqual([
      {
        kind: 'layer',
        layerId: 'a',
        patch: {
          opacity: 0.5,
          blendMode: 'screen',
          transform: { x: 10, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false },
        },
      },
    ]);
  });

  it('sends visibility, lock, label and role', () => {
    const server = doc([mk('a', 1)]);
    const local = doc([mk('a', 1, { visible: false, locked: true, label: 'New', role: 'subject' })]);
    expect(diffDocuments(server, local)[0]).toMatchObject({
      patch: { visible: false, locked: true, label: 'New', role: 'subject' },
    });
  });

  it('sends text content only for a text layer', () => {
    const content = {
      text: 'A',
      fontFamily: 'Anton',
      fontWeight: 400,
      fontSize: 40,
      lineHeight: 1,
      letterSpacing: 0,
      align: 'left' as const,
      color: '#fff',
      stroke: null,
      shadow: null,
      maxWidth: null,
      uppercase: false,
    };
    const server = doc([mk('t', 1, { kind: 'text', content })]);
    const local = doc([mk('t', 1, { kind: 'text', content: { ...content, text: 'B' } })]);
    expect(diffDocuments(server, local)[0]).toMatchObject({ patch: { content: { text: 'B' } } });
  });

  it('puts the document row first, with only the changed canvas fields', () => {
    const server = doc([mk('a', 1)]);
    const local = doc([mk('a', 1, { opacity: 0.5 })], { canvasWidth: 1080, canvasHeight: 1080, backgroundColor: '#fff' });
    const writes = diffDocuments(server, local);
    expect(writes[0]).toEqual({
      kind: 'document',
      patch: { canvasWidth: 1080, canvasHeight: 1080, backgroundColor: '#fff' },
    });
    expect(writes).toHaveLength(2);
  });

  it('sends both sides of the canvas when only one changed (the server takes them together)', () => {
    const writes = diffDocuments(doc([]), doc([], { canvasWidth: 1920 }));
    expect(writes).toEqual([{ kind: 'document', patch: { canvasWidth: 1920, canvasHeight: 720 } }]);
  });

  it('clears the background with null', () => {
    const writes = diffDocuments(doc([], { backgroundColor: '#fff' }), doc([], { backgroundColor: null }));
    expect(writes).toEqual([{ kind: 'document', patch: { backgroundColor: null } }]);
  });

  it('turns a reorder into the fewest 1-based moves and simulates each one', () => {
    const server = doc([mk('a', 1), mk('b', 2), mk('c', 3)]);
    const local = doc([mk('c', 1), mk('a', 2), mk('b', 3)]);
    const writes = diffDocuments(server, local);
    expect(writes).toEqual([{ kind: 'move', layerId: 'c', position: 1 }]);
    // Replaying the writes on the server copy reproduces the local order.
    const replayed = writes.reduce(applyWrite, server);
    expect(replayed.layers.map((l) => l.id)).toEqual(['c', 'a', 'b']);
  });

  it('reproduces any permutation when its moves are replayed', () => {
    const server = doc([mk('a', 1), mk('b', 2), mk('c', 3), mk('d', 4)]);
    const local = doc([mk('d', 1), mk('c', 2), mk('b', 3), mk('a', 4)]);
    const replayed = diffDocuments(server, local).reduce(applyWrite, server);
    expect(replayed.layers.map((l) => l.id)).toEqual(['d', 'c', 'b', 'a']);
    expect(replayed.layers.map((l) => l.zOrder)).toEqual([1, 2, 3, 4]);
  });

  it('ignores a layer only the editor has (creation is adopted elsewhere)', () => {
    const server = doc([mk('a', 1)]);
    const local = doc([mk('a', 1), mk('fresh', 2)]);
    expect(diffDocuments(server, local)).toEqual([]);
  });

  it('hides a layer only the server has (an undone one), once', () => {
    const server = doc([mk('a', 1), mk('undone', 2)]);
    const local = doc([mk('a', 1)]);
    const writes = diffDocuments(server, local);
    expect(writes).toEqual([{ kind: 'layer', layerId: 'undone', patch: { visible: false } }]);
    expect(diffDocuments(writes.reduce(applyWrite, server), local)).toEqual([]);
  });

  it('moves count the undone layer the server still holds', () => {
    const server = doc([mk('a', 1), mk('undone', 2, { visible: false }), mk('b', 3)]);
    const local = doc([mk('b', 1), mk('a', 2)]);
    const writes = diffDocuments(server, local);
    const replayed = writes.reduce(applyWrite, server);
    expect(replayed.layers.map((l) => l.id).filter((id) => id !== 'undone')).toEqual(['b', 'a']);
    expect(diffDocuments(replayed, local)).toEqual([]);
  });
});

describe('applyWrite', () => {
  it('advances the confirmed copy so a second diff is empty', () => {
    const server = doc([mk('a', 1), mk('b', 2)]);
    const local = doc(
      [mk('b', 1, { opacity: 0.4 }), mk('a', 2, { visible: false })],
      { canvasWidth: 1920, canvasHeight: 1080, backgroundColor: '#123456' },
    );
    const writes: DocWrite[] = diffDocuments(server, local);
    const advanced = writes.reduce(applyWrite, server);
    expect(diffDocuments(advanced, local)).toEqual([]);
  });

  it('merges a partial transform into the stored one', () => {
    const server = doc([mk('a', 1)]);
    const next = applyWrite(server, { kind: 'layer', layerId: 'a', patch: { transform: { x: 5 } } });
    expect(next.layers[0].transform).toEqual({ x: 5, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false });
  });

  it('leaves the copy alone for a move of an unknown layer', () => {
    const server = doc([mk('a', 1)]);
    expect(applyWrite(server, { kind: 'move', layerId: 'zz', position: 1 })).toBe(server);
  });
});

describe('crop sync', () => {
  const crop = { x: 0.1, y: 0.2, w: 0.5, h: 0.5 };
  it('sends the crop when it changes and nothing when it does not', () => {
    const server = doc([mk('a', 1)]);
    const local = doc([mk('a', 1, { crop })]);
    expect(diffDocuments(server, local)).toEqual([{ kind: 'layer', layerId: 'a', patch: { crop } }]);
    expect(diffDocuments(local, structuredClone(local))).toEqual([]);
  });
  it('sends the full window to remove a crop, and applying it clears the confirmed copy', () => {
    const server = doc([mk('a', 1, { crop })]);
    const local = doc([mk('a', 1, { crop: null })]);
    const writes = diffDocuments(server, local);
    expect(writes).toEqual([{ kind: 'layer', layerId: 'a', patch: { crop: { x: 0, y: 0, w: 1, h: 1 } } }]);
    expect(applyWrite(server, writes[0]).layers[0].crop).toBeNull();
  });
});

