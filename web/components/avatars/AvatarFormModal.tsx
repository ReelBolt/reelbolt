'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import {
  Alert, Button, Checkbox, Group, List, Modal, NumberInput, Radio, Select, Stack, Stepper, Text, TextInput, Textarea,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { createAvatar, replaceAvatarReferences } from '@/lib/api/avatars';
import {
  defaultConsentLocale, defaultRole, eligibleReferenceFiles, kindOfMime, selectionCounts, selectionProblems, statementLocales,
  type PickedReference,
} from '@/lib/avatars/model';
import { variantDefaultName } from '@/lib/avatars/variants';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useProjectVoices } from '@/lib/hooks/use-voices';
import {
  ROLES_BY_KIND, type AvatarConsentBasis, type AvatarDisclosureLabel, type AvatarReferenceRole, type AvatarRequirements,
  type ProjectAvatar,
} from '@/lib/types/avatars';
import { formatBytes } from '@/lib/utils/bytes';
import { useFormat, useT } from '@/i18n';
import { useAvatarName } from './avatar-names';
import { ReferenceFilePicker, StatementBlock } from './ReferenceParts';
import { useVariantErrorMessage } from './variants/variant-errors';
import { VariantFields, VariantOriginalLine, useVariantText } from './variants/VariantFields';
import type { VariantTarget } from './variants/variant-target';

interface Props {
  projectId: string;
  requirements: AvatarRequirements;
  opened: boolean;
  /** Null creates a new avatar of a person; an avatar changes its reference files (a re-attestation). */
  avatar: ProjectAvatar | null;
  /**
   * Set (with no `avatar`) to add a VARIANT of a person from other files. These are new files of the person, so
   * the consent statement, the adult tick, the keep-for period and the files are asked again; the person's name
   * and whose likeness it is come from the original and cannot be changed.
   */
  variant?: VariantTarget | null;
  onClose: () => void;
  onSaved: () => void | Promise<void>;
}

/**
 * Creating an avatar of a PERSON, or changing its files, in three steps (docs/design/avatars.md "Web"):
 * (1) the files and what each shows, (2) the person and the consent statement, (3) how long to keep
 * it, its voice and its AI label. The statement is the server's CURRENT one, shown verbatim in the
 * language the user picks and sent back unchanged; nothing is submitted until every consent field is
 * complete. An object has its own, shorter form (`ObjectFormModal`).
 * With `variant` the same three steps add a variant of a person (the same person with another look): a complete
 * avatar of its own, grouped under its original, with the consent given again for the new files.
 */
export function AvatarFormModal(props: Props) {
  // A fresh form every time it opens, so a half-filled consent is never carried to the next avatar.
  if (!props.opened) return null;
  return <AvatarForm key={props.avatar?.id ?? (props.variant ? `variant-of-${props.variant.original.id}` : 'new')} {...props} />;
}

