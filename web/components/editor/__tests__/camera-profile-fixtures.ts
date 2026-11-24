import type { MediaProbe } from '@/lib/editing/media/types';
import { withDetectedProfile } from '@/lib/editing/color/auto-profile';

export const PROBE_FIXTURE: MediaProbe = { durationSec: 2, fpsNum: 30, fpsDen: 1, width: 1920, height: 1080, videoCodec: 'h264', audioCodec: null, container: 'mp4', audioChannels: null, audioSampleRateHz: null, bitrateBps: 1 };

/** Marks a clip as converted automatically in this session, through the real helper. */
export function markAutoAppliedForTest(clipId: string): void {
  withDetectedProfile([], { ...PROBE_FIXTURE, cameraProfileHint: { profile: 'appleLog', confidence: 'high', reason: 'x' } }, clipId);
}
