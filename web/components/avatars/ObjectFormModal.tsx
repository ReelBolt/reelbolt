'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { Alert, Button, Group, List, Modal, Stack, Stepper, Text, TextInput, Textarea } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { createAvatar, replaceAvatarReferences } from '@/lib/api/avatars';
import {
  defaultObjectRole, defaultStatementLocale, eligibleReferenceFiles, kindOfMime, objectFileLimits, objectStatements,
  rolesFor, selectionCounts, selectionProblems, statementLocales, type PickedReference,
} from '@/lib/avatars/model';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { variantDefaultName } from '@/lib/avatars/variants';
import type { AvatarReferenceRole, AvatarRequirements, ProjectAvatar } from '@/lib/types/avatars';
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
  /** Null adds a new object; an object changes its files (the statement is confirmed again). */
  avatar: ProjectAvatar | null;
  /**
   * Set (with no `avatar`) to add a VARIANT of an object from other files: the original is fixed and shown at the
   * top, and the form asks for the variant's label and what sets it apart instead of a name.
   */
  variant?: VariantTarget | null;
  onClose: () => void;
  onSaved: () => void | Promise<void>;
}

/**
 * Adding an OBJECT (a product, a vehicle, a building: anything that is not a person), or changing its files, in
 * two steps (docs/design/avatar-objects.md): (1) the pictures and short videos and what each shows, (2) its name
 * and the object statement. No person, no consent of a person, no keep-for period, no voice, no AI label choice.
 * The statement is the server's, shown verbatim in the language the user picks and sent back unchanged.
 * With `variant` the same two steps add a variant of an object (the same car in another colour): a complete
 * object of its own, grouped under its original.
 */
export function ObjectFormModal(props: Props) {
  // A fresh form every time it opens, so a ticked statement is never carried to the next object.
  if (!props.opened) return null;
  return <ObjectForm key={props.avatar?.id ?? (props.variant ? `variant-of-${props.variant.original.id}` : 'new')} {...props} />;
}

