'use client';

import { onReviewRequest } from '@/lib/editing/background/job-marks';
import React, { useEffect, useState } from 'react';
import { Button, Group, Popover, Stack, Text } from '@mantine/core';
import { MAX_REGION_EFFECTS } from '@/lib/editing/api/objects';
import { rebaseAnchor, type RebaseReason } from '@/lib/editing/background/anchor';
import { canApplyFor } from '@/lib/editing/background/edit-guard';
import { resolveObjectOnServer, settleLanded } from '@/lib/editing/background/object-effect';
import { applyWhatFits, isProposalEntry, requestProposalReview } from '@/lib/editing/background/proposal';
import { classifyProposalEntry } from '@/lib/editing/background/proposal-review';
import { isAudioCleanupEntry, isCleanupReady, settleCleanup } from '@/lib/editing/background/audio-cleanup';
import { useBackgroundResults, type BackgroundEntry } from '@/lib/editing/background/results-store';
import { createEditTimelineVersion } from '@/lib/editing/sync/api';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';
import { useT } from '@/i18n';

export interface PendingResultsProps {
  store: EditorStore;
  projectId: string;
  /** The compact (phone) header gives the chip a touch-sized hit area. */
  compact?: boolean;
}

/** The header chip "1 result needs your OK" and the list it opens: apply to what is left, or discard. */
export function PendingResults({ store, projectId, compact = false }: PendingResultsProps) {
  const t = useT('editor-background');
  const { entries, store: results } = useBackgroundResults();
  const [open, setOpen] = useState(false);
  // Live: undoing a delete (or any edit) changes what a waiting result can still be applied to.
  const doc = useEditorStore(store, (s) => s.doc);
  const [fitsError, setFitsError] = useState<string | null>(null);
  // A "Ready to review" chip on a clip opens this list.
  useEffect(() => onReviewRequest(() => setOpen(true)), []);
  const pending = entries.filter((e) => e.state === 'needsReview');
  if (!results || pending.length === 0) return null;

  const reasonText = (reason: RebaseReason): string => {
    switch (reason) {
      case 'targetGone':
        return t('reason.targetGone');
      case 'outsideCoverage':
        return t('reason.outsideCoverage');
      case 'assetChanged':
        return t('reason.assetChanged');
      case 'limitReached':
        return t('reason.limitReached', { max: MAX_REGION_EFFECTS });
    }
  };
  const toolName = (e: BackgroundEntry): string => (e.tool === 'objectEffect' ? t('tool.objectEffect') : isAudioCleanupEntry(e) ? t('tool.audioCleanup') : isProposalEntry(e) ? t('tool.proposal') : t('tool.other'));
  const tell = (e: BackgroundEntry, action: 'applied' | 'discarded') =>
    isAudioCleanupEntry(e) ? undefined : void resolveObjectOnServer(projectId, e.anchor.assetKey, e.requestId, action, store.getState().revision).catch(() => undefined);

  const applyFits = async (entry: BackgroundEntry) => {
    setFitsError(null);
    const outcome = await applyWhatFits({ results, editor: store, entry, label: t('labelApplyFits'), beforeApply: () => createEditTimelineVersion(projectId, store.getState().timelineId, { cause: 'beforeProposal' }) });
    if (outcome === 'readOnly') setFitsError(t('blockedReadOnly'));
    else if (outcome === 'refused') setFitsError(t('blockedRefused', { message: '' }));
    else if (outcome === 'nothing') setFitsError(t('applyNothing'));
    else if (outcome === 'held') setOpen(false);
  };
  const openReview = (entry: BackgroundEntry) => {
    setOpen(false);
    requestProposalReview(entry.id);
  };

  const applyLeft = (entry: BackgroundEntry) =>
    isAudioCleanupEntry(entry) ? settleCleanup({ results, editor: store, entry, autoApply: false }) : settleLanded({ results, editor: store, entry, label: t('labelApplyLeft'), autoApply: false, onApplied: (e) => tell(e, 'applied') });
  const discard = (entry: BackgroundEntry) => {
    results.resolve(entry.id, 'discarded');
    tell(entry, 'discarded');
  };

  return (
    <Popover opened={open} onChange={setOpen} position="bottom-end" withArrow shadow="md" withinPortal>
      <Popover.Target>
        <Button size={compact ? 'compact-sm' : 'compact-xs'} fz="xs" variant="light" color="orange" miw="max-content" style={compact ? { minHeight: 44 } : { flexShrink: 0 }} aria-label={t('chipAria')} onClick={() => setOpen((v) => !v)} data-testid="pending-results-chip">
          {t('chip', { count: pending.length })}
        </Button>
      </Popover.Target>
      <Popover.Dropdown data-testid="pending-results-list">
        <Stack gap="sm" maw={340}>
          <Text size="sm" fw={600}>
            {t('listTitle')}
          </Text>
          {pending.map((entry) => {
            if (isProposalEntry(entry)) {
              const { kind, fit } = classifyProposalEntry(doc, entry);
              return (
                <Stack key={entry.id} gap={4} data-testid={`pending-result-${entry.id}`}>
                  <Text size="sm" fw={600}>
                    {kind === 'ready' ? t('proposalReady') : kind === 'someStale' ? t('proposalSomeStale') : t('proposalAllStale')}
                  </Text>
                  {kind !== 'ready' && fit !== null && fit.changed.length > 0 && (
                    <Text size="xs" data-testid="pending-reason-changed">
                      {t('reason.partChanged')}
                    </Text>
                  )}
                  {fitsError && (
                    <Text size="xs" c="red" role="alert">
                      {fitsError}
                    </Text>
                  )}
                  <Group gap="xs">
                    {kind === 'someStale' && (
                      <Button size="compact-sm" fz="xs" style={compact ? { minHeight: 44 } : undefined} disabled={store.getState().readOnly} onClick={() => void applyFits(entry)} data-testid={`pending-apply-fits-${entry.id}`}>
                        {t('applyFits')}
                      </Button>
                    )}
                    {kind !== 'allStale' && (
                      <Button size="compact-sm" fz="xs" variant={kind === 'ready' ? 'filled' : 'light'} style={compact ? { minHeight: 44 } : undefined} onClick={() => openReview(entry)} data-testid={`pending-review-${entry.id}`}>
                        {t('review')}
                      </Button>
                    )}
                    <Button size="compact-sm" fz="xs" variant="subtle" style={compact ? { minHeight: 44 } : undefined} onClick={() => results.resolve(entry.id, 'discarded')} data-testid={`pending-discard-${entry.id}`}>
                      {t('discard')}
                    </Button>
                  </Group>
                </Stack>
              );
            }
            const review = entry.review;
            const outcome = rebaseAnchor(doc, entry.anchor, canApplyFor(entry));
            const applicable = outcome.kind === 'apply' ? outcome.clipIds : outcome.applicable;
            const reasons: RebaseReason[] = outcome.kind === 'apply' ? [] : outcome.reasons;
            return (
              <Stack key={entry.id} gap={4} data-testid={`pending-result-${entry.id}`}>
                <Text size="sm" fw={600}>
                  {isCleanupReady(entry) ? t('audioCleanupReady') : toolName(entry)}
                </Text>
                {reasons.map((reason) => (
                  <Text key={reason} size="xs" data-testid={`pending-reason-${reason}`}>
                    {reasonText(reason)}
                  </Text>
                ))}
                {review?.blocked === 'readOnly' && <Text size="xs">{t('blockedReadOnly')}</Text>}
                {review?.blocked === 'refused' && <Text size="xs">{t('blockedRefused', { message: review.refusal ?? '' })}</Text>}
                {applicable.length === 0 && (
                  <Text size="xs" c="dimmed" data-testid="pending-apply-reason">
                    {t('applyNothing')}
                  </Text>
                )}
                <Group gap="xs">
                  <Button size="compact-sm" fz="xs" style={compact ? { minHeight: 44 } : undefined} disabled={applicable.length === 0 || store.getState().readOnly} onClick={() => applyLeft(entry)} data-testid={`pending-apply-${entry.id}`}>
                    {isCleanupReady(entry) ? t('applyReady') : t('applyLeft')}
                  </Button>
                  <Button size="compact-sm" fz="xs" variant="subtle" style={compact ? { minHeight: 44 } : undefined} onClick={() => discard(entry)} data-testid={`pending-discard-${entry.id}`}>
                    {t('discard')}
                  </Button>
                </Group>
              </Stack>
            );
          })}
          <Text size="xs" c="dimmed">
            {t('listHint')}
          </Text>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}
