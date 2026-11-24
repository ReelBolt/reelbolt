/**
 * Auto-apply of a detected camera profile when a clip is added from the pool.
 *
 * The server's probe hint says which camera curve a source most likely carries. Only a `high` hint on a non-HDR video is
 * applied on its own; the conversion is appended to the SAME op batch as the add, so add + conversion are one undo step.
 * A `medium` hint never applies (the inspector offers it as a suggestion). The clip is new, so there is no existing
 * `inputProfile` to respect here; an existing one (including an explicit null) is never rewritten by any other path.
 */

import type { EditOp } from '../engine';
import type { MediaProbe } from '../media/types';
import { isCameraProfileId, type CameraProfileId } from './camera-profiles';

/** Clip ids whose conversion was applied automatically in this session (the inspector's "converted automatically" badge). */
const autoApplied = new Set<string>();

export function wasAutoApplied(clipId: string): boolean {
  return autoApplied.has(clipId);
}

export function clearAutoApplied(clipId: string): void {
  autoApplied.delete(clipId);
}

/** The profile a probe asks to be applied on its own, or null. */
export function autoProfileOf(probe: MediaProbe | null | undefined, kind: string = 'video'): CameraProfileId | null {
  if (kind !== 'video' || !probe) return null;
  const hint = probe.cameraProfileHint;
  if (!hint || hint.confidence !== 'high' || !hint.profile || !isCameraProfileId(hint.profile)) return null;
  if (probe.hdr) return null;
  return hint.profile;
}

/** `ops` plus (when the probe warrants it) a `setProperty inputProfile` for the new clip `clipId`; `ops` is returned as is otherwise. */
export function withDetectedProfile(ops: EditOp[], probe: MediaProbe | null | undefined, clipId: string, kind: string = 'video'): EditOp[] {
  const profile = autoProfileOf(probe, kind);
  if (!profile) return ops;
  autoApplied.add(clipId);
  return [...ops, { type: 'setProperty', itemId: clipId, key: 'inputProfile', value: profile }];
}
