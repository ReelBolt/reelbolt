'use client';

import React, { useEffect, useId, useMemo, useState } from 'react';
import Link from 'next/link';
import { Alert, Anchor, Button, Group, List, Modal, NumberInput, SegmentedControl, Select, Stack, Text, Textarea } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { mutate as globalMutate } from 'swr';
import { MediaConsentDialog } from '@/components/avatars/MediaConsentDialog';
import { SubjectPicker } from '@/components/avatars/SubjectPicker';
import { BILLING_SETTINGS_PATH } from '@/components/styles/style-options';
import { useFormat, useT } from '@/i18n';
import { avatarMediaConsentPath } from '@/lib/api/avatars';
import { audioFiles } from '@/lib/avatars/step-picker';
import { hasUnusableChosen, sendableSubjectIds, subjectNames, talkingSubject } from '@/lib/avatars/subjects';
import {
  SUBJECT_CLIP_DEFAULT_SECONDS,
  SUBJECT_CLIP_MAX_SECONDS,
  SUBJECT_CLIP_MAX_TAKES,
  SUBJECT_CLIP_MIN_SECONDS,
  SUBJECT_CLIP_PERSON_OFF_CODE,
  SUBJECT_CLIP_PROMPT_MAX,
  SUBJECT_CLIP_RATIOS,
  buildSubjectClipRequest,
  describeSubjectClipRefusal,
  nearestSubjectClipRatio,
  resolutionsForModel,
  subjectClipBlocker,
  type SubjectClipEstimate,
  type SubjectClipForm,
  type SubjectClipRefusal,
} from '@/lib/editing/subjects/subject-clip-api';
import { useAuth } from '@/lib/hooks/use-auth';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useSelectableProviders } from '@/lib/hooks/use-inference-providers';
import type { ProjectAvatar } from '@/lib/types/avatars';
import { selectableLabel } from '@/lib/types/inference-provider';
import { EarlyPreviewLabel } from '../shell/EarlyPreviewLabel';
import { getEnhanceHost } from '../enhance/enhance-host';
import { defaultSubjectClipApi, useSubjectClipJobs, type SubjectClipApi } from './useSubjectClipJob';

const ESTIMATE_DEBOUNCE_MS = 500;

export interface SubjectClipDialogProps {
  projectId: string;
  opened: boolean;
  onClose: () => void;
  /** The project's people and objects (`useProjectAvatars`). */
  avatars: readonly ProjectAvatar[] | null | undefined;
  /** Ticked when the dialog opens ("Generate clip" pressed on one avatar's row). */
  initialSubjectIds?: readonly string[];
  /** The timeline's picture size, for the starting shape. Absent: the open timeline's own, when there is one. */
  canvas?: { width: number; height: number } | null;
  /** Touch screen: 44 px controls, and the dialog fills the screen. */
  touch?: boolean;
  api?: SubjectClipApi;
}

/** "Generate clip": mounted only while open, so every opening starts from a clean form. */
export function SubjectClipDialog(props: SubjectClipDialogProps) {
  return props.opened ? <OpenDialog {...props} /> : null;
}

type EstimateState =
  | { kind: 'none' }
  | { kind: 'checking' }
  | { kind: 'ready'; estimate: SubjectClipEstimate }
  | { kind: 'refused'; refusal: SubjectClipRefusal };

/** An answer and the request it was asked for (the request as JSON). */
type EstimateAnswer = { key: string; state: Extract<EstimateState, { kind: 'ready' | 'refused' }> };

function timelineCanvas(): { width: number; height: number } | null {
  const host = getEnhanceHost();
  if (!host) return null;
  const { width, height } = host.getDoc().settings;
  return width > 0 && height > 0 ? { width, height } : null;
}

