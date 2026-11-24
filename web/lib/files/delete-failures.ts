import type { T } from '@/i18n/translator';
import { ApiError, extractErrorMessage } from '@/lib/api/client';
import { enT } from '@/lib/i18n-default';

/** The server's refusal when keeping the pictures' private copies of a photo would exceed the storage quota. */
export const PICTURE_COPIES_OVER_QUOTA = 'PICTURE_COPIES_OVER_QUOTA';

/** The server's refusal when an avatar lists the file as a reference (docs/design/avatars.md). */
export const FILE_IN_USE_BY_AVATAR = 'FILE_IN_USE_BY_AVATAR';

/** The sentence for a delete refused because an avatar uses the file, in the active language, or null when the body is not that refusal. */
function avatarInUseSentence(reason: unknown, t: T, fileName?: (id: string) => string | null): string | null {
  if (!(reason instanceof ApiError) || reason.code !== FILE_IN_USE_BY_AVATAR) return null;
  const body = (typeof reason.body === 'object' && reason.body !== null ? reason.body : {}) as Record<string, unknown>;
  const name = typeof body.avatarName === 'string' && body.avatarName.trim() ? body.avatarName : null;
  if (!name) return null;
  const id = typeof body.fileId === 'string' ? body.fileId : null;
  const avatars = typeof body.avatars === 'number' && body.avatars > 1 ? body.avatars : 1;
  const file = (id && fileName?.(id)) || t('libLabels.files.thisFile');
  return t('libLabels.files.deleteInUseByAvatar', { file, name, others: avatars - 1 });
}

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

/**
 * The plain sentence the server sent with a refusal, or null when it sent none we can show: only a
 * decoded JSON body counts (never the HTTP status text), and a sentence carrying an id or raw JSON is
 * dropped so the generic count stands in for it.
 */
function serverSentence(reason: unknown): string | null {
  if (!(reason instanceof ApiError)) return null;
  const message = extractErrorMessage(reason.body)?.trim();
  if (!message || GUID.test(message) || /^[[{]/.test(message)) return null;
  return message;
}

/**
 * The message for a multi-file delete in which some deletes failed, or null when none did. Refusals
 * that carry a server sentence are shown in it (identical sentences once); several
 * `PICTURE_COPIES_OVER_QUOTA` refusals become one sentence that counts them; a `FILE_IN_USE_BY_AVATAR`
 * refusal names the file and the avatar in the active language; every other failure
 * keeps the generic count.
 */
export function describeDeleteFailures(
  results: readonly PromiseSettledResult<unknown>[], total: number, t: T = enT, fileName?: (id: string) => string | null,
): string | null {
  const failures = results.filter((r): r is PromiseRejectedResult => r.status === 'rejected');
  if (failures.length === 0) return null;

  const sentences = new Set<string>();
  const quotaSentences: string[] = [];
  let unexplained = 0;
  for (const { reason } of failures) {
    const avatar = avatarInUseSentence(reason, t, fileName);
    if (avatar) { sentences.add(avatar); continue; }
    const sentence = serverSentence(reason);
    if (!sentence) unexplained += 1;
    else if (reason instanceof ApiError && reason.code === PICTURE_COPIES_OVER_QUOTA) quotaSentences.push(sentence);
    else sentences.add(sentence);
  }

  const parts: string[] = [];
  if (quotaSentences.length === 1) parts.push(quotaSentences[0]);
  else if (quotaSentences.length > 1) {
    parts.push(t('libLabels.files.deleteQuotaMany', { count: quotaSentences.length }));
  }
  parts.push(...sentences);
  if (unexplained > 0) {
    parts.push(parts.length === 0
      ? t('libLabels.files.deleteAllFailed', { count: unexplained, total })
      : t('libLabels.files.deleteOthers', { count: unexplained }));
  }
  return parts.join(' ');
}
