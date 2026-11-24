'use client';

import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { ActionIcon, Alert, Button, Card, Group, List, Modal, Paper, Stack, Text, Title } from '@mantine/core';
import { useDebouncedValue, useMediaQuery } from '@mantine/hooks';
import { IconAlertTriangle, IconPencil, IconTrash } from '@tabler/icons-react';
import { createChangeRequest, estimateChangeRequest } from '@/lib/api/change-requests';
import { createRevision, runHref } from '@/lib/api/revisions';
import { useRevisions } from '@/lib/hooks/use-revisions';
import type { StepChangeCapabilities, StepChangeEstimatePreview, StepChangeRequest } from '@/lib/types/change-requests';
import type { RevisionHistory } from '@/lib/types/revisions';
import { creditsNeededLine } from '@/lib/utils/billing';
import { formatCredits } from '@/lib/utils/usage-labels';
import { useFormat, useT } from '@/i18n';
import { ChangeHistory } from './ChangeHistory';
import { FilmChangeSection } from './FilmChangeSection';
import { crCopy, useCrT } from './change-request-copy';
import { describeChangeRequestError } from './change-request-errors';
import {
  DEFAULT_MAX_TARGETS,
  buildSetBody,
  canCombineFilm,
  filmTarget,
  isFilmCapability,
  targetErrors,
  type ChangeEntry,
} from './change-set';
import { filmPlan, hasRevisionSurface, replayStepLines } from './film-helpers';
import { describeRevisionError, type RevisionRefusal } from './revision-errors';
import type { ChangeSetState } from './use-change-set';

const DEBOUNCE_MS = 500;

export interface ChangeVideoPanelProps {
  projectId: string;
  /** The run this page shows. */
  executionId: string;
  /** The run's status: the video's history is re-read when it changes (e.g. the run just finished). */
  runStatus?: string;
  runFinished: boolean;
  /** The pending changes (steps, app fields and the video's own draft). */
  changeSet: ChangeSetState;
  capabilities: StepChangeCapabilities | undefined;
  /** The change requests made against this run, or that are this run. */
  requests: StepChangeRequest[];
  onEditEntry: (entry: ChangeEntry) => void;
  onEditFields?: () => void;
  /** Called once a request was created, before navigating to the new run. */
  onCreated: () => void;
  /** The video's note field: the film step's "Request changes" focuses it. */
  textareaRef: RefObject<HTMLTextAreaElement | null>;
}

/**
 * "Change this video": everything that can be changed about a finished run in one place. The video itself (its note, scenes,
 * cut), the other steps and app fields waiting in the set, one estimate, one confirm, and the history of changes. Renders
 * nothing for a run with nothing to change and nothing changed.
 */
export function ChangeVideoPanel(props: ChangeVideoPanelProps) {
  const { projectId, executionId, runStatus, changeSet, capabilities, requests } = props;
  const { data: history, error, mutate } = useRevisions(projectId, executionId);

  // A run finishing changes `canRevise` and adds its version: re-read on every status change after the first.
  const seenStatus = useRef(runStatus);
  useEffect(() => {
    if (seenStatus.current === runStatus) return;
    seenStatus.current = runStatus;
    void mutate();
  }, [runStatus, mutate]);

  const filmHistory = !error && hasRevisionSurface(history) ? history : null;
  const hasSteps = !!capabilities?.steps.some((c) => c.supported);
  const anything = !!filmHistory || hasSteps || requests.length > 0 || changeSet.entries.length > 0 || !!changeSet.fields;
  if (!anything) return null;
  return <ChangeVideoCard {...props} history={filmHistory} refreshHistory={() => void mutate()} />;
}

interface CardProps extends ChangeVideoPanelProps {
  /** The video's history when the run has a film surface; null otherwise. */
  history: RevisionHistory | null;
  refreshHistory: () => void;
}