function AvatarForm({ projectId, requirements, avatar, variant = null, onClose, onSaved }: Props) {
  const t = useT('avatars.form');
  const tVar = useT('avatars.variants.form');
  const tCard = useT('avatars.card');
  const fm = useFormat();
  const phone = useMediaQuery('(max-width: 600px)');
  const replacing = avatar !== null;
  const target = replacing ? null : variant;
  const original = target?.original ?? null;
  const variantText = useVariantText(target);
  const shownName = useAvatarName();
  const limits = requirements.limits;
  const { data: files } = useProjectFiles(projectId);
  const { data: voices } = useProjectVoices(projectId);

  const [step, setStep] = useState(0);
  const [picked, setPicked] = useState<PickedReference[]>([]);
  // A variant is the same person: the name and whose likeness it is are the original's.
  const [subject, setSubject] = useState((avatar ?? original)?.consent.subjectName ?? '');
  const [basis, setBasis] = useState<AvatarConsentBasis | null>(original?.consent.basis ?? null);
  const [documentId, setDocumentId] = useState<string | null>(null);
  const [publicFigure, setPublicFigure] = useState<'yes' | 'no' | null>(null);
  const [adult, setAdult] = useState(false);
  const [locale, setLocale] = useState(() => defaultConsentLocale(fm.locale, requirements));
  const [attested, setAttested] = useState(false);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [retentionDays, setRetentionDays] = useState<number>(limits.defaultRetentionDays);
  const [voiceId, setVoiceId] = useState<string | null>(original?.projectVoiceSelectable !== false ? original?.projectVoiceId ?? null : null);
  const [disclosure, setDisclosure] = useState<AvatarDisclosureLabel>(original?.disclosureLabel ?? 'Visible');
  const [disclosesOtherwise, setDisclosesOtherwise] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const eligible = useMemo(() => eligibleReferenceFiles(files ?? [], requirements), [files, requirements]);
  const fileName = useMemo(() => {
    const names = new Map((files ?? []).map((f) => [f.id, f.originalFileName]));
    return (id: string) => names.get(id) ?? null;
  }, [files]);
  const describe = useVariantErrorMessage(fileName, 'Person', target !== null);

  // Changing files starts from the avatar's current list (files that are gone are left out).
  const [seeded, setSeeded] = useState(!replacing);
  useEffect(() => {
    if (seeded || !avatar || !files) return;
    const byId = new Map(files.map((f) => [f.id, f]));
    setPicked(avatar.references // eslint-disable-line react-hooks/set-state-in-effect -- seed once the files have loaded
      .filter((r) => r.state !== 'missing' && byId.has(r.projectFileId))
      .map((r) => ({ fileId: r.projectFileId, kind: r.kind, role: r.role, sizeBytes: byId.get(r.projectFileId)!.sizeBytes })));
    setSeeded(true);
  }, [seeded, avatar, files]);

  const statement = requirements.statements[locale] ?? '';
  const locales = statementLocales(requirements.locales, requirements.statements);
  const problems = selectionProblems(picked, limits);
  const counts = selectionCounts(picked);
  const filesOk = problems.length === 0;
  const subjectOk = subject.trim().length > 0 && subject.trim().length <= limits.maxSubjectNameLength;
  const publicFigureBlocked = publicFigure === 'yes' && basis === 'WrittenPermission';
  const consentOk = replacing || target
    ? adult && attested && statement.length > 0
    : subjectOk && basis !== null && publicFigure !== null && !publicFigureBlocked && adult && attested && statement.length > 0;
  const retentionOk = Number.isInteger(retentionDays) && retentionDays >= limits.minRetentionDays && retentionDays <= limits.maxRetentionDays;
  const nameOk = replacing || (target ? variantText.ok : name.trim().length > 0 && name.trim().length <= limits.maxNameLength);
  const disclosureOk = disclosure === 'Visible' || disclosesOtherwise;
  const canSubmit = filesOk && consentOk && retentionOk && nameOk && disclosureOk && !saving;

  const selectableVoices = useMemo(
    () => (voices ?? []).filter((v) => v.selectable).map((v) => ({ value: v.id, label: v.name })),
    [voices],
  );
  const pickedIds = new Set(picked.map((p) => p.fileId));
  const documentOptions = useMemo(
    () => (files ?? []).filter((f) => !pickedIds.has(f.id)).map((f) => ({ value: f.id, label: f.originalFileName })),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- pickedIds is derived from picked
    [files, picked],
  );

  const toggle = (fileId: string) => {
    const file = eligible.find((f) => f.id === fileId);
    if (!file) return;
    setPicked((prev) => {
      if (prev.some((p) => p.fileId === fileId)) return prev.filter((p) => p.fileId !== fileId);
      const kind = kindOfMime(file.mimeType, requirements)!;
      return [...prev, { fileId, kind, role: defaultRole(kind), sizeBytes: file.sizeBytes }];
    });
    if (documentId === fileId) setDocumentId(null);
  };
  const setRole = (fileId: string, role: AvatarReferenceRole) =>
    setPicked((prev) => prev.map((p) => (p.fileId === fileId ? { ...p, role } : p)));

  const submit = async () => {
    if (!canSubmit) return;
    setSaving(true);
    setError(null);
    const references = picked.map((p) => ({ projectFileId: p.fileId, role: p.role }));
    try {
      if (avatar) {
        await replaceAvatarReferences(projectId, avatar.id, {
          consentStatement: statement, consentLocale: locale, subjectIsAdult: adult, references, retentionDays,
        });
        notifications.show({ color: 'green', message: t('replaced', { name: avatar.name }) });
      } else if (original) {
        // A variant is a complete avatar of its own: the normal create with the consent given again, plus what
        // groups it under its original. The person's name and basis are the original's (the server copies them).
        const shown = variantDefaultName(original, variantText.fields.variantLabel, limits.maxNameLength);
        await createAvatar(projectId, {
          name: shown,
          consentStatement: statement,
          consentLocale: locale,
          consentSubjectName: original.consent.subjectName,
          consentBasis: original.consent.basis,
          subjectIsAdult: adult,
          consentDocumentFileId: documentId,
          references,
          retentionDays,
          // The original's voice is kept only while it is one of the voices that can be chosen.
          projectVoiceId: selectableVoices.some((v) => v.value === voiceId) ? voiceId : null,
          disclosureLabel: disclosure,
          disclosesOtherwise: disclosure === 'MetadataOnly' && disclosesOtherwise,
          ...variantText.fields,
        });
        notifications.show({ color: 'green', message: tVar('created', { name: shown }) });
      } else {
        await createAvatar(projectId, {
          name: name.trim(),
          description: description.trim() || null,
          consentStatement: statement,
          consentLocale: locale,
          consentSubjectName: subject.trim(),
          consentBasis: basis!,
          subjectIsAdult: adult,
          subjectIsPublicFigure: publicFigure === 'yes',
          consentDocumentFileId: documentId,
          references,
          retentionDays,
          projectVoiceId: voiceId,
          disclosureLabel: disclosure,
          disclosesOtherwise: disclosure === 'MetadataOnly' && disclosesOtherwise,
        });
        notifications.show({ color: 'green', message: t('created', { name: name.trim() }) });
      }
      await onSaved();
    } catch (e) {
      setError(describe(e, target ? tVar('failed') : t('failed')));
    } finally {
      setSaving(false);
    }
  };

  const nextDisabled = (step === 0 && !filesOk) || (step === 1 && !consentOk);

  return (
    <Modal
      opened onClose={onClose} size="xl" fullScreen={Boolean(phone)} radius="md"
      title={replacing ? t('titleReplace', { name: shownName(avatar!) }) : original ? tVar('title', { name: original.name }) : t('titleCreate')}
    >
      <Stack gap="md" data-testid="avatar-form">
        {replacing && <Text size="sm" c="dimmed">{t('replaceIntro')}</Text>}
        {original && (
          <>
            <VariantOriginalLine projectId={projectId} original={original} />
            <Text size="sm" c="dimmed">{tVar('personIntro')}</Text>
          </>
        )}
        <Stepper active={step} onStepClick={(s) => { if (s < step) setStep(s); }} size="sm" orientation={phone ? 'vertical' : 'horizontal'} wrap={false} allowNextStepsSelect={false}>
          <Stepper.Step label={t('steps.files')} description={phone ? t('steps.filesHint') : undefined}>
            <Stack gap="sm" mt="sm">
              {/* On a desktop the hint sits in the body, so the three steps fit on one line in every language. */}
              {!phone && <Text size="sm" c="dimmed" data-testid="avatar-step-hint">{t('steps.filesHint')}</Text>}
              <Alert variant="light" color="blue" title={t('guidanceTitle')}>
                <List size="sm" spacing={2}>
                  <List.Item>{t('guidance.face')}</List.Item>
                  <List.Item>{t('guidance.light')}</List.Item>
                  <List.Item>{t('guidance.video')}</List.Item>
                  <List.Item>{t('guidance.audio')}</List.Item>
                </List>
              </Alert>
              <Text size="sm" c="dimmed">
                {t('limits', {
                  images: limits.maxImages, videos: limits.maxVideos, audio: limits.maxAudio,
                  imageSize: formatBytes(limits.maxImageBytes, fm), videoSize: formatBytes(limits.maxVideoBytes, fm),
                  audioSize: formatBytes(limits.maxAudioBytes, fm), minSide: limits.minImageSidePx,
                  minSec: Math.round(limits.minVideoMs / 1000), maxSec: Math.round(limits.maxVideoMs / 1000),
                })}
              </Text>
              {(requirements.moderationEnabled || requirements.csamScanningEnabled) && (
                <Text size="sm" c="dimmed">{t('checkingNote')}</Text>
              )}
              {eligible.length === 0 ? (
                <Alert variant="light" color="gray" title={t('noFilesTitle')}>
                  <Stack gap="xs" align="flex-start">
                    <Text size="sm">{t('noFilesBody')}</Text>
                    <Button component={Link} href={`/projects/${encodeURIComponent(projectId)}?tab=files`} variant="light" style={{ minHeight: 44 }}>{t('openFiles')}</Button>
                  </Stack>
                </Alert>
              ) : (
                <ReferenceFilePicker
                  files={eligible} picked={picked} requirements={requirements} limits={limits}
                  rolesOf={(kind) => ROLES_BY_KIND[kind]} onToggle={toggle} onRole={setRole}
                  maxHeight={phone ? undefined : 340}
                />
              )}
              <Text size="sm" data-testid="avatar-counts">
                {t('counts', { images: counts.Image, maxImages: limits.maxImages, videos: counts.Video, maxVideos: limits.maxVideos, audio: counts.Audio, maxAudio: limits.maxAudio })}
              </Text>
              {picked.length > 0 && problems.map((p) => (
                <Text key={p} size="sm" c="red">{t(`problems.${p}`, { images: limits.maxImages, videos: limits.maxVideos, audio: limits.maxAudio })}</Text>
              ))}
            </Stack>
          </Stepper.Step>

          <Stepper.Step label={t('steps.person')} description={phone ? t('steps.personHint') : undefined}>
            <Stack gap="sm" mt="sm">
              {/* On a desktop the hint sits in the body, so the three steps fit on one line in every language. */}
              {!phone && <Text size="sm" c="dimmed" data-testid="avatar-step-hint">{t('steps.personHint')}</Text>}
              {replacing ? (
                <Text size="sm">{t('subjectFixed', { name: avatar!.consent.subjectName })}</Text>
              ) : original ? (
                <Stack gap={4} data-testid="variant-person-fixed">
                  <Text size="sm">
                    {t('subjectFixed', { name: original.consent.subjectName })}
                    {' · '}
                    {original.consent.basis === 'Self' ? tCard('basisSelf') : tCard('basisWritten')}
                  </Text>
                  <Text size="sm" c="dimmed">{tVar('personFixedNote')}</Text>
                  {original.consent.basis === 'WrittenPermission' && (
                    <Select
                      label={t('document')} description={t('documentHint')} clearable searchable
                      data={documentOptions} value={documentId} onChange={setDocumentId}
                    />
                  )}
                </Stack>
              ) : (
                <>
                  <TextInput
                    label={t('subjectName')} description={t('subjectNameHint')} required maxLength={limits.maxSubjectNameLength}
                    value={subject} onChange={(e) => setSubject(e.currentTarget.value)}
                  />
                  <Radio.Group label={t('basisLabel')} required value={basis} onChange={(v) => setBasis(v as AvatarConsentBasis)}>
                    <Stack gap={6} mt={6}>
                      <Radio value="Self" label={t('basisSelf')} />
                      <Radio value="WrittenPermission" label={t('basisWritten')} />
                    </Stack>
                  </Radio.Group>
                  {basis === 'WrittenPermission' && (
                    <Select
                      label={t('document')} description={t('documentHint')} clearable searchable
                      data={documentOptions} value={documentId} onChange={setDocumentId}
                    />
                  )}
                  <Radio.Group label={t('publicFigure')} description={t('publicFigureHint')} required value={publicFigure} onChange={(v) => setPublicFigure(v as 'yes' | 'no')}>
                    <Group gap="md" mt={6}>
                      <Radio value="no" label={t('no')} />
                      <Radio value="yes" label={t('yes')} />
                    </Group>
                  </Radio.Group>
                  {publicFigureBlocked && <Alert color="red" variant="light">{t('publicFigureBlocked')}</Alert>}
                </>
              )}
              <Checkbox label={t('adult')} checked={adult} onChange={(e) => setAdult(e.currentTarget.checked)} />
              <StatementBlock
                title={t('statementTitle')} testId="avatar-consent-statement"
                about={<Text size="sm" c="dimmed">{t('statementAbout', { name: ((avatar ?? original)?.consent.subjectName ?? subject.trim()) || t('statementNoName') })}</Text>}
                statement={statement} locale={locale} locales={locales} onLocale={setLocale}
                attested={attested} onAttested={setAttested}
              />
            </Stack>
          </Stepper.Step>

          <Stepper.Step label={t('steps.settings')} description={phone ? t('steps.settingsHint') : undefined}>
            <Stack gap="sm" mt="sm">
              {/* On a desktop the hint sits in the body, so the three steps fit on one line in every language. */}
              {!phone && <Text size="sm" c="dimmed" data-testid="avatar-step-hint">{t('steps.settingsHint')}</Text>}
              {target && <VariantFields target={target} text={variantText} />}
              {!replacing && !target && (
                <>
                  <TextInput
                    label={t('name')} placeholder={t('namePlaceholder')} required maxLength={limits.maxNameLength}
                    value={name} onChange={(e) => setName(e.currentTarget.value)}
                  />
                  <Textarea
                    label={t('description')} rows={3} maxLength={limits.maxDescriptionLength}
                    value={description} onChange={(e) => setDescription(e.currentTarget.value)}
                  />
                </>
              )}
              <NumberInput
                label={t('keepFor')} description={t('keepForHint', { min: limits.minRetentionDays, max: limits.maxRetentionDays })}
                min={limits.minRetentionDays} max={limits.maxRetentionDays} allowDecimal={false} required
                value={retentionDays} onChange={(v) => setRetentionDays(typeof v === 'number' ? v : Number.NaN)}
                error={retentionOk ? undefined : t('keepForInvalid', { min: limits.minRetentionDays, max: limits.maxRetentionDays })}
              />
              {!replacing && (
                <>
                  {selectableVoices.length > 0 ? (
                    <Select
                      label={t('voice')} description={t('voiceHint')} clearable placeholder={t('voiceNone')}
                      data={selectableVoices} value={voiceId} onChange={setVoiceId}
                    />
                  ) : (
                    <Text size="sm" c="dimmed">
                      {t('noVoices')}{' '}
                      <Link href={`/projects/${encodeURIComponent(projectId)}?tab=voices`}>{t('cloneVoiceFirst')}</Link>
                    </Text>
                  )}
                  <Radio.Group label={t('disclosure')} description={t('disclosureHint')} value={disclosure} onChange={(v) => setDisclosure(v as AvatarDisclosureLabel)}>
                    <Stack gap={6} mt={6}>
                      <Radio value="Visible" label={t('disclosureVisible')} description={t('disclosureVisibleHint')} />
                      <Radio value="MetadataOnly" label={t('disclosureMetadata')} description={t('disclosureMetadataHint')} />
                    </Stack>
                  </Radio.Group>
                  {disclosure === 'MetadataOnly' && (
                    <Checkbox label={t('disclosesOtherwise')} checked={disclosesOtherwise} onChange={(e) => setDisclosesOtherwise(e.currentTarget.checked)} />
                  )}
                </>
              )}
            </Stack>
          </Stepper.Step>
        </Stepper>

        {error && <Alert color="red" variant="light" data-testid="avatar-form-error">{error}</Alert>}

        <Group justify="space-between" wrap="wrap" gap="xs">
          <Button variant="default" onClick={step === 0 ? onClose : () => setStep(step - 1)} style={{ minHeight: 44 }}>
            {step === 0 ? t('cancel') : t('back')}
          </Button>
          {step < 2 ? (
            <Button onClick={() => setStep(step + 1)} disabled={nextDisabled} style={{ minHeight: 44 }}>{t('next')}</Button>
          ) : (
            <Button onClick={() => void submit()} disabled={!canSubmit} loading={saving} style={{ minHeight: 44 }} data-testid="avatar-submit">
              {replacing ? t('saveFiles') : target ? tVar('create') : t('create')}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
