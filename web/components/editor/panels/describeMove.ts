import type { Rational as Rate } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import { msg, type Msg } from '@/i18n/msg';

const tc = (frame: number, rate: Rate) => framesToTimecode(frame, rate);

/** A one-sentence, timecode-based description of how a clip's place on the timeline changed. */
export function describeMove(
  before: { recordIn: number; recordOut: number },
  after: { recordIn: number; recordOut: number },
  rate: Rate,
): string {
  if (before.recordIn !== after.recordIn) return `Moved from ${tc(before.recordIn, rate)} to ${tc(after.recordIn, rate)}`;
  if (before.recordOut !== after.recordOut) return `Now ends at ${tc(after.recordOut, rate)}, was ${tc(before.recordOut, rate)}`;
  return 'Unchanged position';
}

/** The same sentence as `describeMove`, as a catalogue message (what the panel shows). */
export function describeMoveMsg(
  before: { recordIn: number; recordOut: number },
  after: { recordIn: number; recordOut: number },
  rate: Rate,
): Msg {
  if (before.recordIn !== after.recordIn) return msg('editor-helpers-2.fmt.movedFrom', { from: tc(before.recordIn, rate), to: tc(after.recordIn, rate) });
  if (before.recordOut !== after.recordOut) return msg('editor-helpers-2.fmt.endsAt', { end: tc(after.recordOut, rate), was: tc(before.recordOut, rate) });
  return msg('editor-helpers-2.fmt.unchanged');
}
