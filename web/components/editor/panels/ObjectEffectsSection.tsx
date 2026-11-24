'use client';

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Button, Group, NumberInput, Select, Stack, Switch, Text, TextInput } from '@mantine/core';
import type { MediaClip, RegionClipEffect, RegionEffectType } from '@/lib/editing/types';
import type { PropertyKey, PropertyValue } from '@/lib/editing/engine';
import { mediaRefOf } from '@/lib/editing/preview';
import { anchorForClips, type ResultAnchor } from '@/lib/editing/background/anchor';
import { getActiveDoc, useBackgroundResults } from '@/lib/editing/background/results-store';
import {
  classifyObjectError,
  DEFAULT_OBJECT_STRENGTH,
  estimateObject,
  grantObjectConsent,
  MAX_REGION_EFFECTS,
  OBJECT_NOT_FOUND_AT_POINT,
  PROMPT_MAX,
  promptsByAnalysis,
  REGION_EFFECTS,
  startObject,
  useObjectConsent,
  useObjectList,
  withdrawObjectConsent,
  type ObjectErrorKind,
  type ObjectEstimate,
} from '@/lib/editing/api/objects';
import { useJobFor } from '@/lib/activity/ActivityProvider';
import { InlineJobStatus } from '@/components/activity/InlineJobStatus';
import { JobStatusBadge } from '@/components/activity/JobStatusBadge';
import { localJob } from '@/lib/editing/background/activity-job';
import { armFollowBox, disarmFollowBox, useFollowHost } from '@/lib/editing/follow/follow-host';
import { seedBody, type ObjectSeed } from '@/lib/editing/selection/seed';
import { useCoarsePointer } from '../thumbnail/useCoarsePointer';
import { LiveSlider } from '../shell/LiveSlider';
import { ObjectConsentDialog } from './ObjectConsentDialog';
import { useFormat, useT } from '@/i18n';

export interface ObjectEffectsSectionProps {
  /** The single selected media clip. */
  clip: MediaClip;
  /** Null (no project in scope) renders nothing: there is no source to search. */
  projectId: string | null;
  readOnly: boolean;
  /** One `setProperty` per change, so every add / toggle / strength / remove is one undo step. */
  onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => void;
}

type PriceState = { phase: 'idle' } | { phase: 'loading' } | { phase: 'ready'; estimate: ObjectEstimate; forPrompt: string } | { phase: 'error'; kind: ObjectErrorKind; message: string | null };


/**
 * Magic blur: name an object, pick an effect, see the price, agree once to the outside service, run, and the
 * finished search is applied to the clip as ONE undoable `regionEffects` write. Existing effects are listed
 * below with a switch, a strength and a remove (each one undo step). The preview does not draw these: the
 * render does, and the viewer's "render required" badge already says so.
 */