function ObjectForm({ projectId, requirements, avatar, variant = null, onClose, onSaved }: Props) {
  const t = useT('avatars.form');
  const tVar = useT('avatars.variants.form');
  const fm = useFormat();
  const phone = useMediaQuery('(max-width: 600px)');
  const replacing = avatar !== null;
  const target = replacing ? null : variant;
  const variantText = useVariantText(target);
  const shownName = useAvatarName();
  const limits = useMemo(() => objectFileLimits(requirements), [requirements]);
  const statements = objectStatements(requirements);
  const locales = statementLocales(requirements.locales, statements);
  const { data: files } = useProjectFiles(projectId);

  const [step, setStep] = useState(0);
  const [picked, setPicked] = useState<PickedReference[]>([]);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [locale, setLocale] = useState(() => defaultStatementLocale(fm.locale, requirements.locales, objectStatements(requirements)));
  const [attested, setAttested] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const eligible = useMemo(() => eligibleReferenceFiles(files ?? [], requirements, 'Object'), [files, requirements]);
  const fileName = useMemo(() => {
    const names = new Map((files ?? []).map((f) => [f.id, f.originalFileName]));
    return (id: string) => names.get(id) ?? null;
  }, [files]);
  const describe = useVariantErrorMessage(fileName, 'Object', target !== null);

  // Changing files starts from the object's current list (files that are gone are left out).
  const [seeded, setSeeded] = useState(!replacing);
  useEffect(() => {
    if (seeded || !avatar || !files) return;
    const byId = new Map(files.map((f) => [f.id, f]));
    setPicked(avatar.references // eslint-disable-line react-hooks/set-state-in-effect -- seed once the files have loaded
      .filter((r) => r.state !== 'missing' && r.kind !== 'Audio' && byId.has(r.projectFileId))
      .map((r) => ({ fileId: r.projectFileId, kind: r.kind, role: r.role, sizeBytes: byId.get(r.projectFileId)!.sizeBytes })));
    setSeeded(true);
  }, [seeded, avatar, files]);

  const statement = statements[locale] ?? '';
  const problems = selectionProblems(picked, limits);
  const counts = selectionCounts(picked);
  const filesOk = problems.length === 0;
  const nameOk = replacing || (target ? variantText.ok : name.trim().length > 0 && name.trim().length <= limits.maxNameLength);
  const canSubmit = filesOk && nameOk && attested && statement.length > 0 && !saving;

  const toggle = (fileId: string) => {
    const file = eligible.find((f) => f.id === fileId);
    const kind = file ? kindOfMime(file.mimeType, requirements) : null;
    if (!file || !kind) return;
    setPicked((prev) => (prev.some((p) => p.fileId === fileId)
      ? prev.filter((p) => p.fileId !== fileId)
      : [...prev, { fileId, kind, role: defaultObjectRole(kind, prev, requirements), sizeBytes: file.sizeBytes }]));
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
        // An object sends its own statement again and nothing a person's avatar carries (no keep-for period).
        await replaceAvatarReferences(projectId, avatar.id, { consentStatement: statement, consentLocale: locale, references });
        notifications.show({ color: 'green', message: t('objectReplaced', { name: avatar.name }) });
      } else if (target) {
        // A variant is a complete object of its own: the normal create, plus what groups it under its original.
        const shown = variantDefaultName(target.original, variantText.fields.variantLabel, limits.maxNameLength);
        await createAvatar(projectId, {
          subjectKind: 'Object',
          name: shown,
          consentStatement: statement,
          consentLocale: locale,
          references,
          ...variantText.fields,
        });
        notifications.show({ color: 'green', message: tVar('created', { name: shown }) });
      } else {
        const about = description.trim();
        await createAvatar(projectId, {
          subjectKind: 'Object',
          name: name.trim(),
          ...(about ? { description: about } : {}),
          consentStatement: statement,
          consentLocale: locale,
          references,
        });
        notifications.show({ color: 'green', message: t('objectCreated', { name: name.trim() }) });
      }
      await onSaved();
    } catch (e) {
      setError(describe(e, target ? tVar('failed') : t('objectFailed')));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      opened onClose={onClose} size="xl" fullScreen={Boolean(phone)} radius="md"
      title={replacing ? t('titleReplace', { name: shownName(avatar!) }) : target ? tVar('title', { name: target.original.name }) : t('objectTitleCreate')}
    >
      <Stack gap="md" data-testid="object-form">
        {replacing && <Text size="sm" c="dimmed">{t('objectReplaceIntro')}</Text>}
        {target && (
          <>
            <VariantOriginalLine projectId={projectId} original={target.original} />
            <Text size="sm" c="dimmed">{tVar('objectIntro')}</Text>
          </>
        )}
        <Stepper active={step} onStepClick={(s) => { if (s < step) setStep(s); }} size="sm" orientation={phone ? 'vertical' : 'horizontal'} wrap={false} allowNextStepsSelect={false}>
          <Stepper.Step label={t('steps.files')} description={phone ? t('steps.objectFilesHint') : undefined}>
            <Stack gap="sm" mt="sm">
              {!phone && <Text size="sm" c="dimmed" data-testid="avatar-step-hint">{t('steps.objectFilesHint')}</Text>}
              <Alert variant="light" color="blue" title={t('guidanceTitle')}>
                <List size="sm" spacing={2}>
                  <List.Item>{t('objectGuidance.angles')}</List.Item>
                  <List.Item>{t('objectGuidance.background')}</List.Item>
                  <List.Item>{t('objectGuidance.alone')}</List.Item>
                  <List.Item>{t('objectGuidance.video')}</List.Item>
                </List>
              </Alert>
              <Text size="sm" c="dimmed">
                {t('objectLimits', {
                  images: limits.maxImages, videos: limits.maxVideos,
                  imageSize: formatBytes(limits.maxImageBytes, fm), videoSize: formatBytes(limits.maxVideoBytes, fm),
                  minSide: limits.minImageSidePx,
                  minSec: Math.round(limits.minVideoMs / 1000), maxSec: Math.round(limits.maxVideoMs / 1000),
                })}
              </Text>
              {(requirements.moderationEnabled || requirements.csamScanningEnabled) && (
                <Text size="sm" c="dimmed">{t('checkingNote')}</Text>
              )}
              {eligible.length === 0 ? (
                <Alert variant="light" color="gray" title={t('noFilesTitle')}>
                  <Stack gap="xs" align="flex-start">
                    <Text size="sm">{t('objectNoFilesBody')}</Text>
                    <Button component={Link} href={`/projects/${encodeURIComponent(projectId)}?tab=files`} variant="light" style={{ minHeight: 44 }}>{t('openFiles')}</Button>
                  </Stack>
                </Alert>
              ) : (
                <ReferenceFilePicker
                  files={eligible} picked={picked} requirements={requirements} limits={limits}
                  rolesOf={(kind) => rolesFor('Object', kind, requirements)} onToggle={toggle} onRole={setRole}
                  maxHeight={phone ? undefined : 340}
                />
              )}
              <Text size="sm" data-testid="avatar-counts">
                {t('objectCounts', { images: counts.Image, maxImages: limits.maxImages, videos: counts.Video, maxVideos: limits.maxVideos })}
              </Text>
              {picked.length > 0 && problems.map((p) => (
                <Text key={p} size="sm" c="red">
                  {t.has(`objectProblems.${p}`) ? t(`objectProblems.${p}`) : t(`problems.${p}`, { images: limits.maxImages, videos: limits.maxVideos, audio: limits.maxAudio })}
                </Text>
              ))}
            </Stack>
          </Stepper.Step>

          <Stepper.Step
            label={replacing ? t('steps.objectConfirm') : target ? tVar('step') : t('steps.objectName')}
            description={phone ? (replacing ? t('steps.objectConfirmHint') : target ? tVar('stepHint') : t('steps.objectNameHint')) : undefined}
          >
            <Stack gap="sm" mt="sm">
              {!phone && <Text size="sm" c="dimmed" data-testid="avatar-step-hint">{replacing ? t('steps.objectConfirmHint') : target ? tVar('stepHint') : t('steps.objectNameHint')}</Text>}
              {target && <VariantFields target={target} text={variantText} />}
              {!replacing && !target && (
                <>
                  <TextInput
                    label={t('objectName')} placeholder={t('objectNamePlaceholder')} required maxLength={limits.maxNameLength}
                    value={name} onChange={(e) => setName(e.currentTarget.value)}
                  />
                  <Textarea
                    label={t('description')} rows={3} maxLength={limits.maxDescriptionLength}
                    value={description} onChange={(e) => setDescription(e.currentTarget.value)}
                  />
                </>
              )}
              <StatementBlock
                title={t('objectStatementTitle')} testId="avatar-object-statement"
                statement={statement} locale={locale} locales={locales} onLocale={setLocale}
                attested={attested} onAttested={setAttested}
              />
              {requirements.personCheck === true && (
                <Text size="sm" c="dimmed" data-testid="object-person-check-note">{t('personCheckNote')}</Text>
              )}
            </Stack>
          </Stepper.Step>
        </Stepper>

        {error && <Alert color="red" variant="light" data-testid="avatar-form-error">{error}</Alert>}

        <Group justify="space-between" wrap="wrap" gap="xs">
          <Button variant="default" onClick={step === 0 ? onClose : () => setStep(0)} style={{ minHeight: 44 }}>
            {step === 0 ? t('cancel') : t('back')}
          </Button>
          {step === 0 ? (
            <Button onClick={() => setStep(1)} disabled={!filesOk} style={{ minHeight: 44 }}>{t('next')}</Button>
          ) : (
            <Button onClick={() => void submit()} disabled={!canSubmit} loading={saving} style={{ minHeight: 44 }} data-testid="avatar-submit">
              {replacing ? t('saveFiles') : target ? tVar('create') : t('objectCreate')}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
