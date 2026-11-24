'use client';

import React, { useEffect, useState } from 'react';
import { Alert, Badge, Button, Group, ScrollArea, Select, Stack, Text } from '@mantine/core';
import type { EditTimeline } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import { diffTimelines, type ItemChange, type TimelineDiff } from '@/lib/editing/compare/diff';
import { getEditTimeline, listEditTimelines, type EditTimelineSummary } from '@/lib/editing/sync/api';
import { describeMoveMsg } from './describeMove';
import { EditorPanel } from '../ui/EditorPanel';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useT } from '@/i18n';
import { summarizeDiffMsgs } from '@/lib/editing/summary-msg';

export interface ComparePanelProps {
  doc: EditTimeline;
  projectId: string;
  timelineId: string;
  onSeek: (frame: number) => void;
}

const COLOR: Record<ItemChange['kind'], string> = { added: 'green', removed: 'red', moved: 'blue', trimmed: 'orange', changed: 'violet' };

/**
 * Timeline compare (P6): pick another timeline of the project (a duplicate, an alternative cut) and
 * read the EVENT diff from it to this one — items added, removed, moved, trimmed or changed, by id.
 * The other document is read once from the server; this one is the live document in the store.
 */
export function ComparePanel({ doc, projectId, timelineId, onSeek }: ComparePanelProps) {
  const tx = useT('editor.panels.comparePanel');
  const [others, setOthers] = useState<EditTimelineSummary[] | null>(null);
  const [otherId, setOtherId] = useState<string | null>(null);
  const [diff, setDiff] = useState<TimelineDiff | null>(null);
  const [otherName, setOtherName] = useState<string>('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let cancelled = false;
    listEditTimelines(projectId)
      .then((rows) => {
        if (!cancelled) setOthers(rows.filter((r) => r.id !== timelineId));
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(humanizeErrorT(tx, e, tx('couldNotListThe')));
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, timelineId, tx]);

  const compare = async () => {
    if (!otherId) return;
    setBusy(true);
    setError(null);
    try {
      const other = await getEditTimeline(projectId, otherId);
      setOtherName(other.name);
      setDiff(diffTimelines(other.document, doc));
    } catch (e) {
      setError(humanizeErrorT(tx, e, tx('couldNotLoadThe')));
    } finally {
      setBusy(false);
    }
  };

  return (
    <EditorPanel title={tx('compare')} testId="compare-panel">
      <Stack gap="xs">
      <Group gap={6} align="flex-end">
        <Select
          size="xs"
          label={tx('against')}
          placeholder={others === null ? tx('loading') : others.length === 0 ? tx('noOtherTimeline') : tx('chooseATimeline')}
          data={(others ?? []).map((t) => ({ value: t.id, label: tx('rev', { name: t.name, revision: t.revision }) }))}
          value={otherId}
          onChange={setOtherId}
          style={{ flex: 1 }}
          data-testid="compare-other"
        />
        <Button size="compact-sm" fz="xs" onClick={() => void compare()} disabled={!otherId} loading={busy} data-testid="compare-run">
          {tx('compare2')}
        </Button>
      </Group>
      {error && <Alert color="red" p="xs">{error}</Alert>}
      {diff && (
        <>
          <Text size="xs" data-testid="compare-summary">
            {tx('from')} <b>{otherName}</b> {tx('toThisTimeline')} {(summarizeDiffMsgs(diff).map((m) => tx.msg(m)).join(', ') || tx('noDifferences'))}{tx('length')} {framesToTimecode(diff.lengthBefore, doc.settings.rate)} → {framesToTimecode(diff.lengthAfter, doc.settings.rate)}.
          </Text>
          <ScrollArea.Autosize mah={360} scrollbars="y">
            <Stack gap={2}>
              {diff.tracks.map((t) => (
                <Group key={`track-${t.trackId}`} gap={6} wrap="nowrap">
                  <Badge size="xs" fz="xs" variant="light" color={t.kind === 'added' ? 'green' : t.kind === 'removed' ? 'red' : 'violet'}>
                    {tx('track', { kind: t.kind })}
                  </Badge>
                  <Text size="xs">{t.nameAfter ?? t.nameBefore}{t.kind === 'renamed' ? tx('was', { nameBefore: t.nameBefore }) : ''}</Text>
                </Group>
              ))}
              {diff.settingsChanged.length > 0 && (
                <Group gap={6} wrap="nowrap">
                  <Badge size="xs" fz="xs" variant="light" color="violet">{tx('settings')}</Badge>
                  <Text size="xs">{diff.settingsChanged.join(', ')}</Text>
                </Group>
              )}
              {diff.items.map((c) => {
                const at = c.after ?? c.before;
                return (
                  <Group key={c.itemId} gap={6} wrap="wrap" data-testid={`compare-item-${c.itemId}`}>
                    <Badge size="xs" fz="xs" variant="light" color={COLOR[c.kind]} w={64}>
                      {c.kind}
                    </Badge>
                    <Button size="compact-xs" fz="xs" variant="subtle" ff="monospace" onClick={() => at && onSeek(at.recordIn)} disabled={!c.after}>
                      {at ? framesToTimecode(at.recordIn, doc.settings.rate) : ''}
                    </Button>
                    <Text size="xs" truncate style={{ flex: 1 }}>
                      {c.name} <Text component="span" size="12px" c="dimmed">({c.itemType}{c.trackAfter ? tx('on', { trackAfter: c.trackAfter }) : c.trackBefore ? tx('wasOn', { trackBefore: c.trackBefore }) : ''})</Text>
                    </Text>
                    {c.kind !== 'added' && c.kind !== 'removed' && c.before && c.after && (
                      <Text size="12px" c="dimmed" ff="monospace">
                        {tx.msg(describeMoveMsg(c.before, c.after, doc.settings.rate))}
                      </Text>
                    )}
                    {c.changedKeys.length > 0 && (
                      <Text size="12px" c="dimmed">
                        {c.changedKeys.join(', ')}
                      </Text>
                    )}
                  </Group>
                );
              })}
            </Stack>
          </ScrollArea.Autosize>
        </>
      )}
      </Stack>
    </EditorPanel>
  );
}