function ChangeVideoCard({
  projectId, executionId, runFinished, changeSet, capabilities, requests, onEditEntry, onEditFields, onCreated, textareaRef,
  history, refreshHistory,
}: CardProps) {
  const t = useCrT();
  const tr = useT();
  const trev = useT('revisions');
  const tp = useT('revisions.panel');
  const fm = useFormat();
  const CR_COPY = crCopy(t);
  const router = useRouter();
  const isPhone = useMediaQuery('(max-width: 48em)');
  const [sheetOpen, setSheetOpen] = useState(false);
  const [previewState, setPreview] = useState<{ key: string; value: StepChangeEstimatePreview | null } | null>(null);
  const [refusal, setRefusal] = useState<RevisionRefusal | null>(null);
  const [entryErrors, setEntryErrors] = useState<Record<number, string[]>>({});
  const [submitting, setSubmitting] = useState(false);

  const { entries, fields, film, patchFilm, remove, setFields } = changeSet;
  const maxTargets = capabilities?.maxTargets ?? DEFAULT_MAX_TARGETS;
  const filmCap = capabilities?.steps.find(isFilmCapability) ?? null;
  const filmText = film.instruction.trim();
  const hasOthers = entries.length > 0 || !!fields;
  // Two routes, chosen here: the video alone is a video change; anything else is one change request with the video as a target.
  const route: 'none' | 'film' | 'set' = hasOthers ? 'set' : filmText ? 'film' : 'none';
  const combined = hasOthers && !!filmText;
  const filmDisabled = !history || history.availability === 'Unavailable' || !history.canRevise;
  // The video's note rides along only when the video can be changed and is combinable, and the request has room for it.
  const overMax = entries.length + (filmText ? 1 : 0) > maxTargets;
  const blocked = combined && (!(filmCap && canCombineFilm(filmCap)) || filmDisabled || overMax);
  const filmLocked = hasOthers && !filmText && entries.length >= maxTargets;
  const filmReq = combined && !blocked ? filmTarget(filmCap, filmText) : null;
  const body = useMemo(() => (route === 'set' ? buildSetBody(entries, fields, filmReq) : null), [route, entries, fields, filmReq]);
  const count = entries.length + (fields ? 1 : 0) + (filmText ? 1 : 0);
  const setCount = entries.length + (filmText ? 1 : 0);
  const key = body && !blocked ? JSON.stringify(body) : '';
  const [debouncedKey] = useDebouncedValue(key, DEBOUNCE_MS);

  const plan = history ? filmPlan(history, film.cutPolicy) : null;
  const startFromId = history ? (film.fromVersionId ?? history.headVersionId ?? history.currentVersionId) : null;

  const fail = (e: unknown, via: 'film' | 'set') => {
    if (via === 'film') {
      setEntryErrors({});
      setRefusal(describeRevisionError(e, trev));
      return;
    }
    const mapped = targetErrors(e);
    const hasEntry = !!mapped && Object.keys(mapped.byIndex).length > 0;
    setEntryErrors(hasEntry ? mapped.byIndex : {});
    // Messages tied to a change sit beside it; anything else (or a refusal that is not a 400) is the alert.
    setRefusal(hasEntry && mapped.general.length === 0 ? null : describeChangeRequestError(e, t));
  };

  useEffect(() => {
    if (!debouncedKey) return;
    let cancelled = false;
    estimateChangeRequest(projectId, executionId, JSON.parse(debouncedKey))
      .then((p) => { if (!cancelled) { setPreview({ key: debouncedKey, value: p }); setRefusal(null); setEntryErrors({}); } })
      .catch((e) => { if (!cancelled) { setPreview({ key: debouncedKey, value: null }); fail(e, 'set'); } });
    return () => { cancelled = true; };
  // `fail` and `t` change identity per render; the estimate is re-asked when the request changes only.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [debouncedKey, projectId, executionId]);

  const submit = async () => {
    if (submitting || blocked) return;
    setRefusal(null);
    setEntryErrors({});
    if (route === 'film' && history && !filmDisabled) {
      setSubmitting(true);
      try {
        const revision = await createRevision(projectId, executionId, {
          instruction: filmText,
          targets: film.scenes.length > 0 ? { scenes: film.scenes } : null,
          fromVersionId: startFromId ?? null,
          mode: 'Render',
          ...(plan?.chooseCut ? { cutPolicy: film.cutPolicy } : {}),
        });
        refreshHistory();
        onCreated();
        setSheetOpen(false);
        router.push(runHref(projectId, revision.workflowId, revision.executionId));
      } catch (e) {
        fail(e, 'film');
      } finally {
        setSubmitting(false);
      }
      return;
    }
    if (route === 'set' && body) {
      setSubmitting(true);
      try {
        const created = await createChangeRequest(projectId, executionId, body);
        refreshHistory();
        onCreated();
        setSheetOpen(false);
        router.push(runHref(projectId, created.workflowDefinitionId, created.executionId));
      } catch (e) {
        fail(e, 'set');
      } finally {
        setSubmitting(false);
      }
    }
  };

  const changeVersion = useCallback((versionId: string) => {
    patchFilm({ fromVersionId: versionId });
    const el = textareaRef.current;
    if (el) {
      if (typeof el.scrollIntoView === 'function') el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      el.focus({ preventScroll: true });
    }
  }, [patchFilm, textareaRef]);

  // The price: from the video's own history when it is changed alone, from the request's estimate otherwise.
  const preview = debouncedKey && previewState?.key === debouncedKey && debouncedKey === key ? previewState.value : null;
  const estimating = !!key && previewState?.key !== key;
  const setEstimate = preview?.estimate ?? null;
  const filmEstimate = route === 'film' ? plan?.estimate ?? null : null;
  const estimate = route === 'film' ? filmEstimate : setEstimate;
  const needs = estimate?.billingEnabled ? creditsNeededLine(estimate.requiredCredits, tr, fm.locale) : null;
  const redo = preview?.replay.steps.filter((s) => s.disposition !== 'Reuse' && s.disposition !== 'Skip') ?? [];
  const reuse = preview?.replay.steps.filter((s) => s.disposition === 'Reuse') ?? [];
  const filmRedo = route === 'film' && plan?.replay ? replayStepLines(plan.replay, plan.chooseCut ? film.cutPolicy : 'KeepCut', tp) : [];
  const full = setCount >= maxTargets;
  const filmEstimateText = filmEstimate?.billingEnabled
    ? (filmEstimate.lowCredits === filmEstimate.highCredits
      ? tp('estimateOne', { low: formatCredits(filmEstimate.lowCredits, fm.locale) })
      : tp('estimateRange', { low: formatCredits(filmEstimate.lowCredits, fm.locale), high: formatCredits(filmEstimate.highCredits, fm.locale) }))
    : '';
  const rerunText = tp('rerun');
  const durationText = plan?.replay ? tp('durationReplay') : tp('duration');
  const submitOff = route === 'none' || blocked || (route === 'film' && filmDisabled);
  // Said beside the button, so the sheet on a phone explains why it cannot be pressed.
  const offReason = !submitOff ? null
    : blocked ? (overMax ? t('panel.tooMany', { max: maxTargets }) : filmDisabled ? t('panel.videoUnavailable') : t('panel.notCombinable'))
      : route === 'film' && filmDisabled ? t('panel.videoUnavailable') : null;

  const entriesList = (
    <Stack gap="xs" component="ul" m={0} p={0} style={{ listStyle: 'none' }} aria-label={t('panel.othersTitle')} data-testid="change-set-entries">
      {fields && (
        <li>
          <Paper withBorder p="sm" radius="md" data-testid="change-set-entry-fields">
            <Group justify="space-between" wrap="nowrap" align="flex-start" gap="xs">
              <div style={{ minWidth: 0 }}>
                <Text size="sm" fw={600}>{t('set.fieldsTitle')}</Text>
                {fields.lines.map((line) => <Text key={line} size="sm" c="dimmed" style={{ overflowWrap: 'anywhere' }}>{line}</Text>)}
              </div>
              <Group gap={4} wrap="nowrap">
                {onEditFields && (
                  <ActionIcon variant="subtle" size={44} aria-label={t('set.editLabel', { label: t('set.fieldsTitle') })} onClick={() => { setSheetOpen(false); onEditFields(); }} data-testid="change-set-edit-fields">
                    <IconPencil size={16} />
                  </ActionIcon>
                )}
                <ActionIcon variant="subtle" color="red" size={44} aria-label={t('set.removeLabel', { label: t('set.fieldsTitle') })} onClick={() => { if (count <= 1) setSheetOpen(false); setFields(null); }} data-testid="change-set-remove-fields">
                  <IconTrash size={16} />
                </ActionIcon>
              </Group>
            </Group>
          </Paper>
        </li>
      )}
      {entries.map((entry, i) => (
        <li key={entry.stepOrder}>
          <Paper withBorder p="sm" radius="md" data-testid={`change-set-entry-${entry.stepOrder}`}>
            <Group justify="space-between" wrap="nowrap" align="flex-start" gap="xs">
              <div style={{ minWidth: 0 }}>
                <Text size="sm" fw={600}>{entry.label}</Text>
                {entry.summary.map((line) => <Text key={line} size="sm" c="dimmed" style={{ overflowWrap: 'anywhere' }}>{line}</Text>)}
              </div>
              <Group gap={4} wrap="nowrap">
                <ActionIcon variant="subtle" size={44} aria-label={t('set.editLabel', { label: entry.label })} onClick={() => { setSheetOpen(false); onEditEntry(entry); }} data-testid={`change-set-edit-${entry.stepOrder}`}>
                  <IconPencil size={16} />
                </ActionIcon>
                <ActionIcon variant="subtle" color="red" size={44} aria-label={t('set.removeLabel', { label: entry.label })} onClick={() => { if (count <= 1) setSheetOpen(false); remove(entry.stepOrder); }} data-testid={`change-set-remove-${entry.stepOrder}`}>
                  <IconTrash size={16} />
                </ActionIcon>
              </Group>
            </Group>
            {(entryErrors[i] ?? []).map((m) => (
              <Text key={m} size="sm" c="red" role="alert" mt={4} data-testid={`change-set-entry-error-${entry.stepOrder}`}>{m}</Text>
            ))}
          </Paper>
        </li>
      ))}
    </Stack>
  );

  const review = count > 0 && (
    <Stack gap="md" data-testid="change-set-review">
      <Text size="sm" c="dimmed" data-testid="change-set-count">{t('set.count', { count })}</Text>
      {offReason && <Alert color="orange" variant="light" data-testid="change-set-film-alone">{offReason}</Alert>}
      <div role="status" aria-live="polite">
      {estimating && <Text size="sm" c="dimmed">{CR_COPY.estimating}</Text>}
      {(route === 'film' || preview) && (
        <Stack gap={4} data-testid="change-set-preview">
          {estimate?.billingEnabled && (
            <Text size="sm" fw={500} data-testid="change-set-estimate">
              {route === 'film'
                ? filmEstimateText
                : CR_COPY.priceAbout(formatCredits(estimate.lowCredits, fm.locale), formatCredits(estimate.highCredits, fm.locale))}
            </Text>
          )}
          {route === 'set' && setEstimate?.billingEnabled && setEstimate.targetExactCredits != null && (
            <Text size="sm" data-testid="change-set-exact" title={setEstimate.targetExactNote ?? undefined}>
              {CR_COPY.thisPart(formatCredits(setEstimate.targetExactCredits, fm.locale))}
            </Text>
          )}
          {needs && <Text size="sm" data-testid="change-set-needs">{needs}</Text>}
          {route === 'film' && filmRedo.length > 0 && (
            <div data-testid="request-changes-replay">
              <Text size="sm" fw={500}>{rerunText}</Text>
              <List size="sm" mt={4} spacing={2}>
                {filmRedo.map((line, i) => <List.Item key={i}>{line}</List.Item>)}
              </List>
            </div>
          )}
          {route === 'film' && (
            <Text size="sm" c="dimmed">{durationText}</Text>
          )}
          {redo.length > 0 && (
            <div>
              <Text size="sm" fw={500}>{CR_COPY.redone}</Text>
              <ul style={{ margin: 0, paddingLeft: 20 }}>{redo.map((s) => <li key={s.stepOrder}><Text size="sm" span>{s.label}</Text></li>)}</ul>
            </div>
          )}
          {reuse.length > 0 && (
            <div>
              <Text size="sm" fw={500}>{CR_COPY.reused}</Text>
              <ul style={{ margin: 0, paddingLeft: 20 }}>{reuse.map((s) => <li key={s.stepOrder}><Text size="sm" span c="dimmed">{s.label}</Text></li>)}</ul>
            </div>
          )}
          {estimate?.billingEnabled && !estimate.affordable && (
            <Alert color="yellow" variant="light" role="alert" icon={<IconAlertTriangle size={16} />} title={CR_COPY.shortfall} data-testid="change-set-shortfall">
              <Text size="sm">{CR_COPY.shortfallBody}</Text>
              <Button component={Link} href="/settings/billing" size="compact-sm" variant="light" mt="xs">{CR_COPY.topUp}</Button>
            </Alert>
          )}
        </Stack>
      )}
      </div>

      {refusal && (
        <Alert color={refusal.color} variant="light" icon={<IconAlertTriangle size={16} />} title={refusal.title} role="alert" data-testid="change-set-error">
          <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>{refusal.message}</Text>
          {refusal.title === t('refusals.notEntitled.title') && (
            <Button component={Link} href="/settings/billing#plans" size="compact-sm" variant="light" mt="xs">{t('seePlans')}</Button>
          )}
          {refusal.topUp && <Button component={Link} href="/settings/billing" size="compact-sm" variant="light" mt="xs">{CR_COPY.topUp}</Button>}
        </Alert>
      )}

      {full && hasOthers && <Text size="sm" c="dimmed" data-testid="change-set-max">{t('set.maxReached', { max: maxTargets })}</Text>}
      <Group justify="flex-end">
        <Button onClick={() => void submit()} loading={submitting} disabled={submitOff} mih={44} w={{ base: '100%', xs: 'auto' }} data-testid="change-set-submit">
          {t('set.submit', { count })}
        </Button>
      </Group>
    </Stack>
  );

  const filmErrors = filmReq ? entryErrors[entries.length] ?? [] : [];

  return (
    <Card withBorder shadow="sm" radius="lg" padding="lg" component="section" aria-labelledby="change-video-title" data-testid="change-video-panel">
      <Stack gap="xl">
        <Title order={2} size="h3" id="change-video-title">{t('panel.title')}</Title>

        {history && (
          <section aria-labelledby="change-video-film" data-testid="execution-revisions">
            <Title order={3} size="h4" mb="md" id="change-video-film">{t('panel.videoTitle')}</Title>
            <FilmChangeSection
              history={history}
              film={film}
              onChange={patchFilm}
              runFinished={runFinished}
              combined={combined}
              errors={filmErrors}
              lockedReason={filmLocked ? t('panel.tooMany', { max: maxTargets }) : null}
              textareaRef={textareaRef}
            />
          </section>
        )}

        <section aria-labelledby="change-video-others" data-testid="change-set-panel">
          <Title order={3} size="h4" mb="xs" id="change-video-others">{t('panel.othersTitle')}</Title>
          <Text size="sm" c="dimmed" mb={hasOthers && !isPhone ? 'sm' : 0} data-testid="change-set-hint">{t('panel.othersHint')}</Text>
          {!isPhone && hasOthers && entriesList}
        </section>

        {!isPhone && review}
        {isPhone && count > 0 && (
          <>
            <div style={{ height: 72 }} aria-hidden />
            <Paper
              withBorder shadow="md" p="sm" data-testid="change-set-bar"
              style={{ position: 'fixed', left: 0, right: 0, bottom: 0, zIndex: 150, borderRadius: 0 }}
            >
              <Group justify="space-between" wrap="nowrap">
                <Text size="sm" fw={600}>{t('set.count', { count })}</Text>
                <Button onClick={() => setSheetOpen(true)} mih={44} data-testid="change-set-sheet-open">{t('set.review')}</Button>
              </Group>
            </Paper>
            <Modal opened={sheetOpen} onClose={() => setSheetOpen(false)} fullScreen trapFocus returnFocus title={t('set.title')} data-testid="change-set-sheet">
              <Stack gap="md">
                {hasOthers && entriesList}
                {review}
              </Stack>
            </Modal>
          </>
        )}

        <ChangeHistory
          projectId={projectId}
          executionId={executionId}
          history={history}
          requests={requests}
          onChangeVersion={history ? changeVersion : undefined}
        />
      </Stack>
    </Card>
  );
}
