import type { ComponentType } from 'react';
import {
  IconAdjustments,
  IconCut,
  IconLayersIntersect,
  IconPalette,
  IconPhotoVideo,
  IconSend,
  IconWaveSine,
} from '@tabler/icons-react';
import type { WorkspaceId } from '@/lib/editing/store';

export interface WorkspaceEntry {
  id: WorkspaceId;
  label: string;
  icon: ComponentType<{ size?: number; stroke?: number }>;
  description: string;
  /** Which wave makes this workspace do more than show the shared timeline. */
  availability: 'ready' | 'later';
}

/**
 * The seven workspaces, in bar order. Frozen, so the bar and the keyboard shortcuts read exactly one
 * list and a workspace cannot be added without a label, an icon and a description.
 */
export const WORKSPACES: readonly WorkspaceEntry[] = Object.freeze([
  { id: 'media', label: 'Media', icon: IconPhotoVideo, description: 'The media pool, bins and source viewer.', availability: 'ready' },
  { id: 'cut', label: 'Cut', icon: IconCut, description: 'Fast assembly: source tape, overview strip, smart insert, fast review and boring shots.', availability: 'ready' },
  { id: 'edit', label: 'Edit', icon: IconLayersIntersect, description: 'The timeline, viewer, inspector and history.', availability: 'ready' },
  { id: 'compose', label: 'Compose', icon: IconAdjustments, description: 'Masks, keyers, corner pins, effects, stabilisation, reframe, inserts and adjustment clips.', availability: 'ready' },
  { id: 'color', label: 'Colour', icon: IconPalette, description: 'Primaries, curves, LUTs, looks, colour groups, gallery wipe, scopes and shot match.', availability: 'ready' },
  { id: 'audio', label: 'Audio', icon: IconWaveSine, description: 'The mixer: faders, pan, EQ, loudness and ducking.', availability: 'ready' },
  { id: 'deliver', label: 'Deliver', icon: IconSend, description: 'Render presets, the render queue and the render cache.', availability: 'ready' },
]);

export function isWorkspaceId(value: unknown): value is WorkspaceId {
  return WORKSPACES.some((w) => w.id === value);
}
