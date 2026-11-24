'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import {
  ActionIcon, Alert, Button, Checkbox, Group, Modal, NativeSelect, NumberInput, Paper, SegmentedControl, Stack, Switch, Text, TextInput, Textarea,
} from '@mantine/core';
import { useDebouncedValue, useMediaQuery } from '@mantine/hooks';
import { IconAlertTriangle, IconPlus, IconTrash } from '@tabler/icons-react';
import { createChangeRequest, estimateChangeRequest } from '@/lib/api/change-requests';
import { runHref } from '@/lib/api/revisions';
import type { PronunciationEntry, StepChangeEstimatePreview, StepChangeItem, StepChangeStepCapability } from '@/lib/types/change-requests';
import { creditsNeededLine } from '@/lib/utils/billing';
import { formatCredits } from '@/lib/utils/usage-labels';
import type { RevisionRefusal } from '@/components/change-requests/revision-errors';
import { useFormat, useT } from '@/i18n';
import { costNote, crCopy, itemKindOf, itemName, keyCopy, shotOf, statusLabel, useCrT, valueLabel, type CrT } from './change-request-copy';
import { describeChangeRequestError } from './change-request-errors';
import {
  buildChangeRequest, emptyForm, isDirect, isItemWording, overrideKind, startingDraft, visibleSpecs, type DraftValue, type FormState, type ItemDraft,
} from './change-request-form';
import { summarizeRequest, type ChangeEntry } from './change-set';

const DEBOUNCE_MS = 500;

export interface RequestStepChangeModalProps {
  projectId: string;
  executionId: string;
  capability: StepChangeStepCapability;
  opened: boolean;
  onClose: () => void;
  onCreated?: () => void;
  /**
   * When given, the modal collects the change into the run's set: the primary action is "Add to changes" and, while the
   * set is empty, "Request this change now" keeps the one-step flow. Without it the modal behaves as before.
   */
  onAdd?: (entry: ChangeEntry) => void;
  /** The entry already in the set for this step: the form opens prefilled. */
  existing?: ChangeEntry | null;
  /** Changes in the set (this step's own entry included) and the most one request may carry. */
  setSize?: number;
  maxTargets?: number;
}

