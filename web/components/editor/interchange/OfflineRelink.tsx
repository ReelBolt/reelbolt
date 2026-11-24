'use client';

import React, { useEffect, useState } from 'react';
import { Alert, Button, Group, Modal, Radio, Select, Stack, Text } from '@mantine/core';
import { candidateLabel, fileMediaRef } from '@/lib/editing/interchange/handoff-ui';
import type { ImportMediaCandidate } from '@/lib/editing/sync/api';
import { offlineMarkers, offlineMessage, type OfflineMarker } from '@/lib/editing/interchange/relink';
import type { EditTimeline } from '@/lib/editing/types';
import { useT } from '@/i18n';

/** What the last import said about one offline clip (keyed by lower-case clip name). */
export interface OfflineHint {
  reasonWords: string;
  candidates: readonly ImportMediaCandidate[];
}

export interface OfflineRelinkProps {
  doc: Pick<EditTimeline, 'markers'>;
  /** The project's files to pick from. */
  options: readonly { value: string; label: string }[];
  readOnly: boolean;
  onSeek: (frame: number) => void;
  /** Places the picked file at the marker; returns a plain sentence when it was refused. */
  onRelink: (markerId: string, mediaRef: string) => string | null;
  /** R12.3: why each clip is offline and, for a shared name, which files it could mean. */
  hints?: Readonly<Record<string, OfflineHint>>;
  /** Opens the relink dialog for this marker (the import result sheet's "Relink" button). */
  requestedMarkerId?: string | null;
  onRequestHandled?: () => void;
}

/**
 * Journey 3: after an import with missing media, say so in plain words and offer "Relink" per clip:
 * pick a project file and it goes where the red "Offline" marker is. The banner is derived from the
 * document's markers, so it disappears by itself once the last one is relinked (or undone back).
 */
export function OfflineRelink({ doc, options, readOnly, onSeek, onRelink, hints, requestedMarkerId, onRequestHandled }: OfflineRelinkProps) {
  const t = useT('editor-misc.relink');
  const items = offlineMarkers(doc);
  const [target, setTarget] = useState<OfflineMarker | null>(null);
  const [value, setValue] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [candidate, setCandidate] = useState<string | null>(null);
  // A relink request from the import sheet opens the dialog once (state adjusted during render, not in an effect).
  const [handledRequest, setHandledRequest] = useState<string | null>(null);
  if (requestedMarkerId && requestedMarkerId !== handledRequest) {
    setHandledRequest(requestedMarkerId);
    const found = items.find((m) => m.markerId === requestedMarkerId);
    if (found && !readOnly) setTarget(found);
  }
  if (!requestedMarkerId && handledRequest) setHandledRequest(null);
  useEffect(() => {
    if (requestedMarkerId) onRequestHandled?.();
  }, [requestedMarkerId, onRequestHandled]);
  if (items.length === 0 && !target) return null;
  const close = () => {
    setTarget(null);
    setValue(null);
    setProblem(null);
    setCandidate(null);
  };
  const targetHint = target ? hints?.[target.name.toLowerCase()] : undefined;
  const targetCandidates = targetHint?.candidates ?? [];
  const picked = targetCandidates.length > 0 ? (candidate ? fileMediaRef(candidate) : value) : value;
  return (
    <>
      {items.length > 0 && (
        <Alert color="red" variant="light" py={6} data-testid="offline-banner" title={t('bannerTitle')}>
          <Stack gap={4}>
            <Text size="sm" data-testid="offline-message">{offlineMessage(items)}</Text>
            {items.map((item) => (
              <Group key={item.markerId} gap="xs" wrap="nowrap" data-testid={`offline-row-${item.name}`}>
                <Stack gap={0} style={{ flex: 1 }}>
                  <Text size="sm">{t('offlineItem', { name: item.name })}</Text>
                  {hints?.[item.name.toLowerCase()] && (
                    <Text size="xs" c="dimmed" data-testid={`offline-reason-${item.name}`}>{hints[item.name.toLowerCase()].reasonWords}</Text>
                  )}
                </Stack>
                <Button size="xs" fz="xs" variant="default" mih={44} onClick={() => onSeek(item.frame)} data-testid={`offline-show-${item.name}`}>{t('show')}</Button>
                <Button size="xs" fz="xs" mih={44} disabled={readOnly} onClick={() => setTarget(item)} data-testid={`offline-relink-${item.name}`}>{t('relink')}</Button>
              </Group>
            ))}
          </Stack>
        </Alert>
      )}
      <Modal opened={target !== null} onClose={close} title={target ? t('relinkNamed', { name: target.name }) : t('relink')} centered data-testid="offline-modal">
        <Text size="sm" mb="sm">{t('intro')}</Text>
        {targetCandidates.length > 0 && (
          <Radio.Group label={t('whichFile')} value={candidate} onChange={(v) => { setCandidate(v); setProblem(null); }} mb="sm" data-testid="offline-candidates">
            <Stack gap="xs" mt={4}>
              {targetCandidates.map((c) => (
                <Radio key={c.fileId} value={c.fileId} label={candidateLabel(c)} data-testid={`offline-candidate-${c.fileId}`} />
              ))}
            </Stack>
          </Radio.Group>
        )}
        <Select
          label={targetCandidates.length > 0 ? t('orAnother') : t('fileFromPool')}
          data={options as { value: string; label: string }[]}
          value={value}
          onChange={(v) => { setValue(v); setCandidate(null); setProblem(null); }}
          searchable
          nothingFoundMessage={t('noFiles')}
          comboboxProps={{ withinPortal: true }}
          data-testid="offline-source"
        />
        {problem && <Text size="sm" c="red" mt="xs" role="alert" data-testid="offline-problem">{problem}</Text>}
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={close}>{t('cancel')}</Button>
          <Button
            disabled={!picked || !target}
            onClick={() => {
              if (!target || !picked) return;
              const refused = onRelink(target.markerId, picked);
              if (refused) setProblem(refused);
              else close();
            }}
            data-testid="offline-confirm"
          >
            {t('relink')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}
