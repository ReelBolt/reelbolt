'use client';

import React, { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { Button, Divider, Stack, Text } from '@mantine/core';
import type { EditOp } from '@/lib/editing/engine';
import { originalContentUrl } from '@/lib/editing/api/media';
import { clipSourceRange, clipsUsingSource, enhanceSubject, planApply, planApplyToClip, planRevert, type EnhanceRecord } from '@/lib/editing/enhance/plan';
import type { EditTimeline, MediaClip, Rational } from '@/lib/editing/types';
import { useT } from '@/i18n';
import { EnhanceCompare, EnhanceStatusChip } from './EnhanceParts';
import { EnhanceRunner, type EnhanceResultState } from './EnhanceRunner';
import { enhanceEntry, entryByOutput, enhanceVersion, projectEntries, subscribeEnhance } from './enhance-store';
import type { EnhanceJobApi } from './useEnhanceJob';

export interface EnhanceSectionProps {
  /** The single selected media clip. */
  clip: MediaClip;
  /** Null (no project in scope) renders nothing: there is no source to enhance. */
  projectId: string | null;
  readOnly: boolean;
  doc: EditTimeline;
  /** ONE undo step. */
  onBatch: (ops: EditOp[], label: string) => void;
  phone?: boolean;
  /** Hide the heading when the host (the phone sheet) draws its own. */
  hideTitle?: boolean;
  api?: EnhanceJobApi;
  sourceHeight?: number | null;
}

/**
 * Enhance a video clip: four choices, price, consent for AI, progress, compare, and Apply as one undoable batch on
 * every clip still playing the source. "Use original" puts them back. Nothing is ever applied without a click.
 */
export function EnhanceSection({ clip, projectId, readOnly, doc, onBatch, phone = false, hideTitle = false, api, sourceHeight }: EnhanceSectionProps) {
  const t = useT('editor-enhance');
  const subject = enhanceSubject(doc, clip.id);
  useSyncExternalStore(subscribeEnhance, enhanceVersion, enhanceVersion);
  // After Apply the clip plays the new file; the section keeps showing the job that made it (compare, Use original),
  // found by its output file so it is still there after selecting another clip and back.
  const applied = projectId && subject ? entryByOutput(projectId, subject.mediaRef) : null;
  const sourceRef = applied ? applied.sourceMediaRef : (subject?.mediaRef ?? '');
  const docRef = useRef(doc);
  useEffect(() => {
    docRef.current = doc;
  });
  if (!projectId || !subject) {
    return hideTitle || !projectId ? null : (
      <Text size="xs" c="dimmed" data-testid="enhance-needs-picture">
        {t('section.needsPicture')}
      </Text>
    );
  }
  return (
    <Stack gap="xs" data-testid="enhance-section">
      {!hideTitle && <Divider label={t('section.title')} labelPosition="left" />}
      <Text size="xs" c="dimmed">
        {t('section.hint')}
      </Text>
      <EnhanceRunner
        projectId={projectId}
        sourceMediaRef={sourceRef}
        readOnly={readOnly}
        phone={phone}
        api={api}
        sourceHeight={sourceHeight}
        clipIdsUsing={() => clipsUsingSource(docRef.current, sourceRef).map((c) => c.id)}
        clipName={clip.name}
        currentClipId={clip.id}
        renderResult={(state, reset) => <EnhanceResult state={state} reset={reset} projectId={projectId} clip={clip} docRef={docRef} rate={doc.settings.rate} readOnly={readOnly} phone={phone} onBatch={onBatch} />}
      />
      <OtherJobs projectId={projectId} currentRef={subject.mediaRef} clip={clip} doc={doc} docRef={docRef} readOnly={readOnly} phone={phone} onBatch={onBatch} />
    </Stack>
  );
}

/** Jobs of this project for other sources that still run or wait for an OK (e.g. their clip was deleted or switched). */
function OtherJobs({ projectId, currentRef, clip, doc, docRef, readOnly, phone, onBatch }: { projectId: string; currentRef: string; clip: MediaClip; doc: EditTimeline; docRef: React.RefObject<EditTimeline>; readOnly: boolean; phone: boolean; onBatch: (ops: EditOp[], label: string) => void }) {
  const t = useT('editor-enhance');
  useSyncExternalStore(subscribeEnhance, enhanceVersion, enhanceVersion);
  const currentKey = enhanceEntry(projectId, currentRef).key;
  const h = phone ? 44 : undefined;
  const rows = projectEntries(projectId).filter((e) => e.key !== currentKey);
  return (
    <>
      {rows.map((e) => {
        const s = e.state;
        const orphan = s.kind === 'done' && !e.applied && clipsUsingSource(doc, e.sourceMediaRef).length === 0;
        if (s.kind === 'running') {
          return (
            <Stack gap={4} key={e.key} data-testid="enhance-other-running">
              <EnhanceStatusChip kind="running" progress={s.progress} />
              <Text size="xs">{t('otherJob', { name: s.anchor.clipName || clip.name })}</Text>
              <Button mih={h} size="xs" fz="sm" variant="default" onClick={e.cancel}>{t('cancel')}</Button>
            </Stack>
          );
        }
        if ((s.kind !== 'needs-ok' || e.applied) && !orphan) return null;
        const done = s as Extract<typeof s, { kind: 'needs-ok' | 'done' }>;
        const outputFileId = done.job.outputFileId;
        if (!outputFileId) return null;
        return (
          <Stack gap={4} key={e.key} data-testid="enhance-other-needs-ok">
            <EnhanceStatusChip kind="needs-ok" />
            <Text size="xs">{t('otherJobReady', { name: done.anchor.clipName || clip.name })}</Text>
            <Button
              mih={h}
              size="xs"
              fz="sm"
              disabled={readOnly}
              data-testid="enhance-other-apply"
              onClick={() => {
                const plan = planApplyToClip(docRef.current, clip.id, outputFileId);
                if (plan) {
                  onBatch(plan.ops, t('applyLabel'));
                  // Keep the job (and what Apply changed): the panel then shows it with Use original.
                  e.setRecord(plan.record);
                }
              }}
            >
              {t('needsOk.applyToClip')}
            </Button>
            <Button mih={h} size="xs" fz="sm" variant="default" onClick={e.reset} data-testid="enhance-other-keep">{t('needsOk.keep')}</Button>
          </Stack>
        );
      })}
    </>
  );
}

interface ResultProps {
  state: EnhanceResultState;
  reset: () => void;
  projectId: string;
  clip: MediaClip;
  docRef: React.RefObject<EditTimeline>;
  rate: Rational;
  readOnly: boolean;
  phone: boolean;
  onBatch: (ops: EditOp[], label: string) => void;
}

function EnhanceResult({ state, reset, projectId, clip, docRef, rate, readOnly, phone, onBatch }: ResultProps) {
  const t = useT('editor-enhance');
  const h = phone ? 44 : undefined;
  const entry = enhanceEntry(projectId, state.anchor.sourceMediaRef);
  const record = entry.record;
  const [reverted, setReverted] = useState(false);
  const outputFileId = state.job.outputFileId;
  if (!outputFileId) return null;

  const apply = (plan: ReturnType<typeof planApply>) => {
    if (!plan || readOnly) return;
    onBatch(plan.ops, t('applyLabel'));
    entry.setRecord(plan.record);
    setReverted(false);
  };
  const revert = () => {
    if (!record || readOnly) return;
    const plan = planRevert(docRef.current, record);
    if (plan) onBatch(plan.ops, t('useOriginalLabel'));
    entry.setRecord(null);
    setReverted(true);
  };

  if (state.kind === 'needs-ok') {
    return (
      <Stack gap={6} data-testid="enhance-needs-ok">
        <Text size="sm" fw={600}>
          {t('needsOk.title')}
        </Text>
        <Text size="xs">{t('needsOk.body')}</Text>
        {record ? (
          <Button mih={h} size="xs" fz="sm" variant="default" disabled={readOnly} onClick={revert} data-testid="enhance-use-original">
            {t('useOriginal')}
          </Button>
        ) : (
          <>
            <Button mih={h} size="xs" fz="sm" disabled={readOnly} onClick={() => apply(planApplyToClip(docRef.current, clip.id, outputFileId))} data-testid="enhance-apply-clip">
              {t('needsOk.applyToClip')}
            </Button>
            <Button mih={h} size="xs" fz="sm" variant="default" onClick={reset} data-testid="enhance-keep">
              {t('needsOk.keep')}
            </Button>
          </>
        )}
      </Stack>
    );
  }

  const before = originalContentUrl(projectId, state.anchor.sourceMediaRef);
  const after = originalContentUrl(projectId, `file:${outputFileId}`);
  const range = clipSourceRange(clip, rate);
  return (
    <Stack gap="xs" data-testid="enhance-done">
      <Text size="xs">{t('done')}</Text>
      {before && after && <EnhanceCompare beforeSrc={before} afterSrc={after} startSec={range.startSec} lengthSec={range.lengthSec} />}
      {record ? (
        <Stack gap={4}>
          <Text size="xs" data-testid="enhance-applied">
            {t('applied')}
          </Text>
          <Button mih={h} size="xs" fz="sm" variant="default" disabled={readOnly} onClick={revert} data-testid="enhance-use-original">
            {t('useOriginal')}
          </Button>
        </Stack>
      ) : (
        <Stack gap={4}>
          {reverted && (
            <Text size="xs" data-testid="enhance-reverted">
              {t('reverted')}
            </Text>
          )}
          <Button mih={h} size="xs" fz="sm" disabled={readOnly} onClick={() => apply(planApply(docRef.current, state.anchor.sourceMediaRef, outputFileId))} data-testid="enhance-apply">
            {reverted ? t('applyAgain') : t('apply')}
          </Button>
        </Stack>
      )}
    </Stack>
  );
}