function DraftFields({ cap, draft, onChange, idPrefix, mode }: {
  cap: StepChangeStepCapability; draft: ItemDraft; onChange: (d: ItemDraft) => void; idPrefix: string; mode: 'item' | 'all' | 'direct';
}) {
  const t = useCrT();
  const CR_COPY = crCopy(t);
  const set = (key: string, value: DraftValue) => onChange({ ...draft, [key]: value });
  return (
    <Stack gap="xs">
      {visibleSpecs(cap, mode).map((spec) => {
        const kind = overrideKind(spec);
        const copy = keyCopy(t, cap.stepType, spec.key);
        const raw = draft[spec.key];
        const fieldId = `${idPrefix}-${spec.key}`;
        if (kind === 'text') {
          const value = typeof raw === 'string' ? raw : '';
          // A short value (an id, a voice name) is a line; wording is a text area.
          const long = !!spec.multiLine || isItemWording(spec.key) || (spec.maxLength ?? 0) > 200 || spec.key === 'overlayText';
          return long ? (
            <Textarea
              key={spec.key} label={copy.label} description={copy.help} autosize minRows={2}
              maxLength={spec.maxLength ?? undefined} value={value}
              onChange={(e) => set(spec.key, e.currentTarget.value)}
            />
          ) : (
            <TextInput
              key={spec.key} label={copy.label} description={copy.help} placeholder={copy.placeholder}
              maxLength={spec.maxLength ?? undefined} value={value}
              onChange={(e) => set(spec.key, e.currentTarget.value)}
            />
          );
        }
        if (kind === 'number') {
          const min = spec.min ?? undefined;
          const max = spec.max ?? undefined;
          const span = min !== undefined && max !== undefined ? max - min : Infinity;
          const range = copy.unit
            ? t('rangeUnit', { min: fmtNumber(spec.min ?? 0), max: fmtNumber(spec.max ?? 0), unit: copy.unit })
            : t('range', { min: fmtNumber(spec.min ?? 0), max: fmtNumber(spec.max ?? 0) });
          return (
            <NumberInput
              key={spec.key} label={copy.label}
              description={[copy.help, spec.min !== null && spec.max !== null ? range : null].filter(Boolean).join(' ')}
              min={min} max={max} step={span <= 10 ? 0.1 : 1} decimalScale={2}
              value={typeof raw === 'number' ? raw : ''}
              onChange={(v) => set(spec.key, typeof v === 'number' ? v : null)}
            />
          );
        }
        if (kind === 'flag') {
          return (
            <Switch
              key={spec.key} label={copy.label} description={copy.help} checked={raw === true}
              onChange={(e) => set(spec.key, e.currentTarget.checked)}
            />
          );
        }
        if (kind === 'enum') {
          return (
            <NativeSelect
              key={spec.key} label={copy.label} description={copy.help}
              value={typeof raw === 'string' ? raw : ''}
              onChange={(e) => set(spec.key, e.currentTarget.value)}
              data={[
                { value: '', label: CR_COPY.keepAsIs },
                ...(spec.values ?? []).map((v) => ({ value: v, label: valueLabel(t, spec.key, v) })),
              ]}
            />
          );
        }
        if (kind === 'pronunciation') {
          const entries = Array.isArray(raw) ? raw : [];
          const setEntries = (next: PronunciationEntry[]) => set(spec.key, next);
          return (
            <fieldset key={spec.key} style={{ border: 0, padding: 0, margin: 0 }} aria-labelledby={`${fieldId}-say`}>
              <Text id={`${fieldId}-say`} size="sm" fw={500}>{CR_COPY.sayItLabel}</Text>
              <Text size="xs" c="dimmed" mb={4}>{CR_COPY.sayItHelp}</Text>
              <Stack gap={6}>
                {entries.map((entry, i) => (
                  <Group key={i} gap="xs" align="flex-end" wrap="nowrap">
                    <TextInput
                      aria-label={`${CR_COPY.sayItTerm} ${i + 1}`} placeholder={CR_COPY.sayItTerm} value={entry.term} style={{ flex: 1 }}
                      onChange={(e) => setEntries(entries.map((p, j) => (j === i ? { ...p, term: e.currentTarget.value } : p)))}
                    />
                    <TextInput
                      aria-label={`${CR_COPY.sayItHow} ${i + 1}`} placeholder={CR_COPY.sayItHow} value={entry.spelling} style={{ flex: 1 }}
                      onChange={(e) => setEntries(entries.map((p, j) => (j === i ? { ...p, spelling: e.currentTarget.value } : p)))}
                    />
                    <ActionIcon
                      variant="subtle" color="red" size="lg" aria-label={`${CR_COPY.sayItRemove} ${i + 1}`}
                      onClick={() => setEntries(entries.filter((_, j) => j !== i))}
                    >
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Group>
                ))}
                {(!spec.maxEntries || entries.length < spec.maxEntries) && (
                  <Button
                    variant="subtle" size="compact-sm" leftSection={<IconPlus size={14} />} style={{ alignSelf: 'flex-start' }}
                    onClick={() => setEntries([...entries, { term: '', spelling: '' }])}
                  >
                    {CR_COPY.sayItAdd}
                  </Button>
                )}
              </Stack>
            </fieldset>
          );
        }
        return null;
      })}
    </Stack>
  );
}

const fmtNumber = (n: number) => String(Math.round(n * 100) / 100);

/** The shots of a step with the takes under each; every other item stands alone. */
function groupItems(stepType: string, items: StepChangeItem[]): { item: StepChangeItem; takes: StepChangeItem[] }[] {
  const groups: { item: StepChangeItem; takes: StepChangeItem[] }[] = [];
  const byId = new Map<string, { item: StepChangeItem; takes: StepChangeItem[] }>();
  for (const item of items) {
    const shot = shotOf(stepType, item.id);
    const parent = shot ? byId.get(shot) : undefined;
    if (parent) { parent.takes.push(item); continue; }
    const g = { item, takes: [] as StepChangeItem[] };
    groups.push(g);
    byId.set(item.id, g);
  }
  return groups;
}

function itemFacts(t: CrT, item: StepChangeItem): string {
  return [statusLabel(t, item.status), item.durationSec !== null ? t('seconds', { n: fmtNumber(item.durationSec) }) : null].filter(Boolean).join(' · ');
}

