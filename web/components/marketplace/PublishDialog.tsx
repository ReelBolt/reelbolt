'use client';

import { useState } from 'react';
import { Checkbox, Modal, NativeSelect, Textarea, TextInput } from '@mantine/core';
import { useAuth } from '@/lib/hooks/use-auth';
import { useFormat, useT } from '@/i18n';
import { outputRefFor, publishRecipe, updateRecipe } from '@/lib/api/marketplace';
import { useMarketplaceConsent, usePublishPreview } from '@/lib/hooks/use-marketplace-owner';
import { useOutputVideos } from '@/lib/hooks/use-outputs';
import { useProjects } from '@/lib/hooks/use-projects';
import { useWorkflows } from '@/lib/hooks/use-workflows';
import type { MineRecipe, PublishResult } from '@/lib/types/marketplace';
import {
  DESCRIPTION_MAX,
  NAME_MAX,
  PUBLISH_LANGUAGES,
  SLOT_KIND_LABELS,
  categoryLabel,
  languageName,
  marketplaceErrorMessage,
  refusalMessage,
  removedLine,
} from './marketplace-model';
import { StatusChip } from './StatusChip';
import styles from './marketplace.module.css';

const CATEGORIES = ['promo', 'footage', 'brand', 'utility'];

interface PublishDialogProps {
  /** Known when opened from an app page; otherwise (updating from My recipes) the person picks them. */
  projectId?: string;
  workflowId?: string;
  /** Set to update a recipe: the same form, sent with PUT as a new version. */
  existing?: MineRecipe;
  onClose: () => void;
  onDone?: () => void;
}

/**
 * Share a workflow as a community recipe: what will be removed and what is refused, the texts it sends to AI models,
 * the preview output, the plain-text details, the versioned consent, then submit. Mount it only while open: the consent
 * text and the check are fetched on mount, so each opening reads the current ones.
 */
