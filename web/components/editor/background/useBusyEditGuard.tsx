'use client';

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Button, Drawer, Group, Modal, Stack, Text } from '@mantine/core';
import { jobsHolding } from '@/lib/editing/background/edit-guard';
import { isProposalEntry } from '@/lib/editing/background/proposal';
import { isAudioCleanupEntry } from '@/lib/editing/background/audio-cleanup';
import { stopObjectJob, type ObjectIntent } from '@/lib/editing/background/object-effect';
import { getExternalJobMarks, type JobMark } from '@/lib/editing/background/job-marks';
import { getResultsStore, type BackgroundEntry } from '@/lib/editing/background/results-store';
import type { EditOp } from '@/lib/editing/engine';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useT } from '@/i18n';

interface Held {
  ops: readonly EditOp[];
  label: string;
  entries: BackgroundEntry[];
  /** Running jobs outside the results store (follow a subject, ...). */
  marks: JobMark[];
}

/**
 * Installs the editor's edit guard. An edit that would delete, trim, move, split, slip, replace or retime a clip a
 * background job is still working on - or change a property that kind of job reads (`lib/editing/background/job-protection.ts`) - is held, and the user chooses: go on (the result follows the edit), stop the
 * job, or go back. A "go on" is remembered per job for this session so a drag does not ask twice. Undo, redo, a
 * remote change and the watcher's own write never reach this guard.
 */
export function useBusyEditGuard(args: { store: EditorStore; projectId: string; timelineId: string; onRefusal: (error: unknown) => void; /** On a phone the dialog is a bottom sheet. */ phone?: boolean }): React.ReactNode {
  const { store, projectId, timelineId, onRefusal, phone = false } = args;
  const t = useT('editor-background');
  const [held, setHeld] = useState<Held | null>(null);
  const [stopError, setStopError] = useState(false);
  const allowed = useRef(new Set<string>());

  useEffect(() => {
    const results = getResultsStore(timelineId);
    store.setEditGuard((ops, doc, label, exemptJobId) => {
      // A job's own result is never held by that job's own mark.
      const { entries: affected, marks } = jobsHolding(ops, doc, results.getEntries(), getExternalJobMarks(), allowed.current, exemptJobId);
      if (affected.length === 0 && marks.length === 0) return true;
      setStopError(false);
      setHeld({ ops, label, entries: affected, marks });
      return false;
    });
    return () => store.setEditGuard(null);
  }, [store, timelineId]);

  const goOn = useCallback(() => {
    if (!held) return;
    for (const e of held.entries) allowed.current.add(e.id);
    for (const m of held.marks) allowed.current.add(m.id);
    setHeld(null);
    try {
      store.dispatchBatch(held.ops, held.label);
    } catch (error) {
      onRefusal(error);
    }
  }, [held, store, onRefusal]);

  const stoppable = held?.entries.find((e) => typeof (e.intent as unknown as ObjectIntent).workflowId === 'string' && e.executionId) ?? null;
  const stop = useCallback(async () => {
    if (!held || !stoppable) return;
    const workflowId = (stoppable.intent as unknown as ObjectIntent).workflowId;
    if (!workflowId || !stoppable.executionId) return;
    try {
      await stopObjectJob(projectId, workflowId, stoppable.executionId);
      getResultsStore(timelineId).resolve(stoppable.id, 'discarded');
      const rest = held.entries.filter((e) => e.id !== stoppable.id);
      for (const e of rest) allowed.current.add(e.id);
      setHeld(null);
      store.dispatchBatch(held.ops, held.label);
    } catch (error) {
      if (error instanceof Error && error.name === 'EditEngineError') onRefusal(error);
      else setStopError(true);
    }
  }, [held, stoppable, projectId, timelineId, store, onRefusal]);

  const first = held?.entries[0];
  const mark = held?.marks[0];
  const tool = mark && !first ? (t.has(`tool.${mark.jobKind}`) ? t(`tool.${mark.jobKind}`) : mark.label) : first ? (first.tool === 'objectEffect' ? t('tool.objectEffect') : isAudioCleanupEntry(first) ? t('tool.audioCleanup') : isProposalEntry(first) ? t('tool.proposal') : t('tool.other')) : '';
  const touch = phone ? { minHeight: 44 } : undefined;
  const body = (
    <Stack gap="sm" data-testid="busy-edit-modal">
        <Text size="sm">{first && isProposalEntry(first) ? t('busyBodyProposal') : mark && !first ? t('busyBodyThrowAway') : t('busyBody')}</Text>
        {!stoppable && (
          <Text size="xs" c="dimmed" data-testid="busy-no-stop">
            {t('busyNoStop')}
          </Text>
        )}
        {stopError && (
          <Text size="xs" c="red" role="alert">
            {t('stopFailed')}
          </Text>
        )}
        <Group justify="flex-end" gap="xs">
          <Button variant="subtle" onClick={() => setHeld(null)} style={touch} data-testid="busy-back">
            {t('back')}
          </Button>
          {stoppable && (
            <Button variant="light" color="red" onClick={() => void stop()} style={touch} data-testid="busy-stop">
              {t('stopJob')}
            </Button>
          )}
          <Button onClick={goOn} style={touch} data-testid="busy-go-on">
            {t('goOn')}
          </Button>
        </Group>
      </Stack>
  );
  const title = t('busyTitle', { tool });
  return phone ? (
    <Drawer opened={held !== null} onClose={() => setHeld(null)} title={title} position="bottom" size="auto" data-testid="busy-edit-sheet">
      {body}
    </Drawer>
  ) : (
    <Modal opened={held !== null} onClose={() => setHeld(null)} title={title} centered>
      {body}
    </Modal>
  );
}
