/**
 * The assistant tells the open picture editor what it just changed.
 *
 * The chat stream carries a `data-imageDocumentChanged` frame the moment one of the assistant's tools
 * changes a picture (adds a layer, edits one, queues a designed graphic). The assistant drawer and the
 * editor are far apart in the tree — the drawer is mounted once for the whole dashboard — so the
 * message travels as a window event rather than through props or context: the drawer announces, and
 * whichever editor has that document open listens.
 *
 * Each change is announced ONCE per page, by its `changeId`: reopening the drawer re-renders every
 * message, and a change the editor already brought in must not be brought in again.
 *
 * No React here; the window is the only global touched.
 */

/** The window event's name. */
export const IMAGE_DOCUMENT_CHANGED_EVENT = 'reelbolt:image-document-changed';

export const IMAGE_DOCUMENT_CHANGE_KINDS = [
  'layersAdded',
  'layersChanged',
  'layersDeleted',
  'documentChanged',
  'graphicQueued',
] as const;
export type ImageDocumentChangeKind = (typeof IMAGE_DOCUMENT_CHANGE_KINDS)[number];

/** One change the assistant made to a picture, as the chat stream's data frame carries it. */
export interface ImageDocumentChange {
  /** Unique per change; the announcement is de-duplicated by it. */
  changeId: string;
  projectId: string;
  documentId: string;
  kind: ImageDocumentChangeKind;
  /** The layers it touched (none for a document-level change or a queued graphic). */
  layerIds: string[];
  /** What changed, in the assistant's plain words. */
  summary: string;
  /** For `graphicQueued`: the design job that was started. */
  graphicId?: string;
}

/**
 * Reads a data frame's payload into a change, or null when it is not one (a newer or malformed
 * frame is ignored rather than trusted: it came over the network).
 */
export function parseImageDocumentChange(raw: unknown): ImageDocumentChange | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const r = raw as Record<string, unknown>;
  const str = (v: unknown) => (typeof v === 'string' && v.length > 0 ? v : null);
  const changeId = str(r.changeId);
  const projectId = str(r.projectId);
  const documentId = str(r.documentId);
  const kind = str(r.kind);
  if (!changeId || !projectId || !documentId || !kind) return null;
  if (!(IMAGE_DOCUMENT_CHANGE_KINDS as readonly string[]).includes(kind)) return null;
  const layerIds = Array.isArray(r.layerIds) ? r.layerIds.filter((v): v is string => typeof v === 'string' && v.length > 0) : [];
  const change: ImageDocumentChange = {
    changeId,
    projectId,
    documentId,
    kind: kind as ImageDocumentChangeKind,
    layerIds,
    summary: typeof r.summary === 'string' ? r.summary : '',
  };
  const graphicId = str(r.graphicId);
  if (graphicId) change.graphicId = graphicId;
  return change;
}

/** Every change announced on this page so far. Module-level on purpose: it outlives the drawer. */
const announced = new Set<string>();

/**
 * Tells the page about a change, once per `changeId`. Answers true when it was announced now, false
 * when it already had been (or there is no window to announce on).
 */
export function announceImageDocumentChange(change: ImageDocumentChange): boolean {
  if (!change.changeId || announced.has(change.changeId)) return false;
  if (typeof window === 'undefined') return false;
  announced.add(change.changeId);
  window.dispatchEvent(new CustomEvent<ImageDocumentChange>(IMAGE_DOCUMENT_CHANGED_EVENT, { detail: change }));
  return true;
}

/** Listens for announced changes. Answers the function that stops listening. */
export function onImageDocumentChange(handler: (change: ImageDocumentChange) => void): () => void {
  if (typeof window === 'undefined') return () => {};
  const listener = (event: Event) => {
    const change = (event as CustomEvent<ImageDocumentChange>).detail;
    if (change) handler(change);
  };
  window.addEventListener(IMAGE_DOCUMENT_CHANGED_EVENT, listener);
  return () => window.removeEventListener(IMAGE_DOCUMENT_CHANGED_EVENT, listener);
}

/** Forgets every announcement. For tests only. */
export function resetAnnouncedImageDocumentChanges(): void {
  announced.clear();
}
