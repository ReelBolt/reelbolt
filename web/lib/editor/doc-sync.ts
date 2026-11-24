/**
 * Turns "what the editor shows" into "what the server must be told".
 *
 * The editor mutates a local {@link EditorDocState} instantly (that is the optimistic half of the
 * contract) and keeps a second copy: what the server last confirmed. {@link diffDocuments} is the
 * difference between the two, expressed as the exact `PATCH` calls the API offers; the page sends
 * them in order and advances its confirmed copy one write at a time with {@link applyWrite}, so a
 * failure half-way leaves the next flush resuming from precisely where this one stopped.
 *
 * Undo and redo need no code here: they only change the local state, and the next diff replays the
 * result — which is how "server writes are replayed from the command stack" holds without the
 * stack knowing the server exists.
 *
 * Not covered, on purpose: layer creation (the server answers with the row, so it is adopted into
 * both copies at once) and layer deletion (immediate and irreversible, see `history.ts`).
 */

import type { UpdateImageDocumentRequest, UpdateImageLayerRequest } from '@/lib/types/image-document';
import { normalizeCrop, renumber, type EditorDocState, type EditorLayer } from './model';

export type DocWrite =
  | { kind: 'document'; patch: Pick<UpdateImageDocumentRequest, 'canvasWidth' | 'canvasHeight' | 'backgroundColor'> }
  | { kind: 'layer'; layerId: string; patch: UpdateImageLayerRequest }
  /** Move to a 1-based position; the server renumbers the rest. */
  | { kind: 'move'; layerId: string; position: number };

const same = (a: unknown, b: unknown) => a === b || JSON.stringify(a) === JSON.stringify(b);

function layerPatch(server: EditorLayer, local: EditorLayer): UpdateImageLayerRequest | null {
  const patch: UpdateImageLayerRequest = {};
  if (server.label !== local.label) patch.label = local.label;
  if (server.role !== local.role) patch.role = local.role;
  if (server.visible !== local.visible) patch.visible = local.visible;
  if (server.locked !== local.locked) patch.locked = local.locked;
  if (server.opacity !== local.opacity) patch.opacity = local.opacity;
  if (server.blendMode !== local.blendMode) patch.blendMode = local.blendMode;
  // The whole transform goes out when any part of it changed: it is six numbers, and a partial
  // patch would make "what does the server hold now?" depend on which fields happened to differ.
  if (!same(server.transform, local.transform)) patch.transform = { ...local.transform };
  if (!same(server.crop ?? null, local.crop ?? null)) patch.crop = local.crop ?? { x: 0, y: 0, w: 1, h: 1 };
  if (local.kind === 'text' && local.content && !same(server.content, local.content)) {
    patch.content = local.content;
  }
  return Object.keys(patch).length > 0 ? patch : null;
}

/**
 * The writes that bring `server` to `local`: the document row first (canvas and background), then
 * stack moves, then per-layer field patches. A layer only the local side has is ignored (creation is
 * adopted into both copies at once). A layer only the SERVER has is one the user undid: it is hidden
 * there, so a reload never shows an undone result (the editor deletes it once it cannot be redone).
 */
export function diffDocuments(server: EditorDocState, local: EditorDocState): DocWrite[] {
  const writes: DocWrite[] = [];

  const docPatch: Extract<DocWrite, { kind: 'document' }>['patch'] = {};
  // The server takes the two sides of the canvas size together (each 16..8192), so a change to
  // either one sends both.
  if (server.canvasWidth !== local.canvasWidth || server.canvasHeight !== local.canvasHeight) {
    docPatch.canvasWidth = local.canvasWidth;
    docPatch.canvasHeight = local.canvasHeight;
  }
  if (server.backgroundColor !== local.backgroundColor) docPatch.backgroundColor = local.backgroundColor;
  if (Object.keys(docPatch).length > 0) writes.push({ kind: 'document', patch: docPatch });

  // Order: compare only the layers both sides know, then walk the target order and move whatever
  // is out of place, simulating each move so the positions we send are positions the server will see.
  const localIds = new Set(local.layers.map((l) => l.id));
  const serverIds = new Set(server.layers.map((l) => l.id));
  // Positions are positions in the SERVER's full stack, so a layer only it has (an undone one that is
  // still held for redo) keeps its place and the layers around it are arranged in local order.
  const common = local.layers.map((l) => l.id).filter((id) => serverIds.has(id));
  let next = 0;
  const target = server.layers.map((l) => (localIds.has(l.id) ? common[next++] : l.id));
  const current = server.layers.map((l) => l.id);
  for (let i = 0; i < target.length; i += 1) {
    if (current[i] === target[i]) continue;
    const from = current.indexOf(target[i]);
    current.splice(from, 1);
    current.splice(i, 0, target[i]);
    writes.push({ kind: 'move', layerId: target[i], position: i + 1 });
  }

  const serverById = new Map(server.layers.map((l) => [l.id, l]));
  for (const layer of local.layers) {
    const before = serverById.get(layer.id);
    if (!before) continue;
    const patch = layerPatch(before, layer);
    if (patch) writes.push({ kind: 'layer', layerId: layer.id, patch });
  }
  for (const layer of server.layers) {
    if (!localIds.has(layer.id) && layer.visible) {
      writes.push({ kind: 'layer', layerId: layer.id, patch: { visible: false } });
    }
  }
  return writes;
}

/** The layers the server holds that the editor has taken out because the user undid them. */
export function undoneLayers(server: EditorDocState, local: EditorDocState): EditorLayer[] {
  const localIds = new Set(local.layers.map((l) => l.id));
  return server.layers.filter((l) => !localIds.has(l.id));
}

/** Advances the confirmed copy by one write the server accepted. */
export function applyWrite(server: EditorDocState, write: DocWrite): EditorDocState {
  switch (write.kind) {
    case 'document':
      return {
        ...server,
        ...(write.patch.canvasWidth !== undefined ? { canvasWidth: write.patch.canvasWidth } : {}),
        ...(write.patch.canvasHeight !== undefined ? { canvasHeight: write.patch.canvasHeight } : {}),
        ...(write.patch.backgroundColor !== undefined
          ? { backgroundColor: write.patch.backgroundColor }
          : {}),
      };
    case 'move': {
      const from = server.layers.findIndex((l) => l.id === write.layerId);
      if (from < 0) return server;
      const layers = [...server.layers];
      const [moved] = layers.splice(from, 1);
      layers.splice(Math.min(layers.length, Math.max(0, write.position - 1)), 0, moved);
      return { ...server, layers: renumber(layers) };
    }
    case 'layer':
      return {
        ...server,
        layers: server.layers.map((l) => {
          if (l.id !== write.layerId) return l;
          const p = write.patch;
          return {
            ...l,
            ...(p.label !== undefined ? { label: p.label } : {}),
            ...(p.role !== undefined ? { role: p.role } : {}),
            ...(p.visible !== undefined ? { visible: p.visible } : {}),
            ...(p.locked !== undefined ? { locked: p.locked } : {}),
            ...(p.opacity !== undefined ? { opacity: p.opacity } : {}),
            ...(p.blendMode !== undefined ? { blendMode: p.blendMode } : {}),
            ...(p.transform ? { transform: { ...l.transform, ...p.transform } } : {}),
            ...(p.content !== undefined ? { content: p.content } : {}),
            ...(p.crop !== undefined ? { crop: normalizeCrop(p.crop) } : {}),
          };
        }),
      };
  }
}
