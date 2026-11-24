export type OutputMediaKind = 'image' | 'audio' | 'video';

const IMAGE = new Set(['png', 'jpg', 'jpeg', 'webp', 'gif', 'avif', 'bmp']);
const AUDIO = new Set(['wav', 'mp3', 'm4a', 'aac', 'ogg', 'oga', 'flac', 'opus']);

/**
 * What an output is, read from its file name or storage key. The outputs list and a step result carry no
 * mime type, and the server picks the content type from the same extension, so this agrees with what is
 * actually streamed. Anything unknown stays `video` (the historical default for a render).
 */
export function outputMediaKind(nameOrKey: string | null | undefined): OutputMediaKind {
  const last = (nameOrKey ?? '').split('?')[0].split('/').pop() ?? '';
  const dot = last.lastIndexOf('.');
  const ext = dot >= 0 ? last.slice(dot + 1).toLowerCase() : '';
  if (IMAGE.has(ext)) return 'image';
  if (AUDIO.has(ext)) return 'audio';
  return 'video';
}
