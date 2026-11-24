'use client';

import { Box, Text } from '@mantine/core';
import type { ProjectHistoryHunk } from '@/lib/types/project-history';
import { useT } from '@/i18n';

export interface HunkRow {
  op: '+' | '-' | ' ';
  text: string;
  /** Line number in the older side; null for an added line. */
  fromLine: number | null;
  /** Line number in the newer side; null for a removed line. */
  toLine: number | null;
}

/** Expands a hunk into rows with the line numbers of both sides. */
export function hunkRows(hunk: ProjectHistoryHunk): HunkRow[] {
  let from = hunk.fromStart;
  let to = hunk.toStart;
  return hunk.lines.map((line) => {
    if (line.op === '+') return { op: '+', text: line.text, fromLine: null, toLine: to++ };
    if (line.op === '-') return { op: '-', text: line.text, fromLine: from++, toLine: null };
    return { op: ' ', text: line.text, fromLine: from++, toLine: to++ };
  });
}

const BACKGROUND: Record<HunkRow['op'], string | undefined> = {
  '+': 'var(--mantine-color-green-light)',
  '-': 'var(--mantine-color-red-light)',
  ' ': undefined,
};

/** Text hunks, monospaced, with +/- markers and line numbers; scrolls sideways inside its own box. */
export function HunkView({ hunks }: { hunks: readonly ProjectHistoryHunk[] }) {
  const t = useT('projectHistory');
  if (hunks.length === 0) return <Text size="sm" c="dimmed">{t('diff.noHunks')}</Text>;
  return (
    <Box style={{ overflowX: 'auto', maxWidth: '100%' }} data-testid="hunk-view">
      {hunks.map((hunk, index) => (
        <Box key={`${hunk.fromStart}-${hunk.toStart}-${index}`} mb="sm" style={{ minWidth: 'max-content' }}>
          <Text size="xs" c="dimmed" ff="monospace">
            {t('diff.hunkHeader', {
              fromStart: hunk.fromStart,
              fromEnd: hunk.fromStart + Math.max(hunk.fromCount - 1, 0),
              toStart: hunk.toStart,
              toEnd: hunk.toStart + Math.max(hunk.toCount - 1, 0),
            })}
          </Text>
          {hunkRows(hunk).map((row, i) => (
            <Box
              key={i}
              data-op={row.op}
              style={{ display: 'flex', gap: 8, fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 12, lineHeight: 1.5, background: BACKGROUND[row.op], whiteSpace: 'pre' }}
            >
              <span style={{ width: 40, textAlign: 'right', opacity: 0.7 }}>{row.fromLine ?? ''}</span>
              <span style={{ width: 40, textAlign: 'right', opacity: 0.7 }}>{row.toLine ?? ''}</span>
              <span
                aria-label={row.op === '+' ? t('diff.addedLine') : row.op === '-' ? t('diff.removedLine') : t('diff.contextLine')}
                style={{ width: 12, fontWeight: 700 }}
              >
                {row.op}
              </span>
              <span>{row.text}</span>
            </Box>
          ))}
        </Box>
      ))}
    </Box>
  );
}
