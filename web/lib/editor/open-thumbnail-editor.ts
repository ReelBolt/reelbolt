/**
 * Getting from "an image" to "the thumbnail editor on a document of that image".
 *
 * Two entry points use this — the Editor dashboard's document list and the project Files tab's
 * "Edit as thumbnail" — so the rule lives once: reuse the newest document already opened on that
 * image, and only create one when there is none. Without that, pressing the button twice leaves two
 * documents of the same picture, the second one losing the first one's work.
 */

import { projectFileUrl } from '@/lib/api/files';
import { createImageDocument, getImageDocuments } from '@/lib/api/image-documents';
import type { ProjectFile } from '@/lib/types/project';

/** A photo's own pixel size, or null when this browser could not decode it in time. */
export type ImageMeasurer = (url: string) => Promise<{ width: number; height: number } | null>;

/** The largest side the create route accepts for a browser-measured size; a larger one is not sent. */
const MAX_MEASURED_SIDE = 65535;

/** How long the browser may spend decoding a photo before the document is created without its size. */
export const MEASURE_TIMEOUT_MS = 5000;

/**
 * Decodes a photo in the browser and answers its pixel size. The server measures the file itself when
 * it creates the picture; this is the fallback it uses when it cannot read the file's header. Never
 * throws: a photo that fails to decode, or takes too long, answers null.
 */
export const measureImageInBrowser: ImageMeasurer = (url) =>
  new Promise((resolve) => {
    if (typeof Image === 'undefined') {
      resolve(null);
      return;
    }
    const img = new Image();
    const timer = setTimeout(() => {
      img.src = '';
      resolve(null);
    }, MEASURE_TIMEOUT_MS);
    img.decoding = 'async';
    img.onload = () => {
      clearTimeout(timer);
      resolve(img.naturalWidth > 0 && img.naturalHeight > 0 ? { width: img.naturalWidth, height: img.naturalHeight } : null);
    };
    img.onerror = () => {
      clearTimeout(timer);
      resolve(null);
    };
    img.src = url;
  });

/** The route of the editor on one document. The project rides along as a query parameter. */
export function thumbnailEditorHref(projectId: string, documentId: string): string {
  return `/editor/thumbnails/${encodeURIComponent(documentId)}?project=${encodeURIComponent(projectId)}`;
}

/**
 * The image types the editor cannot draw although they are images: its own layered exports
 * (Photoshop, OpenRaster). The server refuses them as a picture's source or as a placed layer too.
 */
const LAYERED_IMAGE_TYPES = new Set(['image/vnd.adobe.photoshop', 'image/openraster']);

/** Whether a file of this type can be opened in, or placed into, the picture editor. */
export function isEditableImageType(mimeType: string | null | undefined): boolean {
  const type = (mimeType || '').toLowerCase().split(';')[0].trim();
  return type.startsWith('image/') && !LAYERED_IMAGE_TYPES.has(type);
}

/** Whether a file can be opened in the thumbnail editor. */
export function canEditAsThumbnail(file: Pick<ProjectFile, 'mimeType'>): boolean {
  return isEditableImageType(file.mimeType);
}

/**
 * The id of the document to open for `file`: the most recently updated existing one, else a new
 * document named after the file, created with the photo's decoded size. The list route answers newest first, but the comparison does not
 * rely on that.
 */
export async function documentForImage(
  projectId: string,
  file: Pick<ProjectFile, 'id' | 'originalFileName'>,
  measure: ImageMeasurer = measureImageInBrowser,
): Promise<string> {
  const existing = (await getImageDocuments(projectId))
    .filter((d) => d.sourceProjectFileId === file.id)
    .sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt));
  if (existing.length > 0) return existing[0].id;
  // The photo's size as this browser sees it travels with the request: the server prefers its own
  // reading of the file, and falls back to this one, so the canvas opens at the photo's shape.
  const size = await measure(projectFileUrl(projectId, file.id)).catch(() => null);
  const created = await createImageDocument(projectId, {
    sourceProjectFileId: file.id,
    name: file.originalFileName,
    ...(size && size.width >= 1 && size.height >= 1 && size.width <= MAX_MEASURED_SIDE && size.height <= MAX_MEASURED_SIDE
      ? { width: Math.round(size.width), height: Math.round(size.height) }
      : {}),
  });
  return created.id;
}
