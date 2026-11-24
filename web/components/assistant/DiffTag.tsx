import { IconMinus, IconPencil, IconPlus } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { DIFF_KIND_KEY, type DiffKind } from './diff-model';
import classes from './diff.module.css';

export const DIFF_KIND_ICON = {
  added: IconPlus,
  removed: IconMinus,
  changed: IconPencil,
} as const satisfies Record<DiffKind, unknown>;

/**
 * The glyph + word pill that marks a diff kind. Shared by the review tray and the canvas badges,
 * so "Changed" looks identical in both places. The word is real text, never colour alone.
 */
export function DiffTag({ kind, label }: { kind: DiffKind; label?: string }) {
  const t = useT();
  const Icon = DIFF_KIND_ICON[kind];
  return (
    <span className={`${classes.kind} ${classes.tag}`} data-kind={kind}>
      <Icon size={12} stroke={2.5} aria-hidden />
      {label ?? t(DIFF_KIND_KEY[kind])}
    </span>
  );
}
