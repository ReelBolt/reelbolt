/**
 * The file types the thumbnail editor exports, in the words the Export dialog shows.
 *
 * Every file is made in the browser (the server never rasterises). The flat formats crop to the
 * frame; the two layered ones keep each layer as its own layer — with its name, opacity, blend mode
 * and visibility — so the picture can be finished in Photoshop, GIMP or Krita. The server accepts
 * exactly these content types for an export (`EditImageDocumentSupport.ExportFormats`), so the list
 * is pinned to it by a test.
 */

export type ExportFormatId = 'png' | 'jpeg' | 'webp' | 'avif' | 'psd' | 'ora';

export interface ExportFormat {
  id: ExportFormatId;
  /** Short name on the button. */
  label: string;
  /** One plain sentence about when to pick it. */
  description: string;
  mime: string;
  /** With the dot. */
  extension: string;
  /** Keeps the editor's layers. */
  layered: boolean;
  /** Takes a quality setting. */
  lossy: boolean;
  /** Keeps see-through areas. */
  transparency: boolean;
}

export const EXPORT_FORMATS: readonly ExportFormat[] = [
  {
    id: 'png',
    label: 'PNG',
    description: 'Best quality, keeps see-through areas. Good for YouTube and for editing later.',
    mime: 'image/png',
    extension: '.png',
    layered: false,
    lossy: false,
    transparency: true,
  },
  {
    id: 'jpeg',
    label: 'JPEG',
    description: 'Small file that every site accepts. See-through areas become your background colour, or white.',
    mime: 'image/jpeg',
    extension: '.jpg',
    layered: false,
    lossy: true,
    transparency: false,
  },
  {
    id: 'webp',
    label: 'WebP',
    description: 'Smaller than PNG at the same look, and keeps see-through areas.',
    mime: 'image/webp',
    extension: '.webp',
    layered: false,
    lossy: true,
    transparency: true,
  },
  {
    id: 'avif',
    label: 'AVIF',
    description: 'The smallest files. Only some browsers can make them.',
    mime: 'image/avif',
    extension: '.avif',
    layered: false,
    lossy: true,
    transparency: true,
  },
  {
    id: 'psd',
    label: 'Photoshop (PSD)',
    description: 'Opens in Photoshop and Photopea with every layer kept, named and in order. Text becomes flattened pixels.',
    mime: 'image/vnd.adobe.photoshop',
    extension: '.psd',
    layered: true,
    lossy: false,
    transparency: true,
  },
  {
    id: 'ora',
    label: 'OpenRaster (ORA)',
    description: 'Opens in GIMP and Krita with every layer kept. Text becomes flattened pixels.',
    mime: 'image/openraster',
    extension: '.ora',
    layered: true,
    lossy: false,
    transparency: true,
  },
];

/** The formats shown first: every site and phone takes them. */
export const WEB_FORMAT_IDS: readonly ExportFormatId[] = ['jpeg', 'png', 'webp'];
/** Shown under "More formats". */
export const MORE_FORMAT_IDS: readonly ExportFormatId[] = ['avif', 'psd', 'ora'];

export function exportFormat(id: ExportFormatId): ExportFormat {
  return EXPORT_FORMATS.find((f) => f.id === id) ?? EXPORT_FORMATS[0];
}

/** The largest export the server stores (`EditImageDocumentSupport.MaxExportUploadBytes`). */
export const MAX_EXPORT_BYTES = 200 * 1024 * 1024;

/** The background a format without transparency is flattened onto when the picture has none. */
export const OPAQUE_FALLBACK_BACKGROUND = '#ffffff';

/** The default quality for the lossy formats, 0..1. */
export const DEFAULT_QUALITY = 0.9;

/** "12.4 MB" — plain, decimal, the way file managers say it. */
export function formatBytes(bytes: number): string {
  if (bytes < 1000) return `${bytes} bytes`;
  if (bytes < 1_000_000) return `${(bytes / 1000).toFixed(0)} KB`;
  return `${(bytes / 1_000_000).toFixed(1)} MB`;
}
