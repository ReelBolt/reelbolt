'use client';

import React, { useCallback, useEffect, useState } from 'react';
import { Button, Group, Modal, ScrollArea, Stack, Text, TextInput } from '@mantine/core';
import { IconHistory, IconRefresh, IconRestore } from '@tabler/icons-react';
import { EditorEmpty } from '../ui/EditorEmpty';
import { EditorPanel } from '../ui/EditorPanel';
import {
  createEditTimelineVersion,
  getEditTimelineRevision,
  listEditTimelineVersions,
  nameEditTimelineVersion,
  removeEditTimelineVersionName,
  type EditTimelineVersion,
} from '@/lib/editing/sync';
import type { EditTimeline } from '@/lib/editing/types';
import { formatDate } from '@/lib/utils/format';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { causeLabelMsg, NAME_MAX_LENGTH, retentionMsg, sessionLabelMsg, groupVersions } from './versions';
import { msg, useFormat, useT } from '@/i18n';
import { EDITOR_MODAL_Z } from '@/components/editor/ui/z-index';

export interface RevisionHistoryPanelProps {
  projectId: string;
  timelineId: string;
  currentRevision: number;
  readOnly: boolean;
  /** The parent performs the restore (an ordinary PATCH through the store + sync client; it clears this session's undo history). */
  onRestore: (document: EditTimeline, revision: number) => void;
  /** Bump to refetch (after a save). */
  refreshKey?: number;
}

const TOUCH: React.CSSProperties = { minHeight: 44 };

/**
 * The Versions list: every restorable point of the timeline, grouped by editing session, newest first. Every row can be
 * restored; restoring asks first and keeps the current state as "Before you restored". A version can be named (kept
 * forever); an unnamed automatic checkpoint is deleted by the server after 30 days.
 */
