import type { Item, TrackKind } from '@/lib/editing/types';

/** The plain word for an item's kind, shown in the inspector header instead of the internal type name. */
export function itemTypeLabel(type: Item['type'], trackKind: TrackKind): string {
  switch (type) {
    case 'mediaClip':
      return trackKind === 'audio' ? 'Audio clip' : 'Video clip';
    case 'titleClip':
      return 'Title';
    case 'generatorClip':
      return 'Generated clip';
    case 'insertClip':
      return 'Picture';
    case 'caption':
      return 'Caption';
    case 'transition':
      return 'Transition';
    case 'compoundClip':
      return 'Nested clip';
    case 'multicamClip':
      return 'Multicam clip';
    case 'adjustmentClip':
      return 'Adjustment';
  }
}
