import type { TimelineRenderPreset, TimelineRenderRateControl } from '@/lib/types/workflow';

/**
 * The one table of suggested export values per destination. These are starting points taken from each
 * platform's published upload guidance, never a promise that a file is accepted. Re-read the guidance
 * before changing a number, and record the change in docs/editor.md.
 */
export type PlatformId = 'youtube' | 'reels' | 'tiktok' | 'small';

export interface PlatformSuggestion {
  id: PlatformId;
  /** The format to switch to, or null to keep the one chosen. */
  preset: TimelineRenderPreset | null;
  rateControl: Exclude<TimelineRenderRateControl, 'Quality'>;
  /** Video bitrate in kilobits per second (inside the server's 200..100000 range). */
  videoKbps: number;
  /** Audio bitrate in kilobits per second (inside 64..320). */
  audioKbps: number;
}

/*
 * Sources: YouTube publishes recommended upload bitrates (support.google.com/youtube/answer/1722171:
 * 8 Mbps video for 1080p SDR, 384 kbps stereo audio; our server caps audio at 320, so we suggest 320) and no hard limit. Instagram Reels and TikTok publish
 * no bitrate, so their 5 and 6 Mbps on a 1080x1920 picture are our own suggestions. Small for email or chat
 * is 2.5 Mbps video + 128 kbps audio: about 25 MB for every 75 seconds.
 */
export const PLATFORM_SUGGESTIONS: readonly PlatformSuggestion[] = [
  { id: 'youtube', preset: 'Hd1080p16x9', rateControl: 'Average', videoKbps: 8000, audioKbps: 320 },
  { id: 'reels', preset: 'Vertical9x16', rateControl: 'Capped', videoKbps: 5000, audioKbps: 128 },
  { id: 'tiktok', preset: 'Vertical9x16', rateControl: 'Capped', videoKbps: 6000, audioKbps: 128 },
  { id: 'small', preset: null, rateControl: 'Average', videoKbps: 2500, audioKbps: 128 },
];

export const MIN_VIDEO_KBPS = 200;
export const MAX_VIDEO_KBPS = 100000;
export const MIN_AUDIO_KBPS = 64;
export const MAX_AUDIO_KBPS = 320;

/** Megabits per second (what the stepper shows) to the whole kilobits the server takes, kept in range. */
export function mbpsToKbps(mbps: number): number {
  if (!Number.isFinite(mbps)) return MIN_VIDEO_KBPS;
  return Math.min(MAX_VIDEO_KBPS, Math.max(MIN_VIDEO_KBPS, Math.round(mbps * 1000)));
}

export const kbpsToMbps = (kbps: number): number => Math.round(kbps) / 1000;