export function PublishDialog({ projectId: fixedProject, workflowId: fixedWorkflow, existing, onClose, onDone }: PublishDialogProps) {
  const t = useT('marketplace.publish');
  const tr = useT();
  const fm = useFormat();
  const { user } = useAuth();
  const [pickedProject, setPickedProject] = useState('');
  const [pickedWorkflow, setPickedWorkflow] = useState('');
  const projectId = fixedProject ?? pickedProject;
  const workflowId = fixedWorkflow ?? pickedWorkflow;

  const { data: projects } = useProjects();
  const { data: workflows } = useWorkflows(fixedProject ? '' : pickedProject);
  const { data: check, error: checkError, isLoading: checking } = usePublishPreview(projectId, workflowId, true);
  const { data: consent, mutate: refreshConsent } = useMarketplaceConsent(true);
  const { data: outputs } = useOutputVideos(projectId);

  const [outputRef, setOutputRef] = useState('');
  const [name, setName] = useState(existing?.name ?? '');
  const [description, setDescription] = useState(existing?.description ?? '');
  const [category, setCategory] = useState(existing?.category ?? 'promo');
  const initialLanguage = fm.locale.slice(0, 2);
  const [language, setLanguage] = useState(existing?.language ?? (PUBLISH_LANGUAGES.includes(initialLanguage) ? initialLanguage : 'en'));
  const [authorLabel, setAuthorLabel] = useState(existing?.authorLabel ?? user?.displayName ?? '');
  // Consent is tied to the version it was ticked for: a re-fetched newer text starts unticked.
  const [acceptedVersion, setAcceptedVersion] = useState<string | null>(null);
  const accepted = consent !== undefined && acceptedVersion === consent.version;
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<PublishResult | null>(null);

  const finished = (outputs ?? []).filter((o) => o.isFinal !== false);
  const blocked = Boolean(check) && (!check?.publishable || (check?.refusals.length ?? 0) > 0);
  const canSend = Boolean(check?.publishable) && !blocked && Boolean(consent) && !busy;
  const missing = [
    outputRef === '' ? t('needOutput') : null,
    name.trim() === '' ? t('needName') : null,
    authorLabel.trim() === '' ? t('needAuthor') : null,
  ].filter((m): m is string => m !== null);

  const submit = async () => {
    if (!canSend || !consent) return;
    // The button stays enabled: pressing it says what is still needed instead of doing nothing.
    if (missing.length > 0 || !accepted) {
      const parts: string[] = [];
      if (missing.length > 0) parts.push(t('stillNeeded', { items: missing.join(', ') }));
      if (!accepted) parts.push(tr('marketplace.errors.consentRequired'));
      setError(parts.join(' '));
      return;
    }
    setBusy(true);
    setError(null);
    const body = {
      projectId, workflowId, outputRef, name: name.trim(), description: description.trim(), category, language,
      authorLabel: authorLabel.trim(), consentAccepted: true, consentVersion: consent.version,
    };
    try {
      const res = existing ? await updateRecipe(existing.id, body) : await publishRecipe(body);
      setResult(res);
      onDone?.();
    } catch (err: unknown) {
      if ((err as { code?: unknown }).code === 'CONSENT_VERSION_STALE') {
        // The text changed while the dialog was open: show the new one and ask again.
        setAcceptedVersion(null);
        await refreshConsent();
      }
      setError(marketplaceErrorMessage(tr, err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={existing ? t('updateTitle') : t('title')} size="lg" centered>
      {result ? (
        <div className={styles.section} role="status">
          <p className={styles.plain}>{t('doneBody')}</p>
          <div className={styles.badgeRow}>
            <StatusChip status={result.status} preview={result.previewStatus} />
          </div>
          <div className={styles.actions}>
            <button type="button" className={styles.button} onClick={onClose}>
              {tr('marketplace.copy.close')}
            </button>
          </div>
        </div>
      ) : (
        <form
          noValidate
          className={styles.section}
          onSubmit={(e) => {
            e.preventDefault();
            void submit();
          }}
        >
          {!fixedProject && (
            <>
              <NativeSelect
                label={tr('marketplace.copy.project')}
                value={pickedProject}
                onChange={(e) => {
                  setPickedProject(e.currentTarget.value);
                  setPickedWorkflow('');
                  setOutputRef('');
                }}
                data={[{ value: '', label: tr('marketplace.copy.projectPlaceholder') }, ...(projects ?? []).map((p) => ({ value: p.id, label: p.name }))]}
                styles={{ input: { minHeight: 44 } }}
              />
              <NativeSelect
                label={t('workflow')}
                value={pickedWorkflow}
                onChange={(e) => setPickedWorkflow(e.currentTarget.value)}
                disabled={!pickedProject}
                data={[{ value: '', label: t('workflowPlaceholder') }, ...(workflows ?? []).map((w) => ({ value: w.id, label: w.name }))]}
                styles={{ input: { minHeight: 44 } }}
              />
            </>
          )}

          {checking && <p className={styles.hint} role="status">{t('checking')}</p>}
          {checkError !== undefined && checkError !== null && <p className={styles.error} role="alert">{marketplaceErrorMessage(tr, checkError)}</p>}

          {check && blocked && (
            <section className={styles.section} aria-labelledby="pub-refusals">
              <h3 className={styles.promptHeading} id="pub-refusals">{t('refusedTitle')}</h3>
              <p className={styles.hint}>{t('refusedHint')}</p>
              <ul className={styles.list} role="alert">
                {check.refusals.map((r) => (
                  <li key={`${r.code}-${r.stepOrder ?? ''}-${r.message}`}>
                    {r.stepOrder != null ? t('refusalAtStep', { order: r.stepOrder, message: refusalMessage(tr, r) }) : refusalMessage(tr, r)}
                  </li>
                ))}
              </ul>
            </section>
          )}

          {check && !blocked && (
            <>
              <section className={styles.section} aria-labelledby="pub-removed">
                <h3 className={styles.promptHeading} id="pub-removed">{t('removedTitle')}</h3>
                {check.removed.length === 0 ? (
                  <p className={styles.hint}>{t('removedNone')}</p>
                ) : (
                  <ul className={styles.list}>
                    {check.removed.map((r) => (
                      <li key={`${r.stepOrder}-${r.field}`}>{removedLine(tr, r)}</li>
                    ))}
                  </ul>
                )}
              </section>
              <section className={styles.section} aria-labelledby="pub-slots">
                <h3 className={styles.promptHeading} id="pub-slots">{t('slotsTitle')}</h3>
                {check.slots.length === 0 ? (
                  <p className={styles.hint}>{tr('marketplace.detail.needsNone')}</p>
                ) : (
                  <ul className={styles.list}>
                    {check.slots.map((s) => (
                      <li key={`${s.kind}-${s.stepOrder}-${s.label}`}>
                        {tr('marketplace.detail.slotItem', { kind: tr.msg(SLOT_KIND_LABELS[s.kind] ?? SLOT_KIND_LABELS.file), label: s.label })}
                      </li>
                    ))}
                  </ul>
                )}
              </section>
              <section className={styles.section} aria-labelledby="pub-prompts">
                <h3 className={styles.promptHeading} id="pub-prompts">{tr('marketplace.detail.promptsTitle')}</h3>
                <p className={styles.hint}>{t('promptsHint')}</p>
                {check.prompts.length === 0 ? (
                  <p className={styles.hint}>{tr('marketplace.detail.promptsNone')}</p>
                ) : (
                  check.prompts.map((p) => (
                    <div key={`${p.stepOrder}-${p.field}`} className={styles.section}>
                      <h4 className={styles.promptHeading}>{tr('marketplace.detail.promptHeading', { order: p.stepOrder, field: p.field })}</h4>
                      <pre className={styles.prompt} tabIndex={0} role="region" aria-label={tr('marketplace.detail.promptHeading', { order: p.stepOrder, field: p.field })}>{p.text}</pre>
                    </div>
                  ))
                )}
              </section>

              <NativeSelect
                label={t('output')}
                description={t('outputHint')}
                value={outputRef}
                onChange={(e) => setOutputRef(e.currentTarget.value)}
                data={[
                  { value: '', label: finished.length === 0 ? t('outputNone') : t('outputPlaceholder') },
                  ...finished.map((o) => ({
                    value: outputRefFor(o.stepResultId),
                    label: `${o.workflowName ?? o.fileName} · ${fm.date(o.producedAt, 'short')}`,
                  })),
                ]}
                required
                styles={{ input: { minHeight: 44 } }}
              />
              <TextInput label={t('name')} value={name} onChange={(e) => setName(e.currentTarget.value)} maxLength={NAME_MAX} required styles={{ input: { minHeight: 44 } }} />
              <Textarea
                label={t('description')}
                description={t('count', { count: description.length, max: DESCRIPTION_MAX })}
                value={description}
                onChange={(e) => setDescription(e.currentTarget.value.slice(0, DESCRIPTION_MAX))}
                maxLength={DESCRIPTION_MAX}
                rows={4}
              />
              <NativeSelect
                label={tr('marketplace.gallery.category')}
                value={category}
                onChange={(e) => setCategory(e.currentTarget.value)}
                data={CATEGORIES.map((c) => ({ value: c, label: tr.msg(categoryLabel(c) ?? { $msg: 'marketplace.gallery.all' }) }))}
                styles={{ input: { minHeight: 44 } }}
              />
              <NativeSelect
                label={tr('marketplace.gallery.language')}
                value={language}
                onChange={(e) => setLanguage(e.currentTarget.value)}
                data={PUBLISH_LANGUAGES.map((l) => ({ value: l, label: languageName(l, fm.locale) }))}
                styles={{ input: { minHeight: 44 } }}
              />
              <TextInput
                label={t('authorLabel')}
                description={t('authorLabelHint')}
                value={authorLabel}
                onChange={(e) => setAuthorLabel(e.currentTarget.value)}
                maxLength={NAME_MAX}
                required
                styles={{ input: { minHeight: 44 } }}
              />

              <section className={styles.section} aria-labelledby="pub-consent">
                <h3 className={styles.promptHeading} id="pub-consent">{t('consentTitle')}</h3>
                {consent ? (
                  <>
                    <div className={styles.prompt} tabIndex={0} role="region" aria-labelledby="pub-consent">{consent.text}</div>
                    <p className={styles.hint}>{consent.licenceSummary}</p>
                    <p className={styles.hint}>{t('consentVersion', { version: consent.version })}</p>
                  </>
                ) : (
                  <p className={styles.hint} role="status">{t('consentLoading')}</p>
                )}
                <Checkbox checked={accepted} onChange={(e) => setAcceptedVersion(e.currentTarget.checked && consent ? consent.version : null)} label={t('consentLabel')} disabled={!consent} required />
              </section>
            </>
          )}

          {error && <p className={styles.error} role="alert" id="pub-error">{error}</p>}
          <div className={styles.actions}>
            <button type="submit" className={styles.buttonPrimary} disabled={!canSend} aria-describedby={error ? 'pub-error' : undefined}>
              {busy ? t('submitting') : existing ? t('submitUpdate') : t('submit')}
            </button>
            <button type="button" className={styles.button} onClick={onClose}>
              {tr('marketplace.copy.cancel')}
            </button>
          </div>
        </form>
      )}
    </Modal>
  );
}
