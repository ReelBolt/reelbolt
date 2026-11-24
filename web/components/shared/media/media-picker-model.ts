import type { ProjectFile } from '@/lib/types/project';
import type { MediaPoolItem } from '@/lib/editing/media/types';

/** What the picker offers: images, videos, both, or (for recordings) audio alone / video + audio. */
export type MediaPickerKind = 'image' | 'video' | 'both' | 'audio' | 'videoAudio';

export type MediaFileKind = 'image' | 'video' | 'audio';

const PREFIXES: Record<MediaPickerKind, readonly MediaFileKind[]> = {
  image: ['image'],
  video: ['video'],
  both: ['image', 'video'],
  audio: ['audio'],
  videoAudio: ['video', 'audio'],
};

export function fileKindOf(file: Pick<ProjectFile, 'mimeType'>): MediaFileKind | null {
  const mime = file.mimeType ?? '';
  if (mime.startsWith('image/')) return 'image';
  if (mime.startsWith('video/')) return 'video';
  if (mime.startsWith('audio/')) return 'audio';
  return null;
}

export function matchesKind(file: Pick<ProjectFile, 'mimeType'>, kind: MediaPickerKind): boolean {
  const k = fileKindOf(file);
  return k !== null && PREFIXES[kind].includes(k);
}

/** The project files the picker can offer for `kind`, filtered by the search text (name, case-insensitive). */
export function filterMediaFiles(
  files: readonly ProjectFile[] | undefined,
  kind: MediaPickerKind,
  query: string,
  extra?: (file: ProjectFile) => boolean,
): ProjectFile[] {
  const needle = query.trim().toLowerCase();
  return (files ?? []).filter(
    (f) => matchesKind(f, kind) && (!extra || extra(f)) && (needle === '' || f.originalFileName.toLowerCase().includes(needle)),
  );
}

/** Pool items by the project file id they describe (`file:<id>` refs only). */
export function poolByFileId(pool: readonly MediaPoolItem[] | undefined): Map<string, MediaPoolItem> {
  const out = new Map<string, MediaPoolItem>();
  for (const item of pool ?? []) {
    if (item.mediaRef.startsWith('file:')) out.set(item.mediaRef.slice('file:'.length), item);
  }
  return out;
}

export function mediaRefOfFile(fileId: string): string {
  return `file:${fileId}`;
}

export interface SelectionRules {
  multiple: boolean;
  maxSelect?: number;
  minSelect?: number;
}

/** The effective cap: 1 for a single pick, else `maxSelect` (unbounded when unset). */
export function effectiveMax(rules: SelectionRules): number {
  if (!rules.multiple) return 1;
  return rules.maxSelect !== undefined && rules.maxSelect > 0 ? rules.maxSelect : Number.POSITIVE_INFINITY;
}

/** Toggles `id`. Single: picks it (or clears when already picked). Multi: adds unless at the cap. */
export function toggleSelection(current: readonly string[], id: string, rules: SelectionRules): string[] {
  const has = current.includes(id);
  if (!rules.multiple) return has ? [] : [id];
  if (has) return current.filter((s) => s !== id);
  if (current.length >= effectiveMax(rules)) return [...current];
  return [...current, id];
}

/** Adds every visible id (in order) up to the cap; ids already chosen stay. */
export function selectAllVisible(current: readonly string[], visibleIds: readonly string[], rules: SelectionRules): string[] {
  const cap = effectiveMax(rules);
  const out = [...current];
  for (const id of visibleIds) {
    if (out.length >= cap) break;
    if (!out.includes(id)) out.push(id);
  }
  return out;
}

export function canConfirm(count: number, rules: SelectionRules): boolean {
  const min = Math.max(rules.minSelect ?? 1, 0);
  return count >= min && count <= effectiveMax(rules);
}

export type GridKey = 'ArrowLeft' | 'ArrowRight' | 'ArrowUp' | 'ArrowDown' | 'Home' | 'End';

/** The index the arrow keys move the focus to in a grid of `count` tiles `cols` wide; stays put at the edges. */
export function gridNavigate(index: number, key: GridKey, count: number, cols: number): number {
  if (count <= 0) return -1;
  const last = count - 1;
  switch (key) {
    case 'ArrowLeft': return Math.max(index - 1, 0);
    case 'ArrowRight': return Math.min(index + 1, last);
    case 'ArrowUp': return index - cols >= 0 ? index - cols : index;
    case 'ArrowDown': return index + cols <= last ? index + cols : index;
    case 'Home': return 0;
    case 'End': return last;
  }
}

/** Small widths only (server-allowed list); the picker never asks for the original. */
export const THUMB_WIDTH = 320;
export const PREVIEW_WIDTH = 1280;
