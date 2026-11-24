import { ApiError } from '@/lib/api/client';
import { isNetworkFailure } from '@/lib/api/network-error';
import { enT } from '@/lib/i18n-default';
import type { T } from '@/i18n/translator';
import type { PendingUpload } from './drop';

/**
 * English note; components show `t('libLabels.files.acceptedNote')` for the active language.
 * @deprecated Use `t('libLabels.files.acceptedNote')` (translator-taking) so the text follows the active language.
 */
export const ACCEPTED_TYPES_NOTE = enT('libLabels.files.acceptedNote');

const USABLE = /\.(mp4|mov|m4v|webm|mkv|avi|mp3|wav|m4a|aac|ogg|flac|jpe?g|png|webp|gif|avif|svg|txt|md|markdown|pdf|json|csv|srt|vtt|docx?|rtf|html?|ya?ml|xml)$/i;

/** True for files ReelBolt can neither preview nor feed to a workflow (an installer, an archive, ...). */
export function isUnusualType(item: PendingUpload): boolean {
  const { type } = item.file;
  if (/^(video|audio|image|text)\//.test(type)) return false;
  return !USABLE.test(item.file.name);
}

function destinationOf(relativePath: string, destination: string): string {
  const base = destination ? `${destination.replace(/\/+$/, '')}/` : '';
  return `${base}${relativePath}`.replace(/^\/+/, '');
}

/** The pending uploads whose target path already holds a file (same folder, same name). */
export function findDuplicates(
  items: PendingUpload[],
  existing: ReadonlyArray<{ originalPath?: string | null; originalFileName: string }>,
  destination: string,
): PendingUpload[] {
  const taken = new Set(existing.map((f) => (f.originalPath || f.originalFileName).replace(/^\/+/, '').toLowerCase()));
  return items.filter((i) => taken.has(destinationOf(i.relativePath, destination).toLowerCase()));
}

/** A free name in the folder: "a.txt" becomes "a (2).txt", "a (3).txt", ... (case-insensitive, counting other pending uploads). */
export function withUniqueNames(
  items: PendingUpload[],
  existing: ReadonlyArray<{ originalPath?: string | null; originalFileName: string }>,
  destination: string,
): PendingUpload[] {
  const taken = new Set(existing.map((f) => (f.originalPath || f.originalFileName).replace(/^\/+/, '').toLowerCase()));
  return items.map((item) => {
    let path = destinationOf(item.relativePath, destination).toLowerCase();
    let name = item.file.name;
    let relativePath = item.relativePath;
    if (taken.has(path)) {
      const dot = name.lastIndexOf('.');
      const stem = dot > 0 ? name.slice(0, dot) : name;
      const ext = dot > 0 ? name.slice(dot) : '';
      const slash = relativePath.lastIndexOf('/');
      const dir = slash >= 0 ? relativePath.slice(0, slash + 1) : '';
      for (let n = 2; ; n += 1) {
        const candidate = `${stem} (${n})${ext}`;
        const candidatePath = destinationOf(`${dir}${candidate}`, destination).toLowerCase();
        if (!taken.has(candidatePath)) { name = candidate; relativePath = `${dir}${candidate}`; path = candidatePath; break; }
      }
      item = { file: new File([item.file], name, { type: item.file.type }), relativePath };
    }
    taken.add(path);
    return item;
  });
}

/** Why an upload failed, with a cause the person can act on. */
export function explainUploadError(err: unknown, t: T = enT): string {
  if (isNetworkFailure(err)) return t('errors.generic.network');
  const status = err instanceof ApiError ? err.status : 0;
  if (status === 413) {
    return t('libLabels.files.uploadTooLarge');
  }
  const message = err instanceof Error ? err.message.trim() : '';
  if (!message || message === 'Upload failed') {
    return t('libLabels.files.uploadUnfinished');
  }
  return message;
}
