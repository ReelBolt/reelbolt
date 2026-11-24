/**
 * Touch drag of a media tile onto the timeline (R05.4).
 *
 * A finger that lands on a tile and moves is a SCROLL, so the tile is only "lifted" after a
 * long press (the finger rests for {@link LONG_PRESS_MS} without travelling more than
 * {@link MOVE_TOLERANCE_PX}). Once lifted, moves follow the finger and lifting the finger drops.
 * The drop is announced on a window event; the timeline answers it with the same place rules as a
 * mouse drop (`onPoolDrop` -> `buildPlaceOps`), so there is one set of rules, not two.
 */

export const LONG_PRESS_MS = 350;
export const MOVE_TOLERANCE_PX = 10;

export type PoolTouchDragPhase = 'lift' | 'move' | 'drop' | 'cancel';

export interface PoolTouchDragDetail {
  phase: PoolTouchDragPhase;
  mediaRef: string;
  kind: 'video' | 'audio' | 'image';
  clientX: number;
  clientY: number;
}

export const POOL_TOUCH_DRAG_EVENT = 'reelforge:pool-touch-drag';

export function emitPoolTouchDrag(detail: PoolTouchDragDetail): void {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new CustomEvent<PoolTouchDragDetail>(POOL_TOUCH_DRAG_EVENT, { detail }));
}

/** Subscribes to the drag; returns the unsubscribe function. */
export function subscribePoolTouchDrag(handler: (detail: PoolTouchDragDetail) => void): () => void {
  if (typeof window === 'undefined') return () => {};
  const listener = (event: Event) => handler((event as CustomEvent<PoolTouchDragDetail>).detail);
  window.addEventListener(POOL_TOUCH_DRAG_EVENT, listener);
  return () => window.removeEventListener(POOL_TOUCH_DRAG_EVENT, listener);
}

export interface TouchLiftCallbacks {
  onLift: (x: number, y: number) => void;
  onMove: (x: number, y: number) => void;
  onDrop: (x: number, y: number) => void;
  onCancel: () => void;
}

export type TouchLiftState = 'idle' | 'pressing' | 'lifted';

/** The long-press-to-lift state machine, free of the DOM so it can be tested with fake timers. */
export class TouchLiftGesture {
  state: TouchLiftState = 'idle';
  private startX = 0;
  private startY = 0;
  private lastX = 0;
  private lastY = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(private readonly callbacks: TouchLiftCallbacks) {}

  down(x: number, y: number): void {
    this.reset();
    this.state = 'pressing';
    this.startX = this.lastX = x;
    this.startY = this.lastY = y;
    this.timer = setTimeout(() => {
      this.timer = null;
      this.state = 'lifted';
      this.callbacks.onLift(this.lastX, this.lastY);
    }, LONG_PRESS_MS);
  }

  move(x: number, y: number): void {
    this.lastX = x;
    this.lastY = y;
    if (this.state === 'pressing') {
      // Travelled before the press matured: the user is scrolling, not dragging.
      if (Math.hypot(x - this.startX, y - this.startY) > MOVE_TOLERANCE_PX) this.reset();
      return;
    }
    if (this.state === 'lifted') this.callbacks.onMove(x, y);
  }

  up(x: number, y: number): void {
    const wasLifted = this.state === 'lifted';
    this.reset();
    if (wasLifted) this.callbacks.onDrop(x, y);
  }

  cancel(): void {
    const wasLifted = this.state === 'lifted';
    this.reset();
    if (wasLifted) this.callbacks.onCancel();
  }

  private reset(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.state = 'idle';
  }
}
