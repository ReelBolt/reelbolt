/**
 * R12.1: the "Hand off" preview — a plain-language count of what an export file will contain, from
 * the document on screen. Pure: the panel renders the sentences, a test pins the counting.
 */

import type { EditTimelineExportFormat } from '../sync/api';

interface CountableItem {
  readonly type?: string;
  readonly text?: string;
  readonly enabled?: boolean;
}
export interface HandOffSource {
  readonly tracks: readonly { readonly id?: string; readonly name?: string; readonly kind?: string; readonly items: readonly CountableItem[] }[];
  readonly markers?: readonly unknown[];
}

export const FORMAT_LABELS: Record<EditTimelineExportFormat, string> = {
  otio: 'OpenTimelineIO',
  edl: 'CMX3600 EDL',
  srt: 'SRT captions',
  vtt: 'WebVTT captions',
  fcpxml: 'Final Cut Pro XML',
};

/** One line per format: what it is for, before the server's exact lists. */
export const FORMAT_SUMMARY: Record<EditTimelineExportFormat, string> = {
  otio: 'The whole edit, lossless. Another ReelBolt project, or a tool that reads OpenTimelineIO, opens it as it is.',
  edl: 'Cuts only: where each video clip starts and ends. No effects, titles, audio levels or captions.',
  srt: 'The captions as subtitles, with their timing.',
  vtt: 'The captions as web subtitles, with their timing.',
  fcpxml: 'A Final Cut Pro or DaVinci Resolve project of the cuts, with audio and roles when you ask for them.',
};

export interface HandOffCounts {
  videoClips: number;
  audioClips: number;
  titles: number;
  captions: number;
  markers: number;
}

export function handOffCounts(doc: HandOffSource): HandOffCounts {
  const c: HandOffCounts = { videoClips: 0, audioClips: 0, titles: 0, captions: 0, markers: doc.markers?.length ?? 0 };
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (item.type === 'caption') {
        if (item.enabled !== false && (item.text ?? '').trim() !== '') c.captions++;
      } else if (item.type === 'title') c.titles++;
      else if (item.type === 'mediaClip') {
        if (track.kind === 'audio') c.audioClips++;
        else c.videoClips++;
      }
    }
  }
  return c;
}

const plural = (n: number, one: string, many = `${one}s`): string => `${n} ${n === 1 ? one : many}`;

/** The sentences of the preview for one format; empty when the file would hold nothing. */
export function handOffLines(format: EditTimelineExportFormat, counts: HandOffCounts, options: { includeAudio?: boolean } = {}): string[] {
  switch (format) {
    case 'srt':
    case 'vtt':
      return counts.captions > 0 ? [plural(counts.captions, 'caption')] : [];
    case 'edl':
      return counts.videoClips > 0 ? [`${plural(counts.videoClips, 'video cut')}`] : [];
    case 'fcpxml': {
      const lines = counts.videoClips > 0 ? [plural(counts.videoClips, 'video clip')] : [];
      if (options.includeAudio !== false && counts.audioClips > 0) lines.push(plural(counts.audioClips, 'audio clip'));
      if (counts.markers > 0) lines.push(plural(counts.markers, 'marker'));
      return lines;
    }
    case 'otio': {
      const lines: string[] = [];
      if (counts.videoClips > 0) lines.push(plural(counts.videoClips, 'video clip'));
      if (counts.audioClips > 0) lines.push(plural(counts.audioClips, 'audio clip'));
      if (counts.titles > 0) lines.push(plural(counts.titles, 'title'));
      if (counts.captions > 0) lines.push(plural(counts.captions, 'caption'));
      if (counts.markers > 0) lines.push(plural(counts.markers, 'marker'));
      return lines;
    }
  }
}

export function mediaSentence(mode: 'reelbolt-url' | 'none'): string {
  return mode === 'reelbolt-url'
    ? 'Your media is not inside the file. It points at the files in this project by link, so the other app needs access to ReelBolt, or you relink the media there.'
    : 'The file names no media, so it opens anywhere.';
}
