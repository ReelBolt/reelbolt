'use client';

import { useComputedColorScheme } from '@mantine/core';
import { tagChipColors } from '@/lib/projects/tag-colors';

/** A tag as a coloured pill; the name is always shown as text, never colour alone. */
export function TagChip({ name, color, selected, onClick, pressedLabel }: {
  name: string;
  color: string;
  selected?: boolean;
  onClick?: () => void;
  pressedLabel?: string;
}) {
  const scheme = useComputedColorScheme('light');
  const c = tagChipColors(color)[scheme];
  const style: React.CSSProperties = {
    background: c.bg,
    color: c.text,
    border: `1px solid ${selected ? c.text : 'transparent'}`,
    boxShadow: selected ? `0 0 0 1px ${c.text}` : undefined,
    fontSize: 12,
    lineHeight: '16px',
    fontWeight: 600,
    padding: '2px 8px',
    borderRadius: 999,
    maxWidth: '100%',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    display: 'inline-block',
  };
  if (!onClick) return <span style={style} data-tag-color={color}>{name}</span>;
  return (
    <button
      type="button"
      aria-pressed={!!selected}
      aria-label={pressedLabel}
      onClick={onClick}
      style={{ ...style, cursor: 'var(--rb-cursor-pointer)', minHeight: 44, padding: '2px 12px', display: 'inline-flex', alignItems: 'center' }}
      data-tag-color={color}
    >
      {name}
    </button>
  );
}