export function ObjectEffectsSection({ clip, projectId, readOnly, onSetProperty }: ObjectEffectsSectionProps) {
  const t = useT('editor.panels.objectEffects');
  const tb = useT('editor-background');
  const nf = useFormat();
  const coarse = useCoarsePointer();
  const tap = coarse ? { minHeight: 44, minWidth: 44 } : undefined;
  const inputStyles = coarse ? { input: { minHeight: 44 } } : undefined;
  const switchStyles = coarse ? { body: { minHeight: 44, alignItems: 'center' } } : undefined;
  const sliderBox = coarse ? { minHeight: 44 } : undefined;
  const mediaRef = clip.assetRef ? mediaRefOf(clip.assetRef) : null;
  const sourceId = projectId && mediaRef ? mediaRef : null;

  const [prompt, setPrompt] = useState('');
  // The object the user pointed at on the picture; it names the object instead of the words.
  const [seed, setSeed] = useState<ObjectSeed | null>(null);
  const host = useFollowHost();
  const picking = host.armed?.clipId === clip.id;
  useEffect(() => () => disarmFollowBox(), []);
  const [effect, setEffect] = useState<RegionEffectType>('blur');
  const [strength, setStrength] = useState(DEFAULT_OBJECT_STRENGTH);
  const [invert, setInvert] = useState(false);
  const [featherPx, setFeatherPx] = useState(0);
  const [price, setPrice] = useState<PriceState>({ phase: 'idle' });
  const [consentOverride, setConsentOverride] = useState<boolean | null>(null);
  const [dialog, setDialog] = useState(false);
  const [consentBusy, setConsentBusy] = useState(false);
  const [consentError, setConsentError] = useState<string | null>(null);
  const [startError, setStartError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  // The launch is registered with the editor (not owned here): the workspace's watcher applies the result to the
  // CURRENT document, so deselecting, deleting or splitting this clip never drops a paid result.
  const { entries: bgEntries, store: results } = useBackgroundResults();
  const mine = useMemo(() => {
    for (let i = bgEntries.length - 1; i >= 0; i--) {
      const e = bgEntries[i];
      if (e.tool === 'objectEffect' && e.anchor.clipIds.includes(clip.id) && e.anchor.assetKey === mediaRef && e.state !== 'applied' && e.state !== 'discarded') return e;
    }
    return null;
  }, [bgEntries, clip.id, mediaRef]);

  const serverJob = useJobFor((j) => j.origin === 'server' && !!mine?.executionId && j.executionId === mine.executionId);

  const writeRegionEffects = useCallback(
    (itemId: string, next: RegionClipEffect[], label: string) => onSetProperty(itemId, 'regionEffects', next.length === 0 ? null : next, label),
    [onSetProperty],
  );

  const trimmed = prompt.trim();
  const body = seed ? seedBody(seed) : null;
  const seedKey = body ? JSON.stringify(body) : '';
  const promptOk = body !== null || (trimmed.length >= 1 && trimmed.length <= PROMPT_MAX);
  // What the price and the run are for: the pick wins over the words.
  const subjectKey = body ? seedKey : trimmed;
  const subjectName = body ? t('pickedName') : trimmed;
  const startPicking = () => {
    if (picking) {
      disarmFollowBox();
      return;
    }
    armFollowBox({
      clipId: clip.id,
      onSeed: (picked) => {
        setSeed(picked);
        setPrompt('');
        setStartError(null);
      },
    });
  };
  const effects = clip.regionEffects ?? [];
  const atLimit = effects.length >= MAX_REGION_EFFECTS;

  const list = useObjectList(sourceId ? projectId : null, sourceId);
  const names = useMemo(() => promptsByAnalysis(list.data), [list.data]);
  const consentQuery = useObjectConsent(sourceId ? projectId : null);

  // The price, before anything runs: refreshed as the words settle.
  const refreshPrice = useCallback(
    (cancelled: () => boolean) => {
      if (!sourceId || !projectId || !promptOk) {
        setPrice({ phase: 'idle' });
        return;
      }
      setPrice({ phase: 'loading' });
      estimateObject(projectId, sourceId, body ? { seed: body } : trimmed).then(
        (estimate) => !cancelled() && setPrice({ phase: 'ready', estimate, forPrompt: subjectKey }),
        (error: unknown) => !cancelled() && setPrice({ phase: 'error', ...classifyObjectError(error) }),
      );
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `body` is derived from `seedKey`
    [projectId, sourceId, promptOk, trimmed, subjectKey],
  );
  const [priceEpoch, setPriceEpoch] = useState(0);
  useEffect(() => {
    let stop = false;
    const timer = window.setTimeout(() => refreshPrice(() => stop), 400);
    return () => {
      stop = true;
      window.clearTimeout(timer);
    };
  }, [refreshPrice, priceEpoch]);

  const estimate = price.phase === 'ready' ? price.estimate : null;
  const consented = consentOverride ?? consentQuery.data?.consented ?? estimate?.consented ?? false;
  const priceFresh = estimate !== null && price.phase === 'ready' && price.forPrompt === subjectKey;

  // Why Run is off, in plain words (null when it can run).
  const blocked: string | null = (() => {
    if (price.phase === 'error') {
      if (price.kind === 'plan') return price.message ?? t('blockedPlan');
      if (price.kind === 'tooLong') return t('blockedTooLong');
      if (price.kind === 'credits') return price.message ?? t('blockedCredits');
      return null;
    }
    if (!estimate) return null;
    if (estimate.unavailableReason) return estimate.unavailableReason;
    if (!estimate.affordable && !estimate.cached) return t('blockedCredits');
    return null;
  })();

  const doRun = useCallback(async () => {
    if (!sourceId || !projectId) return;
    setStartError(null);
    setStarting(true);
    try {
      const queued = await startObject(projectId, sourceId, { ...(body ? { seed: body } : { prompt: trimmed }), effect, strength, invert, featherPx });
      const doc = getActiveDoc();
      const coverage = { startSec: 0, endSec: null };
      const anchor: ResultAnchor = doc
        ? anchorForClips(doc, [clip.id], coverage)
        : { assetKey: sourceId, clipIds: [clip.id], assetClipIdsAtLaunch: [clip.id], coverage };
      results?.launch({ id: queued.requestId, tool: 'objectEffect', requestId: queued.requestId, executionId: queued.executionId, anchor, intent: { prompt: subjectName, effect, strength, invert, featherPx, ...(queued.workflowId ? { workflowId: queued.workflowId } : {}) }, state: 'running', createdAt: new Date().toISOString() });
    } catch (error) {
      const c = classifyObjectError(error);
      if (c.kind === 'consent') {
        setConsentOverride(false);
        setDialog(true);
      } else if (c.kind === 'notFoundAtPoint') setStartError(t('notFoundAtPoint'));
      else setStartError(c.message ?? t('startFailed'));
    } finally {
      setStarting(false);
    }
  }, [sourceId, projectId, clip.id, subjectName, body, trimmed, effect, strength, invert, featherPx, results, t]);

  const run = () => {
    if (!consented) {
      setConsentError(null);
      setDialog(true);
      return;
    }
    void doRun();
  };

  const confirmConsent = async () => {
    if (!projectId) return;
    setConsentBusy(true);
    setConsentError(null);
    try {
      await grantObjectConsent(projectId);
      setConsentOverride(true);
      setDialog(false);
      await doRun();
    } catch (error) {
      setConsentError(classifyObjectError(error).message ?? t('startFailed'));
    } finally {
      setConsentBusy(false);
    }
  };

  const withdraw = async () => {
    if (!projectId) return;
    try {
      await withdrawObjectConsent(projectId);
      setConsentOverride(false);
      setConsentError(null);
    } catch {
      setConsentError(t('consentWithdrawFailed'));
    }
  };

  const failureText = (code: string | null, message: string | null, object: string): string => {
    switch (code) {
      case OBJECT_NOT_FOUND_AT_POINT:
        return t('notFoundAtPoint');
      case 'OBJECT_NOT_FOUND':
        return t('notFound', { object });
      case 'OBJECT_SOURCE_TOO_LONG':
      case 'SEGMENTATION_SOURCE_TOO_LONG':
        return t('blockedTooLong');
      case 'RESULT_UNAVAILABLE':
        return t('failResultUnavailable');
      case 'CONSENT_WITHDRAWN':
      case 'OBJECT_CONSENT_REQUIRED':
        return t('failConsent');
      case 'FRAME_BUDGET_EXCEEDED':
      case 'SEGMENTATION_FRAME_BUDGET_EXCEEDED':
        return t('failFrameBudget');
      default:
        return message || t('failedGeneric');
    }
  };
  const failedMessage = mine?.state === 'failed' ? failureText(mine.failure?.code ?? null, mine.failure?.message ?? null, String(mine.intent.prompt ?? subjectName)) : null;
  const stalled = mine?.state === 'running' && mine.stalled === true;
  const inFlight = mine?.state === 'running' && !stalled;
  const canRun = !readOnly && results !== null && promptOk && !atLimit && !blocked && priceFresh && !starting && !inFlight && !dialog;

  const setEffects = (next: RegionClipEffect[], label: string) => writeRegionEffects(clip.id, next, label);
  const effectLabels: Record<RegionEffectType, string> = {
    blur: t('effects.blur'),
    pixelate: t('effects.pixelate'),
    highlight: t('effects.highlight'),
    spotlight: t('effects.spotlight'),
    colorpop: t('effects.colorpop'),
    outline: t('effects.outline'),
  };
  const effectLabel = (e: RegionEffectType) => effectLabels[e];
  const objectName = (r: RegionClipEffect, index: number) => names.get(r.mask.analysisStepResultId) ?? t('objectFallback', { n: index + 1 });
  const dec2 = (v: number) => nf.number(v, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  if (!projectId) return null;
  if (!mediaRef) {
    return (
      <Text size="xs" c="dimmed" data-testid="object-effects-unavailable">
        {t('unavailableSource')}
      </Text>
    );
  }

  const priceLine = price.phase === 'loading' ? t('priceChecking') : price.phase === 'error' && price.kind === 'other' ? t('priceFailed') : estimate?.cached ? t('priceCached') : estimate && estimate.billingEnabled ? (estimate.upperBound ? t('priceUpTo', { credits: Math.ceil(estimate.credits) }) : t('priceAbout', { credits: Math.ceil(estimate.credits) })) : null;

  return (
    <Stack gap={6} data-testid="object-effects-section" id="object-effects-section">
      {host.store && (
        <Stack gap={4}>
          <Button size="compact-sm" fz="xs" variant={seed ? 'light' : 'filled'} style={tap} disabled={readOnly} onClick={startPicking} data-testid="object-pick">
            {picking ? t('pickCancel') : seed ? t('pickAgain') : t('pickOnPicture')}
          </Button>
          {picking && (
            <Text size="xs" c="dimmed" data-testid="object-pick-hint">
              {t('pickHint')}
            </Text>
          )}
          {seed && !picking && (
            <Group gap={6} wrap="nowrap" data-testid="object-picked">
              <Text size="xs" style={{ flex: 1 }}>
                {t('pickedAt', { at: `${Math.floor(seed.atSec / 60)}:${String(Math.floor(seed.atSec % 60)).padStart(2, '0')}` })}
              </Text>
              <Button size="compact-xs" fz="xs" variant="subtle" style={tap} disabled={readOnly} onClick={() => setSeed(null)} data-testid="object-pick-clear">
                {t('pickClear')}
              </Button>
            </Group>
          )}
        </Stack>
      )}
      <TextInput
        size="xs"
        styles={inputStyles}
        label={host.store ? t('findByName') : t('objectLabel')}
        placeholder={t('objectPlaceholder')}
        value={prompt}
        maxLength={PROMPT_MAX}
        disabled={readOnly}
        onChange={(e) => {
          setPrompt(e.currentTarget.value);
          if (e.currentTarget.value) setSeed(null);
        }}
        data-testid="object-prompt"
      />
      <Group gap={4} role="group" aria-label={t('quickPicks')}>
        {[
          { key: 'chipLicensePlate', label: t('chipLicensePlate') },
          { key: 'chipFace', label: t('chipFace') },
          { key: 'chipLogo', label: t('chipLogo') },
        ].map(({ key, label }) => (
          <Button key={key} size="compact-xs" fz="xs" variant="light" style={tap} disabled={readOnly} onClick={() => {
            setPrompt(label);
            setSeed(null);
          }} data-testid={`object-chip-${key}`}>
            {label}
          </Button>
        ))}
      </Group>
      <Select
        size="xs"
        styles={inputStyles}
        label={t('effectLabel')}
        data={REGION_EFFECTS.map((e) => ({ value: e, label: effectLabel(e) }))}
        value={effect}
        allowDeselect={false}
        disabled={readOnly}
        onChange={(v) => v && setEffect(v as RegionEffectType)}
        data-testid="object-effect"
      />
      <Switch size="xs" styles={switchStyles} label={t('invert')} checked={invert} disabled={readOnly} onChange={(e) => setInvert(e.currentTarget.checked)} data-testid="object-invert" />
      <Group gap={6} wrap="nowrap" align="center" style={sliderBox} data-testid="object-strength">
        <Text size="xs" w={70} c="dimmed">
          {t('strength')}
        </Text>
        <LiveSlider size="xs" thumbSize={coarse ? 28 : undefined} style={{ flex: 1 }} thumbLabel={t('strength')} min={0} max={1} step={0.01} value={strength} disabled={readOnly} onChangeEnd={setStrength} label={dec2} />
      </Group>
      <NumberInput size="xs" styles={inputStyles} label={t('feather')} min={0} max={64} step={1} allowDecimal={false} value={featherPx} disabled={readOnly} onChange={(v) => setFeatherPx(typeof v === 'number' ? v : 0)} w={140} data-testid="object-feather" />

      {priceLine && (
        <Text size="xs" role="status" data-testid="object-price">
          {priceLine}
        </Text>
      )}
      {blocked && (
        <Text size="xs" c="orange" role="alert" data-testid="object-blocked">
          {blocked}
        </Text>
      )}
      {atLimit && (
        <Text size="xs" c="orange" data-testid="object-limit">
          {t('limitReached', { max: MAX_REGION_EFFECTS })}
        </Text>
      )}
      {startError && (
        <Group gap="xs" align="center" data-testid="object-start-error-row">
          <Text size="xs" c="red" role="alert" style={{ flex: 1 }} data-testid="object-start-error">
            {startError}
          </Text>
          {seed && startError === t('notFoundAtPoint') && (
            <Button size="compact-xs" fz="xs" variant="light" style={tap} onClick={startPicking} data-testid="object-pick-again">
              {t('pickAgainAction')}
            </Button>
          )}
        </Group>
      )}
      {stalled && (
        <Group gap="xs" align="center" data-testid="object-poll-stalled">
          <Text size="xs" c="red" role="alert" style={{ flex: 1 }}>
            {t('pollStalled')}
          </Text>
          <Button size="compact-xs" fz="xs" variant="light" style={tap} onClick={() => mine && results?.update(mine.id, { stalled: false })} data-testid="object-poll-retry">
            {t('pollRetry')}
          </Button>
        </Group>
      )}
      {mine?.state === 'needsReview' && (
        <Group gap="xs" align="center" data-testid="object-needs-ok">
          <JobStatusBadge state="needsReview" />
          <Text size="xs" c="orange" role="status">
            {tb('sectionNeedsOk')}
          </Text>
        </Group>
      )}
      {failedMessage && (
        <Group gap="xs" align="center" wrap="nowrap" data-testid="object-failed-row">
          <JobStatusBadge state="failed" />
          <Text size="xs" c="red" role="alert" data-testid="object-failed">
            {failedMessage}
          </Text>
          {mine?.failure?.code === OBJECT_NOT_FOUND_AT_POINT && (
            <Button size="compact-xs" fz="xs" variant="light" style={tap} disabled={readOnly} onClick={startPicking} data-testid="object-pick-again">
              {t('pickAgainAction')}
            </Button>
          )}
        </Group>
      )}

      {inFlight ? (
        <Group gap="xs" align="center" data-testid="object-progress">
          <Stack gap={2} style={{ flex: 1 }}>
            <Text size="xs" role="status">
              {t('progressRunning', { object: String(mine?.intent.prompt ?? subjectName) })}
            </Text>
            <InlineJobStatus job={serverJob ?? localJob({ state: 'running', type: 'objectAnalysis', executionId: mine?.executionId })} />
          </Stack>
          <Button size="compact-xs" fz="xs" variant="subtle" style={tap} onClick={() => mine && results?.resolve(mine.id, 'discarded')} data-testid="object-stop">
            {t('stopWatching')}
          </Button>
        </Group>
      ) : (
        <Group justify="space-between" gap="xs">
          <Button size="compact-sm" fz="xs" style={tap} disabled={!canRun} loading={starting} onClick={run} data-testid="object-run">
            {t('run')}
          </Button>
          {consented && !readOnly && (
            <Button size="compact-xs" fz="xs" variant="subtle" style={tap} onClick={() => void withdraw()} data-testid="object-withdraw">
              {t('consentWithdraw')}
            </Button>
          )}
        </Group>
      )}
      {inFlight && (
        <Text size="xs" c="dimmed">
          {t('progressNote')}
        </Text>
      )}
      <Text size="xs" c="dimmed" data-testid="object-preview-hint">
        {t('previewHint')}
      </Text>

      {effects.length > 0 && (
        <Stack gap={4} data-testid="object-effects-list" role="list" aria-label={t('existingTitle')}>
          <Text size="xs" fw={600}>
            {t('existingTitle')}
          </Text>
          {effects.map((r, index) => {
            const name = objectName(r, index);
            const word = effectLabel(r.effect);
            return (
              <Stack key={`${r.mask.analysisStepResultId}-${index}`} gap={2} role="listitem" data-testid={`object-row-${index}`}>
                <Group gap={6} wrap="nowrap" align="center">
                  <Switch size="xs" styles={switchStyles} checked={r.enabled} disabled={readOnly} aria-label={t('rowToggle', { effect: word, object: name })} onChange={(e) => setEffects(effects.map((x, i) => (i === index ? { ...x, enabled: e.currentTarget.checked } : x)), t('labelToggle'))} data-testid={`object-row-toggle-${index}`} />
                  <Text size="xs" style={{ flex: 1 }} lineClamp={1}>
                    {t('rowToggle', { effect: word, object: name })}
                  </Text>
                  <Button size="compact-xs" fz="xs" variant="light" color="red" style={tap} disabled={readOnly} aria-label={t('rowRemoveAria', { effect: word, object: name })} onClick={() => setEffects(effects.filter((_, i) => i !== index), t('labelRemove'))} data-testid={`object-row-remove-${index}`}>
                    {t('rowRemove')}
                  </Button>
                </Group>
                <div style={sliderBox}><LiveSlider size="xs" thumbSize={coarse ? 28 : undefined} thumbLabel={t('rowStrength', { effect: word, object: name })} min={0} max={1} step={0.01} value={r.strength} disabled={readOnly} onChangeEnd={(v) => setEffects(effects.map((x, i) => (i === index ? { ...x, strength: v } : x)), t('labelStrength'))} label={dec2} data-testid={`object-row-strength-${index}`} /></div>
              </Stack>
            );
          })}
        </Stack>
      )}

      <ObjectConsentDialog opened={dialog} busy={consentBusy} error={consentError} onCancel={() => setDialog(false)} onConfirm={() => void confirmConsent().then(() => setPriceEpoch((n) => n + 1))} />
    </Stack>
  );
}