export function RequestStepChangeModal({
  projectId, executionId, capability, opened, onClose, onCreated, onAdd, existing = null, setSize = 0, maxTargets = 6,
}: RequestStepChangeModalProps) {
  const t = useCrT();
  const tr = useT();
  const fm = useFormat();
  const CR_COPY = crCopy(t);
  const router = useRouter();
  const isPhone = useMediaQuery('(max-width: 48em)');
  const [form, setForm] = useState<FormState>(() => existing?.form ?? emptyForm());
  const [previewState, setPreview] = useState<{ key: string; value: StepChangeEstimatePreview | null } | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [refusal, setRefusal] = useState<RevisionRefusal | null>(null);

  const built = useMemo(() => buildChangeRequest(capability, form, t), [capability, form, t]);
  // Next to other pending changes the price belongs to the combined review, not to this one alone.
  const othersInSet = onAdd ? setSize - (existing ? 1 : 0) : 0;
  const key = built.request && othersInSet === 0 ? JSON.stringify(built.request) : '';
  const [debouncedKey] = useDebouncedValue(key, DEBOUNCE_MS);

  useEffect(() => {
    if (!opened || !debouncedKey) return;
    let cancelled = false;
    estimateChangeRequest(projectId, executionId, JSON.parse(debouncedKey))
      .then((p) => { if (!cancelled) { setPreview({ key: debouncedKey, value: p }); setRefusal(null); } })
      .catch((e) => { if (!cancelled) { setPreview({ key: debouncedKey, value: null }); setRefusal(describeChangeRequestError(e, t)); } });
    return () => { cancelled = true; };
  // `t` is deliberately not a dependency: the estimate is re-asked when the request changes, not when the language object does.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, debouncedKey, projectId, executionId]);

  const patchDraft = (id: string, d: ItemDraft) => setForm((f) => ({ ...f, drafts: { ...f.drafts, [id]: d } }));
  const toggle = (id: string, on: boolean) =>
    setForm((f) => ({ ...f, selected: on ? [...f.selected, id] : f.selected.filter((x) => x !== id) }));

  const submit = async () => {
    if (!built.request) return;
    setSubmitting(true);
    setRefusal(null);
    try {
      const created = await createChangeRequest(projectId, executionId, built.request);
      onCreated?.();
      onClose();
      router.push(runHref(projectId, created.workflowDefinitionId, created.executionId));
    } catch (e) {
      setRefusal(describeChangeRequestError(e, t));
    } finally {
      setSubmitting(false);
    }
  };

  const add = () => {
    if (!built.request || !onAdd) return;
    onAdd({
      stepOrder: capability.stepOrder, label: capability.label, stepType: capability.stepType,
      request: built.request, form, summary: summarizeRequest(t, capability, built.request),
    });
    onClose();
  };
  const setFull = !existing && setSize >= maxTargets;

  // The preview belongs to the request it was computed for; once the form moves on it is stale.
  const preview = debouncedKey && previewState?.key === debouncedKey && debouncedKey === key ? previewState.value : null;
  const estimating = !!key && previewState?.key !== key;
  const estimate = preview?.estimate ?? null;
  const needs = estimate?.billingEnabled ? creditsNeededLine(estimate.requiredCredits, tr, fm.locale) : null;
  const redo = preview?.replay.steps.filter((s) => s.disposition !== 'Reuse' && s.disposition !== 'Skip') ?? [];
  const reuse = preview?.replay.steps.filter((s) => s.disposition === 'Reuse') ?? [];

  const kind = itemKindOf(capability.stepType, capability.itemKind);
  const direct = isDirect(capability);
  const renderItem = (item: StepChangeItem, nested: boolean) => {
    const on = form.selected.includes(item.id);
    const name = itemName(t, capability.stepType, item.id);
    // The wording is the label (a line, a clip's description); the plain name and the facts sit under it.
    const facts = itemFacts(t, item);
    return (
      <>
        <Checkbox
          checked={on}
          onChange={(e) => toggle(item.id, e.currentTarget.checked)}
          label={item.text ?? name}
          description={[name === item.text ? null : name, facts].filter(Boolean).join(' · ') || undefined}
          size={nested ? 'sm' : 'md'}
        />
        {on && (
          <div style={{ marginTop: 8 }}>
            <DraftFields
              cap={capability} idPrefix={item.id} mode="item"
              draft={form.drafts[item.id] ?? startingDraft(capability, item)}
              onChange={(d) => patchDraft(item.id, d)}
            />
          </div>
        )}
      </>
    );
  };

  return (
    <Modal
      opened={opened} onClose={onClose} fullScreen={!!isPhone} size="lg" trapFocus returnFocus
      title={CR_COPY.modalTitle(capability.label)} data-testid="change-request-modal"
    >
      <form onSubmit={(e) => { e.preventDefault(); if (onAdd) add(); else void submit(); }}>
        <Stack gap="md">
          <Text size="sm" c="dimmed">{direct ? CR_COPY.introDirect : CR_COPY.intro}</Text>
          {capability.costNote && costNote(t, capability.costNote) && (
            <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="change-request-cost-note">
              {costNote(t, capability.costNote)}
            </Alert>
          )}

          {direct ? (
            <DraftFields cap={capability} draft={form.all} idPrefix="direct" mode="direct" onChange={(d) => setForm((f) => ({ ...f, all: d }))} />
          ) : capability.items.length > 0 ? (
            <>
              <SegmentedControl
                fullWidth aria-label={t('scope')} value={form.scope}
                onChange={(v) => setForm((f) => ({ ...f, scope: v === 'all' ? 'all' : 'items' }))}
                data={[{ value: 'items', label: CR_COPY.scopeItems(kind) }, { value: 'all', label: CR_COPY.scopeAll(kind) }]}
              />
              {form.scope === 'all' ? (
                <DraftFields cap={capability} draft={form.all} idPrefix="all" mode="all" onChange={(d) => setForm((f) => ({ ...f, all: d }))} />
              ) : (
                <Stack gap="sm" role="group" aria-label={CR_COPY.pickItems(kind)}>
                  {groupItems(capability.stepType, capability.items).map(({ item, takes }) => (
                    <Paper key={item.id} withBorder p="sm" radius="md" data-testid={`change-request-item-${item.id}`}>
                      {renderItem(item, false)}
                      {takes.length > 0 && (
                        <Stack gap="xs" mt="xs" pl="md" role="group" aria-label={t('takesGroup', { name: itemName(t, capability.stepType, item.id) })}>
                          {takes.map((take) => (
                            <Paper key={take.id} withBorder p="xs" radius="sm" data-testid={`change-request-item-${take.id}`}>
                              {renderItem(take, true)}
                            </Paper>
                          ))}
                        </Stack>
                      )}
                    </Paper>
                  ))}
                </Stack>
              )}
            </>
          ) : (
            <Text size="sm" c="dimmed">{CR_COPY.noItems}</Text>
          )}

          <Textarea
            label={capability.allowsInstructionOnly ? CR_COPY.instructionRewrite : CR_COPY.instruction}
            placeholder={capability.allowsInstructionOnly ? CR_COPY.instructionRewritePlaceholder : CR_COPY.instructionPlaceholder} autosize minRows={2}
            value={form.instruction} onChange={(e) => setForm((f) => ({ ...f, instruction: e.currentTarget.value }))}
          />

          {built.errors.length > 0 && (
            <Alert color="yellow" variant="light" role="alert" data-testid="change-request-validation">
              {built.errors.map((m) => <Text key={m} size="sm">{m}</Text>)}
            </Alert>
          )}

          {estimating && <Text size="sm" c="dimmed">{CR_COPY.estimating}</Text>}
          {preview && (
            <Stack gap={4} data-testid="change-request-preview">
              {estimate?.billingEnabled && (
                <Text size="sm" fw={500} data-testid="change-request-estimate">
                  {CR_COPY.priceAbout(formatCredits(estimate.lowCredits, fm.locale), formatCredits(estimate.highCredits, fm.locale))}
                </Text>
              )}
              {estimate?.billingEnabled && estimate.targetExactCredits != null && (
                <Text size="sm" data-testid="change-request-exact" title={estimate.targetExactNote ?? undefined}>
                  {CR_COPY.thisPart(formatCredits(estimate.targetExactCredits, fm.locale))}
                </Text>
              )}
              {needs && <Text size="sm" data-testid="change-request-needs">{needs}</Text>}
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
                <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} title={CR_COPY.shortfall} data-testid="change-request-shortfall">
                  <Text size="sm">{CR_COPY.shortfallBody}</Text>
                  <Button component={Link} href="/settings/billing" size="compact-sm" variant="light" mt="xs">{CR_COPY.topUp}</Button>
                </Alert>
              )}
            </Stack>
          )}

          {refusal && (
            <Alert color={refusal.color} variant="light" icon={<IconAlertTriangle size={16} />} title={refusal.title} role="alert" data-testid="change-request-error">
              <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>{refusal.message}</Text>
              {refusal.title === t('refusals.notEntitled.title') && (
                <Button component={Link} href="/settings/billing#plans" size="compact-sm" variant="light" mt="xs" data-testid="change-request-see-plans">{t('seePlans')}</Button>
              )}
              {refusal.topUp && (
                <Button component={Link} href="/settings/billing" size="compact-sm" variant="light" mt="xs">{CR_COPY.topUp}</Button>
              )}
            </Alert>
          )}

          {onAdd && setFull && <Text size="sm" c="dimmed" data-testid="change-request-max">{t('set.maxReached', { max: maxTargets })}</Text>}
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>{CR_COPY.cancel}</Button>
            {onAdd ? (
              <>
                {setSize === 0 && (
                  <Button variant="light" loading={submitting} disabled={!built.request} onClick={() => void submit()} data-testid="change-request-now">
                    {t('requestNow')}
                  </Button>
                )}
                <Button type="submit" disabled={!built.request || setFull} data-testid="change-request-add">
                  {existing ? t('updateChange') : t('addToChanges')}
                </Button>
              </>
            ) : (
              <Button type="submit" loading={submitting} disabled={!built.request}>{CR_COPY.confirm}</Button>
            )}
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
