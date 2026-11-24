/**
 * How long a single finger must stay down before a one-finger tool fires (T7). A pinch lands its two fingers
 * a few tens of milliseconds apart, so the first one must never paint, erase or add text before the second
 * arrives. A quick tap still counts: lifting the finger before the wait is over fires the tool at once.
 */
export const TOUCH_INTENT_MS = 90;

/** The tools that change the picture from a single press, so a touch needs the intent wait. */
const ONE_FINGER_TOOLS: ReadonlySet<string> = new Set(['text', 'brush', 'subject', 'layermask']);

export function needsTouchIntent(tool: string, pointerType: string): boolean {
  return pointerType === 'touch' && ONE_FINGER_TOOLS.has(tool);
}

/** Holds one pending tool action until the finger proves it is alone (timer), lifts (tap), or is cancelled (a second finger). */
export class IntentGate {
  private pending: { id: number; timer: ReturnType<typeof setTimeout>; run: () => void } | null = null;

  constructor(private readonly wait: number = TOUCH_INTENT_MS) {}

  get isPending(): boolean {
    return this.pending !== null;
  }

  begin(id: number, run: () => void): void {
    this.cancel();
    const timer = setTimeout(() => this.fire(), this.wait);
    this.pending = { id, timer, run };
  }

  /** The finger lifted: a tap fires the action now. Returns true when it did. */
  release(id: number): boolean {
    if (!this.pending || this.pending.id !== id) return false;
    this.fire();
    return true;
  }

  /** A second finger arrived (or the gesture ended): the action never fires. */
  cancel(): void {
    if (!this.pending) return;
    clearTimeout(this.pending.timer);
    this.pending = null;
  }

  private fire(): void {
    const pending = this.pending;
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pending = null;
    pending.run();
  }
}
