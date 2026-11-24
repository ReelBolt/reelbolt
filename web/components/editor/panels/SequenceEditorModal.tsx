'use client';

import React, { useMemo } from 'react';
import { Badge, Box, Button, Group, Modal, Stack, Text } from '@mantine/core';
import { EditorStore, useEditorStore } from '@/lib/editing/store';
import type { EditOp } from '@/lib/editing/engine';
import { useRefusalToast } from '../shell/refusal-toast';
import type { EditTimeline, Track } from '@/lib/editing/types';
import { compileCommitSequence, openSequenceDocument } from '@/lib/editing/structure/compound';
import { framesToTimecode } from '@/lib/editing/time';
import { TimelineCanvas } from '../timeline/TimelineCanvas';
import { useT } from '@/i18n';

export interface SequenceEditorModalProps {
  /** The parent document and the sequence being edited, or null when closed. */
  doc: EditTimeline;
  sequenceId: string | null;
  newId: () => string;
  onClose: () => void;
  /** Receives the ONE `setSequence` op that writes the edited tracks back. */
  onCommit: (op: EditOp, label: string) => void;
}

const flags = (track: Track) => ({ locked: track.locked, enabled: track.enabled, mute: track.mute, solo: track.solo, collapsed: false });

/**
 * Opening a compound clip (P8): its sequence is edited in its OWN store over a sub-document whose
 * tracks are the sequence's, with the same engine (blade, move, trim, lift, undo). Nothing reaches
 * the parent until "Save to compound", which dispatches one `setSequence` — so the whole session
 * inside the compound is one undo step outside it, and the engine re-checks ids and nesting there.
 */
export function SequenceEditorModal({ doc, sequenceId, newId, onClose, onCommit }: SequenceEditorModalProps) {
  const t = useT('editor.panels.sequenceEditorModal');
  const store = useMemo(() => {
    if (sequenceId === null) return null;
    try {
      return new EditorStore({ timelineId: `${doc.id}:${sequenceId}`, projectId: doc.projectId, doc: openSequenceDocument(doc, sequenceId), revision: 0 });
    } catch {
      return null;
    }
    // A fresh store per opening; later parent edits do not reset an open session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sequenceId]);
  return (
    <Modal opened={sequenceId !== null && store !== null} onClose={onClose} title={t('compoundClip')} size="90%" data-testid="sequence-editor">
      {store && sequenceId && <SequenceEditorBody store={store} parent={doc} sequenceId={sequenceId} newId={newId} onClose={onClose} onCommit={onCommit} />}
    </Modal>
  );
}

function SequenceEditorBody({ store, parent, sequenceId, newId, onClose, onCommit }: { store: EditorStore; parent: EditTimeline; sequenceId: string; newId: () => string; onClose: () => void; onCommit: (op: EditOp, label: string) => void }) {
  const t = useT('editor.panels.sequenceEditorModal');
  const { showRefusal } = useRefusalToast();
  const sub = useEditorStore(store, (s) => s.doc);
  const ui = useEditorStore(store, (s) => s.ui);
  const dirty = useEditorStore(store, (s) => s.dirty);
  // Undo/redo availability follows the history cursor.
  useEditorStore(store, (s) => s.cursor);

  const run = (fn: () => void) => {
    try {
      fn();
    } catch (error) {
      showRefusal(error, store.getState().doc);
    }
  };

  const bladeHere = () =>
    run(() => {
      const ops: EditOp[] = [];
      for (const track of sub.tracks) for (const item of track.items) if (item.recordIn < ui.playheadFrame && ui.playheadFrame < item.recordOut && (ui.selection.length === 0 || ui.selection.includes(item.id))) ops.push({ type: 'blade', itemId: item.id, atFrame: ui.playheadFrame, newItemId: newId() });
      if (ops.length > 0) store.dispatchBatch(ops, 'Blade');
    });

  return (
    <Stack gap="xs">
      <Group justify="space-between">
        <Group gap="xs">
          <Text size="sm" fw={600}>
            {sub.name}
          </Text>
          <Badge size="xs" fz="xs" variant="light">{t('trackCount', { count: sub.tracks.length })}</Badge>
          <Text size="xs" c="dimmed" ff="monospace">
            {framesToTimecode(ui.playheadFrame, sub.settings.rate)}
          </Text>
        </Group>
        <Group gap={4}>
          <Button size="compact-xs" fz="xs" variant="subtle" disabled={!store.canUndo()} onClick={() => store.undo()} data-testid="sequence-undo">
            {t('undo')}
          </Button>
          <Button size="compact-xs" fz="xs" variant="subtle" disabled={!store.canRedo()} onClick={() => store.redo()}>
            {t('redo')}
          </Button>
          <Button size="compact-xs" fz="xs" variant="light" onClick={bladeHere} data-testid="sequence-blade">
            {t('cutAtPlayhead')}
          </Button>
          <Button size="compact-xs" fz="xs" variant="light" color="red" disabled={ui.selection.length === 0} onClick={() => run(() => { store.dispatchBatch(ui.selection.map((itemId) => ({ type: 'lift', itemId })), 'Lift'); store.setUi({ selection: [] }); })} data-testid="sequence-lift">
            {t('liftSelection')}
          </Button>
        </Group>
      </Group>
      <Box style={{ border: '1px solid var(--mantine-color-default-border)' }}>
        <TimelineCanvas
          doc={sub}
          playheadFrame={ui.playheadFrame}
          selection={ui.selection}
          inFrame={null}
          outFrame={null}
          pixelsPerFrame={ui.pixelsPerFrame}
          scrollFrame={ui.scrollFrame}
          tool={ui.tool}
          trackFlags={flags}
          parityOf={() => 'faithful'}
          rationaleFrames={[]}
          snap={(f) => f}
          onSeek={(f) => store.setUi({ playheadFrame: Math.max(0, f) })}
          onSelect={(id) => store.setUi({ selection: id ? [id] : [] })}
          onMarquee={() => undefined}
          onBlade={(itemId, atFrame) => run(() => store.dispatch({ type: 'blade', itemId, atFrame, newItemId: newId() }))}
          onMoveItem={(itemId, toTrackId, toRecordIn) => run(() => store.dispatch({ type: 'move', itemId, toTrackId, toRecordIn }))}
          onTrimItem={(itemId, edge, deltaFrames) => run(() => store.dispatch({ type: 'trim', itemId, edge, mode: 'ripple', deltaFrames }))}
          onZoom={(pixelsPerFrame) => store.setUi({ pixelsPerFrame })}
          onScroll={(scrollFrame) => store.setUi({ scrollFrame })}
          height={220}
        />
      </Box>
      <Group justify="flex-end" gap="xs">
        <Button size="xs" fz="xs" variant="subtle" onClick={onClose}>
          {dirty ? t('discardChanges') : t('close')}
        </Button>
        <Button
          size="xs" fz="xs"
          disabled={!dirty}
          onClick={() =>
            run(() => {
              onCommit(compileCommitSequence(parent, sequenceId, sub), t('editCompound', { name: sub.name }));
              onClose();
            })
          }
          data-testid="sequence-save"
        >
          {t('saveToCompound')}
        </Button>
      </Group>
    </Stack>
  );
}
