/**
 * Pure helpers behind the camera-profile controls (inspector, Colour page, phone look sheet): the picker's groups, the one
 * `setProperty inputProfile` op, "apply to all clips from this file", and the double-conversion check.
 */

import type { EditOp } from '../engine';
import { mediaRefOf } from '../preview/scheduler';
import type { EditTimeline, MediaClip } from '../types';
import { CAMERA_PROFILE_IDS, getCameraProfile, type CameraProfileId, type CameraProfileStatus } from './camera-profiles';

export interface CameraProfileChoice {
  id: CameraProfileId;
  displayName: string;
  maker: string;
  status: CameraProfileStatus;
}

export interface CameraProfileGroup {
  maker: string;
  items: CameraProfileChoice[];
}

/** The 12 profiles grouped by maker, in the catalogue's order. Display names are factual maker names. */
export function cameraProfileGroups(): CameraProfileGroup[] {
  const groups: CameraProfileGroup[] = [];
  for (const id of CAMERA_PROFILE_IDS) {
    const def = getCameraProfile(id);
    const choice: CameraProfileChoice = { id, displayName: def.displayName, maker: def.maker, status: def.status };
    const group = groups.find((g) => g.maker === def.maker);
    if (group) group.items.push(choice);
    else groups.push({ maker: def.maker, items: [choice] });
  }
  return groups;
}

export function cameraProfileName(id: string | null | undefined): string | null {
  return id && (CAMERA_PROFILE_IDS as readonly string[]).includes(id) ? getCameraProfile(id as CameraProfileId).displayName : null;
}

export function cameraProfileStatus(id: string | null | undefined): CameraProfileStatus | null {
  return id && (CAMERA_PROFILE_IDS as readonly string[]).includes(id) ? getCameraProfile(id as CameraProfileId).status : null;
}

export const setProfileOp = (itemId: string, profile: CameraProfileId | null): EditOp => ({ type: 'setProperty', itemId, key: 'inputProfile', value: profile });

/** The picture clips of the timeline that read the same file as `clip`, the clip itself included. */
export function clipsFromSameFile(doc: EditTimeline, clip: MediaClip): MediaClip[] {
  const ref = mediaRefOf(clip.assetRef);
  if (!ref) return [];
  return doc.tracks
    .filter((t) => t.kind === 'video')
    .flatMap((t) => t.items)
    .filter((i): i is MediaClip => i.type === 'mediaClip' && mediaRefOf(i.assetRef) === ref);
}

/** One batch (one undo step): `profile` on every other clip from the same file. Empty when there is none. */
export function applyToAllFromFileOps(doc: EditTimeline, clip: MediaClip, profile: CameraProfileId | null): EditOp[] {
  return clipsFromSameFile(doc, clip)
    .filter((c) => c.id !== clip.id && (c.inputProfile ?? null) !== profile)
    .map((c) => setProfileOp(c.id, profile));
}

/** True when any grade node of the clip carries a LUT. */
export function clipHasLut(clip: MediaClip): boolean {
  return (clip.grade?.nodes ?? []).some((n) => Boolean(n.lut));
}
