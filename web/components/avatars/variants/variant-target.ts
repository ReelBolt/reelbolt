import { takenVariantLabels } from '@/lib/avatars/variants';
import type { ProjectAvatar } from '@/lib/types/avatars';

/**
 * What a create form needs to make a variant instead of a new avatar: the original (fixed, shown at the top) and
 * the labels its other variants already use.
 */
export interface VariantTarget {
  original: ProjectAvatar;
  takenLabels: readonly string[];
}

export function variantTargetOf(original: ProjectAvatar, avatars: readonly ProjectAvatar[] | null | undefined): VariantTarget {
  return { original, takenLabels: takenVariantLabels(original, avatars) };
}
