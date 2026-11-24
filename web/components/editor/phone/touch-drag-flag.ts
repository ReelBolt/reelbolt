'use client';

/**
 * W3-D3: drag-to-move and long-press pick-up are experimental on touch and OFF by default. The phone
 * header's overflow menu flips the per-device flag; the strip and the timeline canvas read it. There is
 * ONE implementation (`lib/editing/touch-drag`): this module only re-exports it so the phone code keeps
 * its import path.
 */
export { TOUCH_DRAG_KEY as TOUCH_DRAG_STORAGE_KEY, readTouchDragEnabled as getTouchDragEnabled, setTouchDragEnabled, useTouchDragEnabled } from '@/lib/editing/touch-drag';