function OpenDialog({ projectId, onClose, avatars, initialSubjectIds = [], canvas, touch = false, api = defaultSubjectClipApi }: SubjectClipDialogProps) {
  const t = useT('editor-media.subjects.dialog');
  const tAv = useT('avatars');
  const f = useFormat();
  const { user } = useAuth();
  const role = user?.orgRole ?? null;
  // Mirrors the list's own gate: every role but Billing may read the models.
  const { data: providers } = useSelectableProviders('VideoGeneration', !!role && role !== 'Billing');
  const { data: files } = useProjectFiles(projectId);
  const { start } = useSubjectClipJobs(projectId, api);

  const [form, setForm] = useState<SubjectClipForm>(() => ({
    subjectIds: [...initialSubjectIds],
    prompt: '',
    capabilityId: null,
    durationSeconds: SUBJECT_CLIP_DEFAULT_SECONDS,
    ratio: nearestSubjectClipRatio(canvas === undefined ? timelineCanvas() : canvas),
    resolution: resolutionsForModel(null)[0],
    takes: 1,
    speechFileId: null,
  }));
  const [answer, setAnswer] = useState<EstimateAnswer | null>(null);
  const [starting, setStarting] = useState(false);
  const [refusal, setRefusal] = useState<SubjectClipRefusal | null>(null);
  const [consent, setConsent] = useState<{ busy: boolean; failed: boolean } | null>(null);
  const shapeLabelId = useId();
  const takesLabelId = useId();
  const patch = (update: Partial<SubjectClipForm>) => {
    setForm((cur) => ({ ...cur, ...update }));
    setRefusal(null);
  };

  const models = useMemo(() => (providers ?? []).filter((p) => p.capability === 'VideoGeneration'), [providers]);
  const chosenModel = models.find((p) => p.id === form.capabilityId) ?? null;
  const resolutions = resolutionsForModel(chosenModel?.model ?? null);
  const resolution = resolutions.includes(form.resolution) ? form.resolution : resolutions[0];

  const talker = talkingSubject(form.subjectIds, avatars);
  const speechFiles = useMemo(() => audioFiles(files), [files]);
  const speechFileId = talker && speechFiles.some((file) => file.id === form.speechFileId) ? form.speechFileId : null;

  const sendable = sendableSubjectIds(form.subjectIds, avatars);
  const staleSubject = hasUnusableChosen(form.subjectIds, avatars);
  const blocker = staleSubject ? null : subjectClipBlocker({ subjectIds: sendable, prompt: form.prompt });
  const request = blocker || staleSubject ? null : buildSubjectClipRequest({ ...form, subjectIds: sendable, resolution, speechFileId }, talker !== null);
  const requestKey = request ? JSON.stringify(request) : '';
  const [settledKey] = useDebouncedValue(requestKey, ESTIMATE_DEBOUNCE_MS);

  // The price, asked once the form stops changing. Only what the server answers is ever shown.
  useEffect(() => {
    if (!settledKey) return undefined;
    let alive = true;
    api.estimate(projectId, JSON.parse(settledKey)).then(
      (estimate) => {
        if (alive) setAnswer({ key: settledKey, state: { kind: 'ready', estimate } });
      },
      (error: unknown) => {
        if (alive) setAnswer({ key: settledKey, state: { kind: 'refused', refusal: describeSubjectClipRefusal(error) } });
      },
    );
    return () => {
      alive = false;
    };
  }, [settledKey, projectId, api]);
  // An answer belongs to the request it was asked for: while the form moves on, the price is being checked again.
  const shown: EstimateState = !requestKey ? { kind: 'none' } : answer && answer.key === requestKey ? answer.state : { kind: 'checking' };

  const run = async () => {
    if (!request) return;
    setStarting(true);
    setRefusal(null);
    try {
      await start(request, subjectNames(sendable, avatars));
      onClose();
    } catch (error) {
      const refused = describeSubjectClipRefusal(error);
      if (refused.kind === 'consent') setConsent({ busy: false, failed: false });
      else setRefusal(refused);
    } finally {
      setStarting(false);
    }
  };

  const grantConsent = async () => {
    setConsent({ busy: true, failed: false });
    try {
      await api.grantConsent(projectId);
      // The Avatars tab and the picture editor read the same answer.
      void globalMutate(avatarMediaConsentPath(projectId));
    } catch {
      setConsent({ busy: false, failed: true });
      return;
    }
    setConsent(null);
    await run();
  };

  /** The model a "cannot show pictures" refusal names, when the person may pick it here. */
  const siblingOf = (r: SubjectClipRefusal) => (r.kind === 'model' && r.sibling ? models.find((p) => p.model === r.sibling || p.name === r.sibling) ?? null : null);
  const ready = shown.kind === 'ready' ? shown.estimate : null;
  // A clip of a person where this workspace makes none yet is worded here (the server's sentence is English only).
  const unavailable = !ready || ready.unavailableCode === 'CONSENT_REQUIRED' ? null
    : ready.unavailableCode === SUBJECT_CLIP_PERSON_OFF_CODE ? t('refusal.personOff')
    : ready.unavailableReason || null;
  const tooDear = !!ready && ready.billingEnabled && !unavailable && ready.affordable === false;
  const control = touch ? 44 : undefined;
  const inputStyles = control ? { input: { minHeight: control, fontSize: 16 } } : undefined;
  const promptLength = form.prompt.trim().length;

  const refusalText = (r: SubjectClipRefusal): string => {
    switch (r.kind) {
      case 'credits': return r.message ?? t('refusal.credits');
      case 'plan': return t(`refusal.plan.${r.feature ?? 'other'}`);
      case 'busy': return r.retryAfterSeconds ? t('refusal.busySeconds', { seconds: Math.ceil(r.retryAfterSeconds) }) : t('refusal.busy');
      case 'model': return r.sibling ? t('refusal.model', { sibling: siblingOf(r)?.name ?? r.sibling }) : t('refusal.modelPlain');
      case 'avatar': {
        const why = r.reason && tAv.has(`reasons.${r.reason}`) ? tAv(`reasons.${r.reason}`) : null;
        return why ? t('refusal.avatarReason', { reason: why }) : t('refusal.avatar');
      }
      case 'personOff': return t('refusal.personOff');
      case 'email': return t('refusal.email');
      case 'invalid': return r.message ?? t('refusal.invalid');
      case 'off': return t('refusal.off');
      case 'consent': return t('refusal.consent');
      default: return t('refusal.other');
    }
  };

  const refusalBox = (r: SubjectClipRefusal, fromEstimate: boolean) => {
    const sibling = siblingOf(r);
    return (
    <Alert color="red" variant="light" role="alert" data-testid={fromEstimate ? 'subject-clip-estimate-refusal' : 'subject-clip-refusal'} data-kind={r.kind}>
      <Stack gap={4}>
        <Text size="sm">{refusalText(r)}</Text>
        {r.kind === 'invalid' && r.errors && r.errors.length > 0 && (
          <List size="sm" spacing={2}>
            {r.errors.map((line) => <List.Item key={line}>{line}</List.Item>)}
          </List>
        )}
        {r.kind === 'model' && sibling && (
          <Button variant="light" mih={44} style={{ alignSelf: 'flex-start' }} onClick={() => patch({ capabilityId: sibling.id })} data-testid="subject-clip-use-sibling">
            {t('refusal.useSibling', { sibling: sibling.name })}
          </Button>
        )}
        {r.kind === 'credits' && (
          <Anchor component={Link} href={BILLING_SETTINGS_PATH} size="sm" data-testid="subject-clip-add-credits">{t('refusal.addCredits')}</Anchor>
        )}
        {r.kind === 'plan' && (
          <Anchor component={Link} href={`${BILLING_SETTINGS_PATH}#plans`} size="sm" data-testid="subject-clip-see-plans">{t('refusal.seePlans')}</Anchor>
        )}
      </Stack>
    </Alert>
    );
  };

  return (
    <Modal
      opened
      onClose={onClose}
      centered
      size="lg"
      fullScreen={touch}
      title={
        <Group gap="xs" wrap="nowrap">
          <Text fw={600}>{t('title')}</Text>
          <EarlyPreviewLabel />
        </Group>
      }
      data-testid="subject-clip-dialog"
    >
      <Stack gap="sm">
        <SubjectPicker projectId={projectId} avatars={avatars} value={form.subjectIds} onChange={(ids) => patch({ subjectIds: ids })} label={t('subjects')} touch={touch} disabled={starting} />
        {staleSubject && (
          <Text size="sm" c="red" role="alert" data-testid="subject-clip-stale">{t('staleSubject')}</Text>
        )}

        <Textarea
          label={t('prompt')}
          placeholder={t('promptPlaceholder')}
          description={t('promptCount', { count: f.number(promptLength), max: f.number(SUBJECT_CLIP_PROMPT_MAX) })}
          value={form.prompt}
          onChange={(e) => patch({ prompt: e.currentTarget.value })}
          autosize
          minRows={3}
          maxRows={8}
          error={promptLength > SUBJECT_CLIP_PROMPT_MAX ? t('blocker.promptTooLong', { max: f.number(SUBJECT_CLIP_PROMPT_MAX) }) : undefined}
          styles={touch ? { input: { fontSize: 16 } } : undefined}
          data-testid="subject-clip-prompt"
        />

        {talker && (
          <Select
            label={t('speech')}
            description={speechFiles.length > 0 ? t('speechHint', { name: talker.name }) : t('speechNoFiles')}
            placeholder={t('speechNone')}
            data={speechFiles.map((file) => ({ value: file.id, label: file.originalFileName }))}
            value={speechFileId}
            onChange={(v) => patch({ speechFileId: v })}
            clearable
            searchable={!touch}
            comboboxProps={{ withinPortal: true }}
            styles={inputStyles}
            data-testid="subject-clip-speech"
          />
        )}

        <Select
          label={t('model')}
          placeholder={t('modelDefault')}
          data={models.map((p) => ({ value: p.id, label: selectableLabel(p) }))}
          value={form.capabilityId}
          onChange={(v) => patch({ capabilityId: v })}
          clearable
          searchable={!touch}
          comboboxProps={{ withinPortal: true }}
          styles={inputStyles}
          data-testid="subject-clip-model"
        />

        <Group grow align="flex-start" gap="sm" wrap="wrap">
          <NumberInput
            label={t('length')}
            min={SUBJECT_CLIP_MIN_SECONDS}
            max={SUBJECT_CLIP_MAX_SECONDS}
            clampBehavior="blur"
            allowDecimal={false}
            value={form.durationSeconds}
            onChange={(v) => patch({ durationSeconds: typeof v === 'number' ? v : SUBJECT_CLIP_DEFAULT_SECONDS })}
            styles={inputStyles}
            style={{ minWidth: 140 }}
            data-testid="subject-clip-length"
          />
          <Select
            label={t('resolution')}
            data={resolutions.map((r) => ({ value: r, label: r }))}
            value={resolution}
            onChange={(v) => v && patch({ resolution: v })}
            allowDeselect={false}
            comboboxProps={{ withinPortal: true }}
            styles={inputStyles}
            style={{ minWidth: 140 }}
            data-testid="subject-clip-resolution"
          />
        </Group>

        <div>
          <Text size="sm" fw={500} mb={4} id={shapeLabelId}>{t('shape')}</Text>
          <SegmentedControl
            fullWidth
            size={touch ? 'md' : 'sm'}
            aria-labelledby={shapeLabelId}
            data={SUBJECT_CLIP_RATIOS.map((r) => ({ value: r, label: r }))}
            value={form.ratio}
            onChange={(v) => patch({ ratio: v })}
            data-testid="subject-clip-shape"
          />
        </div>

        <div>
          <Text size="sm" fw={500} mb={4} id={takesLabelId}>{t('takes')}</Text>
          <SegmentedControl
            fullWidth
            size={touch ? 'md' : 'sm'}
            aria-labelledby={takesLabelId}
            data={Array.from({ length: SUBJECT_CLIP_MAX_TAKES }, (_, i) => ({ value: String(i + 1), label: t('takesOption', { count: i + 1 }) }))}
            value={String(form.takes)}
            onChange={(v) => patch({ takes: Number(v) })}
            data-testid="subject-clip-takes"
          />
        </div>

        <Stack gap={4} data-testid="subject-clip-estimate" aria-live="polite">
          {shown.kind === 'checking' && <Text size="sm" c="dimmed" data-testid="subject-clip-estimate-checking">{t('estimate.checking')}</Text>}
          {ready && unavailable && (
            <Alert color="yellow" variant="light" role="alert" data-testid="subject-clip-unavailable">
              <Text size="sm">{unavailable}</Text>
            </Alert>
          )}
          {ready && !unavailable && ready.billingEnabled && typeof ready.credits === 'number' && (
            <Text size="sm" data-testid="subject-clip-price">
              {typeof ready.creditsHigh === 'number' && ready.creditsHigh > ready.credits
                ? t('estimate.creditsUpTo', { credits: ready.credits, high: ready.creditsHigh })
                : t('estimate.credits', { credits: ready.credits })}
            </Text>
          )}
          {ready && !unavailable && ready.stillIncluded && <Text size="sm" c="dimmed" data-testid="subject-clip-still">{t('estimate.still')}</Text>}
          {tooDear && (
            <Text size="sm" c="red" role="alert" data-testid="subject-clip-not-enough">
              {t('estimate.notEnough')}{' '}
              <Anchor component={Link} href={BILLING_SETTINGS_PATH} size="sm">{t('refusal.addCredits')}</Anchor>
            </Text>
          )}
          {shown.kind === 'refused' && shown.refusal.kind === 'consent' && <Text size="sm" c="dimmed" data-testid="subject-clip-estimate-consent">{t('estimate.afterConsent')}</Text>}
          {shown.kind === 'refused' && shown.refusal.kind === 'other' && <Text size="sm" c="dimmed" data-testid="subject-clip-estimate-failed">{t('estimate.failed')}</Text>}
          {shown.kind === 'refused' && shown.refusal.kind !== 'consent' && shown.refusal.kind !== 'other' && !refusal && refusalBox(shown.refusal, true)}
        </Stack>

        {refusal && refusalBox(refusal, false)}

        <Group justify="flex-end" gap="xs" wrap="wrap">
          {blocker && <Text size="sm" c="dimmed" style={{ flex: '1 1 200px' }} data-testid="subject-clip-blocker">{t(`blocker.${blocker}`, { max: f.number(SUBJECT_CLIP_PROMPT_MAX) })}</Text>}
          <Button variant="default" mih={44} onClick={onClose} data-testid="subject-clip-cancel">{t('cancel')}</Button>
          <Button mih={44} loading={starting} disabled={!request || !!unavailable || tooDear} onClick={() => void run()} data-testid="subject-clip-start">{t('start')}</Button>
        </Group>
      </Stack>

      <MediaConsentDialog
        opened={consent !== null}
        busy={consent?.busy ?? false}
        failed={consent?.failed ?? false}
        onCancel={() => setConsent(null)}
        onConfirm={() => void grantConsent()}
      />
    </Modal>
  );
}
