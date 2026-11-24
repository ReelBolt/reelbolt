import type { ClipJobPaint } from '@/lib/editing/background/job-marks';
import { approxTextWidth } from './clipPaint';
import type { PaintContext, PaintTheme } from './paint';
import type { PaintLabels } from './paintLabels';

export const JOB_CHIP_HEIGHT = 14;
export const JOB_STRIP_HEIGHT = 4;

export interface JobChipRect {
  x: number;
  y: number;
  w: number;
  h: number;
  text: string;
}

/** The words on a clip's job chip: the job's name while it runs, "Ready to review", or the failure label. */
export function jobChipText(job: ClipJobPaint, words: Pick<PaintLabels, 'jobProcessing' | 'jobReview' | 'jobFailed'>): string {
  return job.state === 'running' ? job.label || words.jobProcessing : job.state === 'needsReview' ? words.jobReview : words.jobFailed;
}

/**
 * Where the chip sits: right-aligned in the label band. The same function places it for painting and for the click, so
 * they cannot disagree. A clip too narrow to hold the chip and some name shows no chip (the foot strip still shows).
 */
export function jobChipRect(job: ClipJobPaint, words: Pick<PaintLabels, 'jobProcessing' | 'jobReview' | 'jobFailed'>, x0: number, w: number, bodyY: number, bodyH: number): JobChipRect | null {
  if (bodyH < 22) return null;
  const text = jobChipText(job, words);
  const width = Math.ceil(approxTextWidth(text)) + 10;
  if (w < width + 30) return null;
  return { x: x0 + w - width - 3, y: bodyY + 2, w: width, h: JOB_CHIP_HEIGHT, text };
}

/**
 * A job is working on (or finished with) this clip: the body dims while it runs, a strip along the foot shows how far
 * it is (a bar when the job reports progress, a moving stripe when it does not, static hatching when `nowMs` is
 * undefined - reduced motion), and a chip names the state. "Ready to review" is a clickable chip in the accent colour;
 * a failure is a chip in the error colour.
 */
export function paintClipJob(ctx: PaintContext, job: ClipJobPaint, words: Pick<PaintLabels, 'jobProcessing' | 'jobReview' | 'jobFailed'>, x0: number, w: number, bodyY: number, bodyH: number, nowMs: number | undefined, theme: PaintTheme): void {
  ctx.save();
  if (job.state === 'running') {
    ctx.globalAlpha = 0.22;
    ctx.fillStyle = theme.chip;
    ctx.fillRect(x0 + 0.5, bodyY, Math.max(1, w - 1), bodyH);
    ctx.globalAlpha = 1;
    const stripY = bodyY + bodyH - JOB_STRIP_HEIGHT;
    const stripW = Math.max(1, w - 1);
    ctx.fillStyle = theme.chip;
    ctx.globalAlpha = 0.85;
    ctx.fillRect(x0 + 0.5, stripY, stripW, JOB_STRIP_HEIGHT);
    ctx.globalAlpha = 1;
    ctx.fillStyle = theme.item.transition;
    if (job.progress !== undefined) {
      ctx.fillRect(x0 + 0.5, stripY, Math.max(1, Math.round(stripW * job.progress)), JOB_STRIP_HEIGHT);
    } else if (nowMs !== undefined) {
      const block = Math.min(60, Math.max(8, stripW * 0.3));
      const at = ((nowMs / 16) % (stripW + block)) - block;
      const from = Math.max(0, at);
      const to = Math.min(stripW, at + block);
      if (to > from) ctx.fillRect(x0 + 0.5 + from, stripY, to - from, JOB_STRIP_HEIGHT);
    } else {
      // Reduced motion: hatching, no movement.
      ctx.strokeStyle = theme.item.transition;
      ctx.lineWidth = 1;
      ctx.beginPath();
      for (let d = 0; d < stripW; d += 5) {
        ctx.moveTo(x0 + 0.5 + d, stripY + JOB_STRIP_HEIGHT);
        ctx.lineTo(x0 + 0.5 + d + JOB_STRIP_HEIGHT, stripY);
      }
      ctx.stroke();
    }
  }
  const chip = jobChipRect(job, words, x0, w, bodyY, bodyH);
  if (chip) {
    ctx.fillStyle = job.state === 'failed' ? theme.renderRequired : job.state === 'needsReview' ? theme.selected : theme.chip;
    ctx.globalAlpha = job.state === 'running' ? 0.85 : 1;
    ctx.fillRect(chip.x, chip.y, chip.w, chip.h);
    ctx.globalAlpha = 1;
    ctx.fillStyle = job.state === 'running' ? theme.itemText : theme.chip;
    ctx.font = '12px system-ui, sans-serif';
    ctx.textAlign = 'left';
    ctx.fillText(chip.text, chip.x + 5, chip.y + 1);
  }
  ctx.restore();
}