export function RevisionHistoryPanel({ projectId, timelineId, readOnly, onRestore, refreshKey = 0 }: RevisionHistoryPanelProps) {
  const t = useT('editor.panels.revisionHistoryPanel');
  const f = useFormat();
  const [versions, setVersions] = useState<EditTimelineVersion[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [manualRefresh, setManualRefresh] = useState(0);
  const [restoring, setRestoring] = useState<number | null>(null);
  const [confirming, setConfirming] = useState<EditTimelineVersion | null>(null);
  // The inline name field: `new` for "Save a named version now", else the revision being named.
  const [naming, setNaming] = useState<'new' | number | null>(null);
  const [nameValue, setNameValue] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let cancelled = false;
    listEditTimelineVersions(projectId, timelineId)
      .then((result) => {
        if (cancelled) return;
        setVersions(result);
        setError(null);
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(humanizeErrorT(t, e, t('couldNotLoadThe')));
      })
      .finally(() => {
        if (!cancelled) setRefreshing(false);
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, timelineId, refreshKey, manualRefresh, t]);

  const load = useCallback(() => {
    setRefreshing(true);
    setManualRefresh((n) => n + 1);
  }, []);
  const loading = refreshing || (versions === null && error === null);

  const startNaming = (target: 'new' | number, initial = '') => {
    setNaming(target);
    setNameValue(initial);
  };

  const saveName = async () => {
    const name = nameValue.trim();
    if (!name || naming === null || busy) return;
    setBusy(true);
    setError(null);
    try {
      if (naming === 'new') await createEditTimelineVersion(projectId, timelineId, { name, cause: 'named' });
      else await nameEditTimelineVersion(projectId, timelineId, naming, name);
      setNaming(null);
      load();
    } catch (e) {
      setError(humanizeErrorT(t, e, t('theNameCouldNot')));
    } finally {
      setBusy(false);
    }
  };

  const removeName = async (revision: number) => {
    setError(null);
    try {
      await removeEditTimelineVersionName(projectId, timelineId, revision);
      load();
    } catch (e) {
      setError(humanizeErrorT(t, e, t('theNameCouldNot2')));
    }
  };

  const restore = async (version: EditTimelineVersion) => {
    setConfirming(null);
    setRestoring(version.revision);
    setError(null);
    try {
      const response = await getEditTimelineRevision(projectId, timelineId, version.revision);
      onRestore(response.document, version.revision);
    } catch (e) {
      setError(humanizeErrorT(t, e, t('thatVersionCouldNot')));
    } finally {
      setRestoring(null);
    }
  };

  const nameField = (
    <Group gap={6} wrap="nowrap" align="flex-end">
      <TextInput
        aria-label={t('versionName')}
        placeholder={t('nameThisVersion')}
        value={nameValue}
        maxLength={NAME_MAX_LENGTH}
        onChange={(e) => setNameValue(e.currentTarget.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') void saveName();
          if (e.key === 'Escape') setNaming(null);
        }}
        style={{ flex: 1 }}
        data-autofocus
        data-testid="version-name-input"
      />
      <Button size="sm" fz="xs" style={TOUCH} onClick={() => void saveName()} loading={busy} disabled={nameValue.trim() === ''} data-testid="version-name-save">
        {t('save')}
      </Button>
      <Button size="sm" fz="xs" variant="default" style={TOUCH} onClick={() => setNaming(null)}>
        {t('cancel')}
      </Button>
    </Group>
  );

  const sessions = groupVersions(versions ?? []);

  return (
    <EditorPanel
      title={t('versions')}
      testId="revision-history-panel"
      actions={<Button size="compact-sm" fz="xs" variant="subtle" leftSection={<IconRefresh size={12} />} onClick={load} loading={loading}>{t('refresh')}</Button>}
    >
      <Stack gap="xs">
      <Text size="sm" c="dimmed" data-testid="versions-rule">
        {t.msg(msg('editor-helpers-1.panels.versions.rule'))}
      </Text>
      {naming === 'new' ? (
        nameField
      ) : (
        <Button variant="light" style={TOUCH} disabled={readOnly} onClick={() => startNaming('new')} data-testid="version-save-named">
          {t('saveANamedVersion')}
        </Button>
      )}
      {error && (
        <Text size="sm" c="red" data-testid="revision-history-error">
          {error}
        </Text>
      )}
      {versions && versions.length === 0 && (
        <EditorEmpty compact icon={IconHistory} title={t('noVersionsYet')} hint={t('theyAppearAsYou')} testId="revision-history-empty" />
      )}
      <ScrollArea.Autosize mah={360}>
        <Stack gap="sm">
          {sessions.map((session) => (
            <Stack key={session.key} gap={4} data-testid="version-session">
              <Text size="xs" fw={600} c="dimmed" tt="uppercase">
                {t.msg(sessionLabelMsg(session, (ms) => f.date(new Date(ms), 'short')))}
              </Text>
              {session.versions.map((v) => (
                <Stack key={v.revision} gap={4} data-testid={`revision-row-${v.revision}`} data-cause={v.cause}>
                  <Group justify="space-between" wrap="nowrap" align="flex-start">
                    <Stack gap={0} style={{ minWidth: 0 }}>
                      <Text size="sm" fw={v.name ? 600 : 400} truncate data-testid={`version-title-${v.revision}`}>
                        {v.name ?? t.msg(causeLabelMsg(v))}
                      </Text>
                      <Text size="xs" c="dimmed" data-testid={`version-meta-${v.revision}`}>
                        {v.name ? `${t.msg(causeLabelMsg(v))} · ` : ''}
                        {formatDate(v.createdAt)}
                        {v.createdByName ? ` · ${v.actorKind === 'apiKey' ? `${t('apiKeyPrefix')} ` : ''}${v.createdByName}` : ''}
                      </Text>
                      <Text size="xs" c="dimmed" data-testid={`version-retention-${v.revision}`}>
                        {t.msg(retentionMsg(v))}
                      </Text>
                    </Stack>
                    <Button
                      size="compact-sm" fz="xs"
                      variant="light"
                      leftSection={<IconRestore size={12} />}
                      disabled={readOnly}
                      loading={restoring === v.revision}
                      onClick={() => setConfirming(v)}
                      style={{ flexShrink: 0 }}
                      data-testid={`revision-restore-${v.revision}`}
                    >
                      {t('restore')}
                    </Button>
                  </Group>
                  {naming === v.revision ? (
                    nameField
                  ) : (
                    <Group gap={4}>
                      <Button size="compact-xs" fz="xs" variant="subtle" disabled={readOnly} onClick={() => startNaming(v.revision, v.name ?? '')} data-testid={`version-name-${v.revision}`}>
                        {v.name ? t('rename') : t('nameThisVersion2')}
                      </Button>
                      {v.name && (
                        <Button size="compact-xs" fz="xs" variant="subtle" color="gray" disabled={readOnly} onClick={() => void removeName(v.revision)} data-testid={`version-unname-${v.revision}`}>
                          {t('removeTheName')}
                        </Button>
                      )}
                    </Group>
                  )}
                </Stack>
              ))}
            </Stack>
          ))}
        </Stack>
      </ScrollArea.Autosize>

      <Modal opened={confirming !== null} onClose={() => setConfirming(null)} title={t('restoreThisVersion')} radius="md" centered zIndex={EDITOR_MODAL_Z}>
        <Stack gap="md">
          <Text size="sm" data-testid="version-restore-text">
            {t('restore2')} {confirming?.name ? `“${confirming.name}”` : t.msg(causeLabelMsg(confirming ?? { cause: 'auto' })).toLowerCase()} {t('from')} {confirming ? formatDate(confirming.createdAt) : ''}{t('yourCurrentStateIs')}
          </Text>
          <Group justify="flex-end" gap="xs">
            <Button variant="default" style={TOUCH} onClick={() => setConfirming(null)} data-testid={`revision-restore-cancel-${confirming?.revision ?? 0}`}>
              {t('keepCurrent')}
            </Button>
            <Button color="orange" style={TOUCH} onClick={() => confirming && void restore(confirming)} data-testid={`revision-restore-confirm-${confirming?.revision ?? 0}`}>
              {t('restore3')}
            </Button>
          </Group>
        </Stack>
      </Modal>
      </Stack>
    </EditorPanel>
  );
}
