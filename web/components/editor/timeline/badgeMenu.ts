import type { EditOp } from '@/lib/editing/engine';
import { clipBadges, type ClipBadge, type ClipBadgeKind } from '@/lib/editing/display/clip-badges';
import type { EditTimeline, Item } from '@/lib/editing/types';
import type { ClipMenuItem } from './ClipContextMenu';

type Translate = (key: string, values?: Record<string, string | number>) => string;

export interface BadgeMenuDeps {
  /** `useT('editor-timeline.badges')`. */
  t: Translate;
  readOnly: boolean;
  /** Selects the clip and opens the section of the adjustment. */
  open: (itemId: string, kind: ClipBadgeKind) => void;
  /** One undo step with a clear label. */
  apply: (ops: EditOp[], label: string) => void;
}

const nameOf = (t: Translate, badge: ClipBadge): string => t(badge.labelKey);

/** Open / turn off (on) / remove for one badge. A missing action is not listed (never a disabled control). */
export function badgeActions(itemId: string, badge: ClipBadge, deps: BadgeMenuDeps, dividerBefore = false): ClipMenuItem[] {
  const { t, readOnly } = deps;
  const name = nameOf(t, badge);
  const items: ClipMenuItem[] = [{ id: `badge-open-${badge.kind}`, label: t('open', { name }), dividerBefore, run: () => deps.open(itemId, badge.kind) }];
  if (badge.toggle && !readOnly) {
    const ops = badge.toggle;
    const label = t(badge.enabled ? 'turnOffLabel' : 'turnOnLabel', { name });
    items.push({ id: `badge-toggle-${badge.kind}`, label: t(badge.enabled ? 'turnOff' : 'turnOn', { name }), run: () => deps.apply(ops, label) });
  }
  if (badge.remove.length > 0 && !readOnly) {
    const ops = badge.remove;
    const removeKey = badge.removeKey ?? 'remove';
    items.push({
      id: `badge-remove-${badge.kind}`,
      label: t(removeKey, { name }),
      danger: true,
      ...(badge.removeNoteKey ? { note: t(badge.removeNoteKey) } : {}),
      run: () => deps.apply(ops, t(badge.removeKey ? removeKey : 'removeLabel', { name })),
    });
  }
  return items;
}

/** The small menu of one badge (right-click / long-press on it). */
export function badgeMenuItems(item: Item, doc: EditTimeline, kind: ClipBadgeKind, deps: BadgeMenuDeps): ClipMenuItem[] {
  const badge = clipBadges(item, doc).find((b) => b.kind === kind);
  return badge ? badgeActions(item.id, badge, deps) : [];
}

/** The "Adjustments" group of the clip menu: every badge with the same actions, reachable without a pointer. */
export function adjustmentsMenuItems(item: Item, doc: EditTimeline, deps: BadgeMenuDeps): ClipMenuItem[] {
  const badges = clipBadges(item, doc);
  return badges.flatMap((badge, i) => badgeActions(item.id, badge, deps, i === 0));
}
