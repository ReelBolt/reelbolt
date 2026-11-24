'use client';

import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Checkbox, Group, Modal, SimpleGrid, Stack, Switch, Text, Textarea } from '@mantine/core';
import { useDebouncedValue, useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { useSWRConfig } from 'swr';
import { CheckedImage } from '@/components/outputs/CheckedVideo';
import { estimateAvatarVariant, startAvatarVariant } from '@/lib/api/avatar-variants';
import { avatarMediaConsentPath, grantAvatarMediaConsent } from '@/lib/api/avatars';
import { projectFileUrl } from '@/lib/api/files';
import { defaultStatementLocale, objectStatements, statementLocales } from '@/lib/avatars/model';
import {
  VARIANT_INSTRUCTION_MAX, changeableReferences, descriptionFromInstruction, variantDefaultName, variantDescriptionProblem,
  variantInstructionProblem, variantLabelProblem,
} from '@/lib/avatars/variants';
import type { AvatarBuild, VariantEstimate } from '@/lib/types/avatar-builds';
import type { AvatarRequirements } from '@/lib/types/avatars';
import { useFormat, useT } from '@/i18n';
import { MediaConsentDialog } from '../MediaConsentDialog';
import { StatementBlock } from '../ReferenceParts';
import { needsMediaConsent, useVariantErrorMessage } from './variant-errors';
import { VariantFields, VariantOriginalLine, type VariantText } from './VariantFields';
import type { VariantTarget } from './variant-target';

const ESTIMATE_DEBOUNCE_MS = 350;

type EstimateState =
  | { status: 'idle' | 'loading' | 'failed' }
  | { status: 'ok'; estimate: VariantEstimate };

interface Props {
  projectId: string;
  requirements: AvatarRequirements;
  /** The object to make a variant of, or null while the dialog is closed. */
  target: VariantTarget | null;
  onClose: () => void;
  /** The build was started (`202`): the tab lists it under "Being made" and closes the dialog. */
  onStarted: (build: AvatarBuild) => void | Promise<void>;
}

/**
 * "Make with AI": asks the picture model to change an OBJECT's own pictures (and, when asked, to make its videos
 * again) into a variant (docs/design/avatar-objects.md "Avatar variants"). The person says what to change, names
 * the variant, picks which pictures to change, sees what it costs before starting, and confirms the object
 * statement. A person's pictures are never changed by a model here, so this dialog is for objects only.
 */
export function VariantAiModal(props: Props) {
  // A fresh dialog every time it opens, so a ticked statement is never carried to the next variant.
  if (!props.target) return null;
  return <VariantAiForm key={props.target.original.id} {...props} target={props.target} />;
}

function VariantAiForm({ projectId, requirements, target, onClose, onStarted }: Props & { target: VariantTarget }) {
  const t = useT('avatars.variants.ai');
  const tForm = useT('avatars.form');
  const tProb = useT('avatars.variants.problems');
  const fm = useFormat();
  const phone = Boolean(useMediaQuery('(max-width: 600px)'));
  const { original } = target;
  const { pictures, videos } = useMemo(() => changeableReferences(original), [original]);
  const statements = objectStatements(requirements);
  const locales = statementLocales(requirements.locales, statements);

  const [instruction, setInstruction] = useState('');
  const [label, setLabel] = useState('');
  // The description follows the instruction until the person writes their own.
  const [ownDescription, setOwnDescription] = useState<string | null>(null);
  const [ticked, setTicked] = useState<ReadonlySet<string>>(() => new Set(pictures.map((r) => r.id)));
  const [includeVideos, setIncludeVideos] = useState(false);
  const [locale, setLocale] = useState(() => defaultStatementLocale(fm.locale, requirements.locales, objectStatements(requirements)));
  const [attested, setAttested] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [consent, setConsent] = useState<{ busy: boolean; failed: boolean } | null>(null);
  const { mutate: globalMutate } = useSWRConfig();
  const [estimate, setEstimate] = useState<EstimateState>({ status: 'idle' });
  const describe = useVariantErrorMessage(() => null, 'Object');

  const description = ownDescription ?? descriptionFromInstruction(instruction);
  const text: VariantText = {
    label,
    description,
    setLabel,
    setDescription: setOwnDescription,
    ok: variantLabelProblem(label, target.takenLabels) === null && variantDescriptionProblem(description) === null,
    fields: { variantOfAvatarId: original.id, variantLabel: label.trim(), variantDescription: description.trim() },
  };
  const statement = statements[locale] ?? '';
  const remakeVideos = includeVideos && videos.length > 0;
  // Which of the original's files to change: the ticked pictures, and its videos when they are made again.
  const referenceIds = useMemo(
    () => [...pictures.filter((r) => ticked.has(r.id)).map((r) => r.id), ...(remakeVideos ? videos.map((r) => r.id) : [])],
    [pictures, videos, ticked, remakeVideos],
  );
  const pictureCount = pictures.filter((r) => ticked.has(r.id)).length;
  const instructionProblem = variantInstructionProblem(instruction);
  const canStart = instructionProblem === null && text.ok && pictureCount > 0 && attested && statement.length > 0 && !busy;

  const estimateKey = pictureCount > 0 ? `${referenceIds.join(',')}|${remakeVideos}` : '';
  const [debouncedKey] = useDebouncedValue(estimateKey, ESTIMATE_DEBOUNCE_MS);
  useEffect(() => {
    if (!debouncedKey) return undefined;
    let live = true;
    const [ids, withVideos] = debouncedKey.split('|');
    setEstimate({ status: 'loading' }); // eslint-disable-line react-hooks/set-state-in-effect -- the price is asked again whenever the choice changes
    estimateAvatarVariant(projectId, original.id, { referenceIds: ids.split(','), includeVideos: withVideos === 'true' })
      .then((answer) => { if (live) setEstimate({ status: 'ok', estimate: answer }); })
      .catch(() => { if (live) setEstimate({ status: 'failed' }); });
    return () => { live = false; };
  }, [debouncedKey, projectId, original.id]);
  // The price shown belongs to the choice on screen: while a newer choice is waiting, the older price is hidden.
  const shownEstimate: EstimateState = !estimateKey ? { status: 'idle' } : debouncedKey === estimateKey ? estimate : { status: 'loading' };

  const toggle = (id: string) => setTicked((prev) => {
    const next = new Set(prev);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  });

  const start = async () => {
    if (!canStart) return;
    setBusy(true);
    setError(null);
    const name = variantDefaultName(original, label, requirements.limits.maxNameLength);
    try {
      const started = await startAvatarVariant(projectId, original.id, {
        instruction: instruction.trim(),
        variantLabel: text.fields.variantLabel,
        variantDescription: text.fields.variantDescription,
        referenceIds,
        includeVideos: remakeVideos,
        consentStatement: statement,
        consentLocale: locale,
      });
      notifications.show({ color: 'blue', message: t('started', { name }) });
      await onStarted({ jobId: started.jobId, state: started.state, kind: 'variant', name });
    } catch (e) {
      // The project's permission to send media is not recorded yet: ask for it, then start again.
      if (needsMediaConsent(e)) setConsent({ busy: false, failed: false });
      else setError(describe(e, t('failed')));
    } finally {
      setBusy(false);
    }
  };

  /** The explicit yes: record the project's permission, then start what was waiting for it. */
  const grantConsent = async () => {
    setConsent({ busy: true, failed: false });
    try {
      await grantAvatarMediaConsent(projectId);
      // The Avatars tab and the editors read the same answer.
      void globalMutate(avatarMediaConsentPath(projectId));
    } catch {
      setConsent({ busy: false, failed: true });
      return;
    }
    setConsent(null);
    await start();
  };

  return (
    <>
      <Modal opened onClose={onClose} title={t('title', { name: original.name })} size="lg" fullScreen={phone} radius="md">
        <Stack gap="md" data-testid="variant-ai-form">
          <VariantOriginalLine projectId={projectId} original={original} />
          <Text size="sm" c="dimmed">{t('intro')}</Text>

          <Textarea
            label={t('instruction')} description={t('instructionHint')} placeholder={t('instructionPlaceholder')} required
            autosize minRows={2} maxRows={6} maxLength={VARIANT_INSTRUCTION_MAX}
            value={instruction} onChange={(e) => setInstruction(e.currentTarget.value)}
            error={instructionProblem === 'tooLong' ? tProb('instruction.tooLong', { max: VARIANT_INSTRUCTION_MAX }) : undefined}
            data-testid="variant-instruction"
          />

          <VariantFields target={target} text={text} />

          <Stack gap={6}>
            <Text size="sm" fw={600} id="variant-pictures-label">{t('pictures.title')}</Text>
            <Text size="sm" c="dimmed">{t('pictures.hint')}</Text>
            <SimpleGrid cols={{ base: 2, xs: 3, sm: 4 }} spacing="xs" role="group" aria-labelledby="variant-pictures-label" data-testid="variant-pictures">
              {pictures.map((r) => {
                const name = r.fileName ?? t('pictures.unnamed');
                return (
                  <label key={r.id} style={{ display: 'block', cursor: 'var(--rb-cursor-pointer)' }}>
                    <div style={{ aspectRatio: '1 / 1', borderRadius: 8, overflow: 'hidden', background: 'var(--mantine-color-default-hover)', opacity: ticked.has(r.id) ? 1 : 0.5 }}>
                      <CheckedImage src={projectFileUrl(projectId, r.projectFileId)} alt="" loading="lazy" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
                    </div>
                    <Checkbox
                      mt={6} size="sm" checked={ticked.has(r.id)} onChange={() => toggle(r.id)}
                      label={<Text size="sm" truncate maw={140}>{name}</Text>} aria-label={t('pictures.select', { name })}
                    />
                  </label>
                );
              })}
            </SimpleGrid>
            {pictureCount === 0 && <Text size="sm" c="red" data-testid="variant-no-pictures">{t('pictures.none')}</Text>}
          </Stack>

          {videos.length > 0 && (
            <Stack gap={4}>
              <Switch
                label={t('videos.label', { count: videos.length })} checked={includeVideos}
                onChange={(e) => setIncludeVideos(e.currentTarget.checked)} data-testid="variant-include-videos"
              />
              <Text size="sm" c="dimmed">{t('videos.note')}</Text>
            </Stack>
          )}

          <EstimateLine state={shownEstimate} />

          <StatementBlock
            title={tForm('objectStatementTitle')} testId="variant-object-statement"
            statement={statement} locale={locale} locales={locales} onLocale={setLocale}
            attested={attested} onAttested={setAttested}
          />
          <Text size="sm" c="dimmed">{t('sendsNote')}</Text>

          {error && <Alert color="red" variant="light" data-testid="variant-ai-error">{error}</Alert>}

          <Group justify="space-between" wrap="wrap" gap="xs">
            <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
            <Button onClick={() => void start()} disabled={!canStart} loading={busy} style={{ minHeight: 44 }} data-testid="variant-ai-start">
              {t('start')}
            </Button>
          </Group>
        </Stack>
      </Modal>
      <MediaConsentDialog
        opened={consent !== null}
        busy={consent?.busy ?? false}
        failed={consent?.failed ?? false}
        onCancel={() => setConsent(null)}
        onConfirm={() => void grantConsent()}
      />
    </>
  );
}

/**
 * What the variant costs, as the server worked it out: nothing is computed here. With billing off there is no
 * price line; when the server cannot price it, its reason is said in words.
 */
function EstimateLine({ state }: { state: EstimateState }) {
  const t = useT('avatars.variants.ai.estimate');
  if (state.status !== 'ok') {
    if (state.status === 'idle') return null;
    return <Text size="sm" c="dimmed" data-testid="variant-estimate">{state.status === 'loading' ? t('loading') : t('failed')}</Text>;
  }
  const e = state.estimate;
  if (!e.billingEnabled) return null;
  if (e.unavailableReason) {
    const key = `unavailable.${e.unavailableReason}`;
    return <Text size="sm" c="orange" data-testid="variant-estimate">{t.has(key) ? t(key) : t('unavailable.other')}</Text>;
  }
  if (typeof e.credits !== 'number') return null;
  const high = typeof e.creditsHigh === 'number' && e.creditsHigh > e.credits ? e.creditsHigh : null;
  return (
    <Stack gap={2} data-testid="variant-estimate">
      <Text size="sm" fw={600}>
        {high === null ? t('about', { credits: e.credits }) : t('aboutUpTo', { credits: e.credits, high })}
      </Text>
      <Text size="sm" c="dimmed">{t('covers', { pictures: e.pictures ?? 0, videos: e.videos ?? 0 })}</Text>
      {e.affordable === false && (
        <Text size="sm" c="orange" data-testid="variant-estimate-short">
          {typeof e.balanceCredits === 'number' ? t('notEnoughBalance', { balance: e.balanceCredits }) : t('notEnough')}
        </Text>
      )}
    </Stack>
  );
}
