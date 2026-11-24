import type { VideoOutputFormat, VideoOutputFit } from '@/lib/types/workflow';

/** Customer-facing choices for `VideoCompileStepConfig.outputFormat` (named for where the video goes). */
export const OUTPUT_FORMAT_OPTIONS: { value: VideoOutputFormat; label: string; short: string }[] = [
  { value: 'Source', label: 'Original size', short: 'Original size' },
  { value: 'Vertical', label: 'Vertical 9:16 — Reels/TikTok/Shorts', short: 'Vertical 9:16' },
  { value: 'Square', label: 'Square 1:1 — feed', short: 'Square 1:1' },
  { value: 'Landscape', label: 'Landscape 16:9 — YouTube', short: 'Landscape 16:9' },
  { value: 'Portrait', label: 'Portrait 4:5 — feed', short: 'Portrait 4:5' },
];

export const OUTPUT_FIT_OPTIONS: { value: VideoOutputFit; label: string }[] = [
  { value: 'Crop', label: 'Fill the frame (crop the middle)' },
  { value: 'Subject', label: 'Fill the frame, keep the subject in shot' },
  { value: 'Pad', label: 'Fit whole clip (bars)' },
];

export function outputFormatShortLabel(format: VideoOutputFormat | null | undefined): string {
  return OUTPUT_FORMAT_OPTIONS.find((o) => o.value === (format ?? 'Source'))?.short ?? 'Original size';
}
