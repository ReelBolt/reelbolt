'use client';

import React, { useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { Alert, Anchor, Button, Checkbox, Group, NumberInput, SegmentedControl, Select, Stack, Text, TextInput } from '@mantine/core';
import { BILLING_SETTINGS_PATH } from '@/components/styles/style-options';
import { useCreditAmount } from '@/components/billing/useCreditAmount';
import { useFormat, useT } from '@/i18n';
import {
  DEFAULT_SELECTION,
  DENOISE_STRENGTHS,
  ENHANCE_CHOICES,
  EXTEND_ASPECTS,
  EXTEND_MARGIN_MAX,
  EXTEND_PROMPT_MAX,
  extendAmountOf,
  extendMargins,
  extendMarginsEmpty,
  extendSidesOn,
  EXTEND_SIDES,
  FRAME_MULTIPLIERS,
  HAS_AI,
  HOSTED_ONLY,
  multiplierGate,
  failureKey,
  progressPercent,
  requestOf,
  selectionOf,
  SHUTTERS,
  UPSCALE_OPTIONS,
  upscaleGate,
  withEngine,
  type EnhanceChoice,
  type EnhanceRequest,
  type EnhanceEngine,
  type EnhanceEstimate,
  type EnhanceSelection,
  type ExtendAspect,
  type ExtendMode,
} from '@/lib/editing/enhance/types';
import { EnhanceConsentDialog, EnhanceStatusChip } from './EnhanceParts';
import { defaultEnhanceJobApi, useEnhanceJob, type EnhanceJobApi, type EnhanceJobState } from './useEnhanceJob';


export function requestOfState(s: EnhanceJobState): EnhanceRequest | null {
  switch (s.kind) {
    case 'idle': return null;
    case 'done': case 'needs-ok': case 'cancelled': return s.anchor.request;
    case 'error': return s.request;
    default: return s.request;
  }
}

export type EnhanceResultState = Extract<EnhanceJobState, { kind: 'done' | 'needs-ok' }>;

export interface EnhanceRunnerProps {
  projectId: string;
  sourceMediaRef: string;
  readOnly: boolean;
  /** 44 px targets. */
  phone?: boolean;
  api?: EnhanceJobApi;
  /** The source's height in pixels when known: makes the x2 / x4 plan check exact before asking. */
  sourceHeight?: number | null;
  clipIdsUsing?: () => readonly string[];
  clipName?: string;
  /** The clip selected now, to name the clip a shown job belongs to when it is another one. */
  currentClipId?: string;
  /** What to show when a run finishes (compare + apply, or "added to your media"). */
  renderResult: (state: EnhanceResultState, reset: () => void) => React.ReactNode;
}


/**
 * The picker and the run: four choices, Fast or AI, the options, the price line, Start, progress with Cancel,
 * the consent dialog and the refusals. What happens with the finished file is the caller's (`renderResult`).
 */
export function EnhanceRunner({ projectId, sourceMediaRef, readOnly, phone = false, api = defaultEnhanceJobApi, sourceHeight = null, clipIdsUsing, clipName, currentClipId, renderResult }: EnhanceRunnerProps) {
  const t = useT('editor-enhance');
  const nf = useFormat();
  const credits = useCreditAmount();
  const h = phone ? 44 : undefined;
  const segStyles = phone ? { root: { minHeight: 44 }, label: { minHeight: 40, display: 'flex', alignItems: 'center', justifyContent: 'center' } } : undefined;
  const job = useEnhanceJob({ projectId, sourceMediaRef, clipIdsUsing, clipName, readOnly }, api);
  const { state } = job;
  // A remounted section shows what the stored job is doing: its choice and options come back with it.
  const [restored] = useState<EnhanceSelection | null>(() => {
    const req = requestOfState(state);
    return req ? selectionOf(req) : null;
  });
  const [choice, setChoice] = useState<EnhanceChoice | null>(restored?.choice ?? null);
  const [sel, setSel] = useState<EnhanceSelection>(restored ?? DEFAULT_SELECTION);
  const [maxHeight, setMaxHeight] = useState<number | null>(null);

  const request = useMemo(() => {
    // Sides mode that adds nothing is refused by the server: show the field note, do not ask for a price.
    if (!choice || (choice === 'extend' && sel.extendMode === 'sides' && extendMarginsEmpty(sel))) return null;
    return requestOf({ ...sel, choice });
  }, [choice, sel]);
  const requestKey = request ? JSON.stringify(request) : '';
  const busy = state.kind === 'running' || state.kind === 'consent' || state.kind === 'needs-ok';

  const { estimate, reset } = job;
  const idle = state.kind === 'idle';
  const seenKey = useRef(restored && state.kind !== 'idle' ? requestKey : '');
  const kindRef = useRef(state.kind);
  useEffect(() => {
    kindRef.current = state.kind;
  });
  useEffect(() => {
    if (!request || readOnly || busy) return;
    if (kindRef.current !== 'idle' && requestKey === seenKey.current) return;
    seenKey.current = requestKey;
    const timer = window.setTimeout(() => void estimate(request), 300);
    return () => window.clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `requestKey` stands for `request`; `busy` only gates the first run
  }, [requestKey, readOnly, estimate, idle]);

  // The plan's cap arrives with an estimate; keep it for the other options (derived during render, no effect).
  const readyMax = state.kind === 'ready' ? state.estimate.maxOutputHeight : null;
  if (readyMax && readyMax !== maxHeight) setMaxHeight(readyMax);

  const pick = (c: string | null) => {
    if (!c || busy) return;
    if (state.kind === 'done') reset();
    setChoice(c as EnhanceChoice);
  };
  const patch = (p: Partial<EnhanceSelection>) => setSel((s) => ({ ...s, ...p }));
  const engine: EnhanceEngine = choice && HOSTED_ONLY[choice] ? 'Hosted' : choice && HAS_AI[choice] ? sel.engine : 'Local';

  const estimateLine = (e: EnhanceEstimate): string => {
    const cost = e.hosted && e.credits > 0 ? t('estimate.credits', { credits: Math.ceil(e.credits) }) : t('estimate.free');
    if (!e.outputWidth || !e.outputHeight) return t('estimate.lineShort', { cost });
    const secs = e.estimatedProcessingSeconds ?? e.seconds;
    const time = secs < 90 ? t('estimate.seconds', { seconds: Math.max(1, Math.round(secs)) }) : t('estimate.minutes', { minutes: Math.ceil(secs / 60) });
    return t('estimate.line', {
      cost,
      width: e.outputWidth,
      height: e.outputHeight,
      fps: e.outputFps ? nf.number(Math.round(e.outputFps * 10) / 10) : '-',
      size: nf.number(Math.max(1, Math.round((e.estimatedBytes ?? 0) / 1_000_000))),
      time,
    });
  };

  const refusalBlock = (s: Extract<EnhanceJobState, { kind: 'error' }>) => {
    const r = s.refusal;
    let text: string;
    if (s.failureCode) text = t(`failure.${failureKey(s.failureCode)}`);
    else if (!r) text = t('failure.failed');
    else
      switch (r.kind) {
        case 'credits': text = r.missing !== undefined ? t('refusal.credits', { missing: credits(r.missing) }) : t('refusal.creditsPlain'); break;
        case 'plan': text = r.feature === 'resolution' ? t('refusal.planResolution') : r.feature === 'video_enhance' ? t('refusal.planVideo') : t('refusal.plan'); break;
        case 'tooLong': text = r.maxSeconds !== undefined ? t('refusal.tooLongMax', { seconds: Math.round(r.maxSeconds) }) : t('refusal.tooLong'); break;
        case 'tooShort': text = r.minSeconds !== undefined ? t('refusal.tooShortMin', { seconds: Math.round(r.minSeconds) }) : t('refusal.tooShort'); break;
        case 'consent': text = t('refusal.consent'); break;
        case 'email': text = t('refusal.email'); break;
        case 'invalid': text = t('refusal.invalid'); break;
        case 'busy': text = t('refusal.busy'); break;
        case 'notMeasured': text = t('refusal.notMeasured'); break;
        case 'unavailable': text = t('refusal.unavailable'); break;
        case 'notVideo': text = t('refusal.notVideo'); break;
        case 'notFound': text = t('refusal.notFound'); break;
        default: text = t('refusal.other');
      }
    return (
      <Alert color="red" variant="light" role="alert" data-testid="enhance-refusal" data-kind={r?.kind ?? 'failed'}>
        <Stack gap={4}>
          <Text size="sm">{text}</Text>
          {r?.kind === 'credits' && (
            <Anchor component={Link} href={BILLING_SETTINGS_PATH} size="sm" data-testid="enhance-add-credits">
              {t('refusal.addCredits')}
            </Anchor>
          )}
          {r?.kind === 'plan' && (
            <Anchor component={Link} href={`${BILLING_SETTINGS_PATH}#plans`} size="sm" data-testid="enhance-see-plans">
              {t('refusal.seePlans')}
            </Anchor>
          )}
        </Stack>
      </Alert>
    );
  };

  const seg = <V extends string>(value: V, onChange: (v: V) => void, data: { value: V; label: string; disabled?: boolean }[], testId: string) => (
    <SegmentedControl size="xs" fullWidth value={value} onChange={(v) => onChange(v as V)} data={data} disabled={busy || readOnly} styles={segStyles} data-testid={testId} />
  );

  /** The one Extend amount, shown in percent and stored as a 0..0.5 fraction on every side that grows. */
  const amountInput = (value: number, onChange: (fraction: number) => void) => (
    <NumberInput
      label={t('extend.sides.amount')}
      size="sm"
      min={1}
      max={EXTEND_MARGIN_MAX * 100}
      suffix="%"
      allowDecimal={false}
      value={Math.round(value * 100)}
      onChange={(v) => {
        // An empty or sub-percent box keeps the last valid amount: nothing near zero is ever sent.
        const pct = typeof v === 'number' ? v : Number.NaN;
        if (!Number.isFinite(pct) || pct < 1) return;
        onChange(Math.min(EXTEND_MARGIN_MAX, pct / 100));
      }}
      disabled={busy || readOnly}
      styles={phone ? { input: { minHeight: 44 } } : undefined}
      data-testid="enhance-extend-amount"
    />
  );

  const choiceData = ENHANCE_CHOICES.map((c) => ({ value: c, label: t(`choice.${c}`) }));
  const showStart = !readOnly && (state.kind === 'ready' || state.kind === 'estimating');
  const progress = state.kind === 'running' ? progressPercent(state.progress) : null;

  return (
    <Stack gap="xs" data-testid="enhance-runner">
      <Select
        label={t('section.pick')}
        size="xs"
        data={choiceData}
        value={choice}
        onChange={pick}
        allowDeselect={false}
        disabled={busy || readOnly}
        comboboxProps={{ withinPortal: false }}
        styles={phone ? { input: { minHeight: 44 } } : undefined}
        data-testid="enhance-choice"
      />
      {choice && (
        <Text size="xs" c="dimmed" data-testid="enhance-choice-hint">
          {t(`choiceHint.${choice}`)}
        </Text>
      )}

      {choice && HAS_AI[choice] && (
        <Stack gap={2}>
          <Text size="xs">{t('section.how')}</Text>
          {HOSTED_ONLY[choice] ? (
            <Text size="xs" data-testid="enhance-engine">
              {t('extend.aiOnly')}
            </Text>
          ) : (
            seg<EnhanceEngine>(engine, (v) => setSel((s) => withEngine(s, v)), [{ value: 'Local', label: t('engine.Local') }, { value: 'Hosted', label: t('engine.Hosted') }], 'enhance-engine')
          )}
        </Stack>
      )}

      {choice === 'upscale' &&
        seg(
          sel.upscale,
          (v) => patch({ upscale: v }),
          UPSCALE_OPTIONS.map((o) => ({ value: o, label: t(`upscale.${o}`), disabled: upscaleGate(o, maxHeight, sourceHeight) !== null })),
          'enhance-upscale',
        )}
      {choice === 'upscale' && UPSCALE_OPTIONS.some((o) => upscaleGate(o, maxHeight, sourceHeight) !== null) && maxHeight !== null && (
        <Text size="xs" c="dimmed" data-testid="enhance-plan-reason">
          {t('section.planHeight', { height: maxHeight })}
        </Text>
      )}

      {choice === 'interpolate' &&
        seg(
          String(sel.multiplier),
          (v) => patch({ multiplier: Number(v) as 2 | 4 | 8 }),
          FRAME_MULTIPLIERS.map((m) => ({ value: String(m), label: t(`multiplier.${m}`), disabled: multiplierGate(m, engine) !== null })),
          'enhance-multiplier',
        )}
      {choice === 'interpolate' && engine === 'Hosted' && (
        <Text size="xs" c="dimmed">
          {t('section.aiLimit')}
        </Text>
      )}

      {choice === 'denoise' && (
        <Stack gap={6}>
          {seg(sel.strength, (v) => patch({ strength: v }), DENOISE_STRENGTHS.map((s) => ({ value: s, label: t(`strength.${s}`) })), 'enhance-strength')}
          <Checkbox size="sm" label={t('section.keepGrain')} checked={sel.keepGrain} disabled={busy || readOnly} onChange={(e) => patch({ keepGrain: e.currentTarget.checked })} styles={phone ? { body: { minHeight: 44, alignItems: 'center' } } : undefined} data-testid="enhance-keep-grain" />
        </Stack>
      )}

      {choice === 'motionBlur' && seg(sel.shutter, (v) => patch({ shutter: v }), SHUTTERS.map((s) => ({ value: s, label: t(`shutter.${s}`) })), 'enhance-shutter')}

      {choice === 'extend' && (
        <Stack gap={6}>
          {seg<ExtendMode>(
            sel.extendMode,
            (v) => patch({ extendMode: v }),
            [{ value: 'aspect', label: t('extend.mode.aspect') }, { value: 'sides', label: t('extend.mode.sides') }],
            'enhance-extend-mode',
          )}
          {sel.extendMode === 'aspect' ? (
            seg<ExtendAspect>(
              sel.extendAspect,
              (v) => patch({ extendAspect: v }),
              EXTEND_ASPECTS.map((a) => ({ value: a, label: t(`extend.aspect.${a}`) })),
              'enhance-extend-aspect',
            )
          ) : (
            <Stack gap={4}>
              <Text size="xs" c="dimmed">
                {t('extend.sides.hint')}
              </Text>
              <Group gap="sm" wrap="wrap">
                {EXTEND_SIDES.map((side) => (
                  <Checkbox
                    key={side}
                    size="sm"
                    label={t(`extend.sides.${side}`)}
                    checked={extendSidesOn(sel).includes(side)}
                    onChange={(e) => {
                      const on = e.currentTarget.checked;
                      const current = extendSidesOn(sel);
                      patch(extendMargins(on ? [...current, side] : current.filter((x) => x !== side), extendAmountOf(sel)));
                    }}
                    disabled={busy || readOnly}
                    styles={phone ? { root: { minHeight: 44, display: 'flex', alignItems: 'center' }, input: { minHeight: 24 } } : undefined}
                    data-testid={`enhance-extend-${side}`}
                  />
                ))}
              </Group>
              {amountInput(extendAmountOf(sel), (amount) => patch(extendMargins(extendSidesOn(sel), amount)))}
              {extendMarginsEmpty(sel) && (
                <Text size="xs" c="red" role="alert" data-testid="enhance-extend-margins-error">
                  {t('extend.sides.required')}
                </Text>
              )}
            </Stack>
          )}
          <TextInput
            label={t('extend.prompt.label')}
            description={`${t('extend.prompt.description', { max: EXTEND_PROMPT_MAX })} (${sel.prompt.length}/${EXTEND_PROMPT_MAX})`}
            size="sm"
            value={sel.prompt}
            maxLength={EXTEND_PROMPT_MAX}
            onChange={(e) => patch({ prompt: e.currentTarget.value })}
            disabled={busy || readOnly}
            styles={phone ? { input: { minHeight: 44 } } : undefined}
            data-testid="enhance-extend-prompt"
          />
        </Stack>
      )}

      {!choice && (
        <Text size="xs" c="dimmed">
          {t('section.noChoice')}
        </Text>
      )}
      {choice && state.kind === 'estimating' && (
        <Text size="xs" c="dimmed" data-testid="enhance-estimate">
          {t('estimate.checking')}
        </Text>
      )}
      {state.kind === 'ready' && (
        <Text size="xs" data-testid="enhance-estimate">
          {estimateLine(state.estimate)}
        </Text>
      )}

      {'anchor' in state && state.anchor.clipName && currentClipId && !state.anchor.clipIds.includes(currentClipId) && (
        <Text size="xs" c="dimmed" data-testid="enhance-for-clip">
          {t('forClip', { name: state.anchor.clipName })}
        </Text>
      )}

      {state.kind === 'error' && refusalBlock(state)}

      {showStart && (
        <Button mih={h} size="xs" fz="sm" disabled={state.kind !== 'ready'} onClick={job.start} data-testid="enhance-start">
          {t('start')}
        </Button>
      )}

      {state.kind === 'running' && (
        <Stack gap={6} data-testid="enhance-running">
          <Group gap="xs" wrap="nowrap">
            <EnhanceStatusChip kind="running" progress={state.progress} />
            <Text size="xs" data-testid="enhance-progress">
              {progress !== null ? t('progress', { percent: progress }) : t('running')}
            </Text>
          </Group>
          <Button mih={h} size="xs" fz="sm" variant="default" onClick={job.cancel} data-testid="enhance-cancel">
            {t('cancel')}
          </Button>
        </Stack>
      )}

      {state.kind === 'cancelled' && (
        <Text size="xs" data-testid="enhance-cancelled">
          {t('cancelled')}
        </Text>
      )}

      {state.kind === 'needs-ok' && <EnhanceStatusChip kind="needs-ok" />}
      {(state.kind === 'done' || state.kind === 'needs-ok') && renderResult(state, job.reset)}

      <EnhanceConsentDialog
        opened={state.kind === 'consent'}
        busy={state.kind === 'consent' && state.busy}
        failed={state.kind === 'consent' && state.failed}
        onCancel={job.declineConsent}
        onConfirm={() => void job.grantConsent()}
      />
    </Stack>
  );
}
