'use client';

import { useMemo, useState } from 'react';
import {
  Alert, Badge, Button, Checkbox, Divider, Group, List, Loader, Modal, Radio, Select, Stack, Text, TextInput, Textarea,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { deleteAvatar, revokeAvatarConsent, updateAvatar } from '@/lib/api/avatars';
import { isObjectAvatar, needsPictureConsent } from '@/lib/avatars/model';
import { VARIANT_DESCRIPTION_MAX, isVariant, variantDescriptionProblem } from '@/lib/avatars/variants';
import { useAvatarUsage } from '@/lib/hooks/use-avatars';
import { useProjectVoices } from '@/lib/hooks/use-voices';
import type {
  AvatarDisclosureLabel, AvatarRequirements, DeleteAvatarResponse, ProjectAvatar, UpdateAvatarRequest,
} from '@/lib/types/avatars';
import { useFormat, useT } from '@/i18n';
import { useAvatarErrorMessage } from './avatar-errors';
import { useAvatarName } from './avatar-names';
import { ObjectFactsSection } from './ObjectFactsSection';

const NO_VOICE = '__none__';

function usePhone(): boolean {
  return Boolean(useMediaQuery('(max-width: 600px)'));
}

/**
 * Name, description, voice link and AI label (an object: name and description only): a plain edit, no
 * re-attestation. A variant also edits what sets it apart from its original.
 */
export function AvatarEditModal({ projectId, requirements, avatar, onClose, onSaved }: {
  projectId: string;
  requirements: AvatarRequirements;
  avatar: ProjectAvatar | null;
  onClose: () => void;
  onSaved: () => void | Promise<void>;
}) {
  if (!avatar) return null;
  return <EditForm key={avatar.id} projectId={projectId} requirements={requirements} avatar={avatar} onClose={onClose} onSaved={onSaved} />;
}

function EditForm({ projectId, requirements, avatar, onClose, onSaved }: {
  projectId: string;
  requirements: AvatarRequirements;
  avatar: ProjectAvatar;
  onClose: () => void;
  onSaved: () => void | Promise<void>;
}) {
  const t = useT('avatars.edit');
  const tForm = useT('avatars.form');
  const phone = usePhone();
  // An object has no voice and no AI label choice: the server refuses both fields for it.
  const isObject = isObjectAvatar(avatar);
  const { data: voices } = useProjectVoices(isObject ? null : projectId);
  const [name, setName] = useState(avatar.name);
  const [description, setDescription] = useState(avatar.description ?? '');
  // A variant shows as "Car · Red": its own name is what it goes by if its original is ever deleted.
  const variant = isVariant(avatar);
  const shownName = useAvatarName()(avatar);
  const [variantDescription, setVariantDescription] = useState(avatar.variantDescription ?? '');
  const variantProblem = variant ? variantDescriptionProblem(variantDescription) : null;
  const [voiceId, setVoiceId] = useState<string>(avatar.projectVoiceId ?? NO_VOICE);
  const [disclosure, setDisclosure] = useState<AvatarDisclosureLabel>(avatar.disclosureLabel);
  const [disclosesOtherwise, setDisclosesOtherwise] = useState(avatar.disclosureLabel === 'MetadataOnly');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const describe = useAvatarErrorMessage(() => null, isObject ? 'Object' : 'Person');

  const voiceOptions = useMemo(() => {
    const out = [{ value: NO_VOICE, label: tForm('voiceNone') }];
    for (const v of voices ?? []) {
      if (v.selectable || v.id === avatar.projectVoiceId) out.push({ value: v.id, label: v.name });
    }
    return out;
  }, [voices, avatar.projectVoiceId, tForm]);

  const nameOk = name.trim().length > 0 && name.trim().length <= requirements.limits.maxNameLength;
  const disclosureOk = isObject || disclosure === 'Visible' || disclosesOtherwise;

  const save = async () => {
    const patch: UpdateAvatarRequest = {};
    if (name.trim() !== avatar.name) patch.name = name.trim();
    if (description.trim() !== (avatar.description ?? '')) patch.description = description.trim();
    if (variant && variantDescription.trim() !== (avatar.variantDescription ?? '').trim()) patch.variantDescription = variantDescription.trim();
    if (!isObject) {
      if (voiceId === NO_VOICE && avatar.projectVoiceId) patch.clearProjectVoice = true;
      else if (voiceId !== NO_VOICE && voiceId !== avatar.projectVoiceId) patch.projectVoiceId = voiceId;
      if (disclosure !== avatar.disclosureLabel) {
        patch.disclosureLabel = disclosure;
        patch.disclosesOtherwise = disclosure === 'MetadataOnly' && disclosesOtherwise;
      }
    }
    if (Object.keys(patch).length === 0) { onClose(); return; }
    setSaving(true);
    setError(null);
    try {
      await updateAvatar(projectId, avatar.id, patch);
      notifications.show({ color: 'green', message: t('saved', { name: variant ? shownName : patch.name ?? avatar.name }) });
      await onSaved();
    } catch (e) {
      setError(describe(e, t('failed')));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title', { name: shownName })} size="lg" fullScreen={phone} radius="md">
      <Stack gap="sm">
        <TextInput
          label={isObject ? tForm('objectName') : tForm('name')} required maxLength={requirements.limits.maxNameLength}
          description={variant ? t('variantNameHint', { name: shownName }) : undefined}
          value={name} onChange={(e) => setName(e.currentTarget.value)}
        />
        {variant && (
          <Textarea
            label={t('variantDescription')} description={t('variantDescriptionHint')} required autosize minRows={2} maxRows={5}
            maxLength={VARIANT_DESCRIPTION_MAX} value={variantDescription} onChange={(e) => setVariantDescription(e.currentTarget.value)}
            error={variantProblem === 'required' ? t('variantDescriptionRequired') : undefined} data-testid="avatar-edit-variant-description"
          />
        )}
        <Textarea label={tForm('description')} rows={3} maxLength={requirements.limits.maxDescriptionLength} value={description} onChange={(e) => setDescription(e.currentTarget.value)} />
        {!isObject && (
          <>
            <Select label={tForm('voice')} description={tForm('voiceHint')} data={voiceOptions} value={voiceId} onChange={(v) => setVoiceId(v ?? NO_VOICE)} allowDeselect={false} />
            <Radio.Group label={tForm('disclosure')} description={tForm('disclosureHint')} value={disclosure} onChange={(v) => setDisclosure(v as AvatarDisclosureLabel)}>
              <Stack gap={6} mt={6}>
                <Radio value="Visible" label={tForm('disclosureVisible')} description={tForm('disclosureVisibleHint')} />
                <Radio value="MetadataOnly" label={tForm('disclosureMetadata')} description={tForm('disclosureMetadataHint')} />
              </Stack>
            </Radio.Group>
            {disclosure === 'MetadataOnly' && (
              <Checkbox label={tForm('disclosesOtherwise')} checked={disclosesOtherwise} onChange={(e) => setDisclosesOtherwise(e.currentTarget.checked)} />
            )}
          </>
        )}
        {isObject && <ObjectFactsSection projectId={projectId} avatar={avatar} onSaved={onSaved} />}
        {error && <Alert color="red" variant="light" data-testid="avatar-edit-error">{error}</Alert>}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
          <Button onClick={() => void save()} disabled={!nameOk || !disclosureOk || variantProblem !== null} loading={saving} style={{ minHeight: 44 }}>{t('save')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Withdrawing consent: unusable at once, everywhere; the avatar is deleted after the grace period. */
export function AvatarRevokeModal({ projectId, requirements, avatar, onClose, onDone, fileName }: {
  projectId: string;
  requirements: AvatarRequirements;
  avatar: ProjectAvatar | null;
  onClose: () => void;
  onDone: () => void | Promise<void>;
  fileName: (id: string) => string | null;
}) {
  const t = useT('avatars.revoke');
  const tForm = useT('avatars.form');
  const phone = usePhone();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const describe = useAvatarErrorMessage(fileName);
  const nameOf = useAvatarName();
  if (!avatar) return null;
  const name = nameOf(avatar);

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      await revokeAvatarConsent(projectId, avatar.id);
      notifications.show({ color: 'yellow', message: t('done', { name, days: requirements.limits.revokedGraceDays }) });
      await onDone();
      onClose();
    } catch (e) {
      setError(describe(e, t('failed')));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title', { name })} size="md" fullScreen={phone} radius="md">
      <Stack gap="sm">
        <Text size="sm">{t('body', { name, days: requirements.limits.revokedGraceDays })}</Text>
        <Text size="sm" c="dimmed">{t('outputsStay')}</Text>
        <Text size="sm" c="dimmed">{t('cannotUndo')}</Text>
        {error && <Alert color="red" variant="light">{error}</Alert>}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
          <Button color="orange" onClick={() => void confirm()} loading={busy} style={{ minHeight: 44 }}>{t('confirm')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * Deleting: says what will be removed (a person's reference files, the signed permission, vendor copies),
 * what stays (videos already made; an object's files, which are ordinary project files), then shows the
 * report of what was actually removed.
 */
export function AvatarDeleteModal({ projectId, avatar, onClose, onDone, fileName }: {
  projectId: string;
  avatar: ProjectAvatar | null;
  onClose: () => void;
  onDone: () => void | Promise<void>;
  fileName: (id: string) => string | null;
}) {
  if (!avatar) return null;
  return <DeleteDialog key={avatar.id} projectId={projectId} avatar={avatar} onClose={onClose} onDone={onDone} fileName={fileName} />;
}

function DeleteDialog({ projectId, avatar, onClose, onDone, fileName }: {
  projectId: string;
  avatar: ProjectAvatar;
  onClose: () => void;
  onDone: () => void | Promise<void>;
  fileName: (id: string) => string | null;
}) {
  const t = useT('avatars.delete');
  const tForm = useT('avatars.form');
  const phone = usePhone();
  const { data: usage } = useAvatarUsage(projectId, avatar.id);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [report, setReport] = useState<DeleteAvatarResponse | null>(null);
  const isObject = isObjectAvatar(avatar);
  const describe = useAvatarErrorMessage(fileName, isObject ? 'Object' : 'Person');
  const name = useAvatarName()(avatar);
  // Deleting an original leaves its variants in the project, as avatars of their own.
  const variantCount = !isVariant(avatar) && typeof avatar.variantCount === 'number' ? avatar.variantCount : 0;

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await deleteAvatar(projectId, avatar.id);
      setReport(result);
      await onDone();
    } catch (e) {
      setError(describe(e, isObject ? t('objectFailed') : t('failed')));
    } finally {
      setBusy(false);
    }
  };

  const names = avatar.references.map((r) => r.fileName ?? fileName(r.projectFileId) ?? t('unnamedFile'));
  const documentName = !isObject && avatar.consent.documentFileId ? fileName(avatar.consent.documentFileId) ?? t('unnamedFile') : null;

  return (
    <Modal opened onClose={onClose} title={t('title', { name })} size="md" fullScreen={phone} radius="md">
      {report ? (
        <Stack gap="sm" data-testid="avatar-delete-report">
          <Text size="sm" fw={600}>{t('reportTitle', { name })}</Text>
          <List size="sm" spacing={4}>
            {isObject ? (
              // An object's files are ordinary project files: none is deleted with it.
              <>
                {report.references.deleted > 0 && <List.Item>{t('reportReferences', { deleted: report.references.deleted })}</List.Item>}
                {report.references.kept > 0 && <List.Item>{t('reportObjectKept', { count: report.references.kept })}</List.Item>}
              </>
            ) : (
              <>
                <List.Item>{t('reportReferences', { deleted: report.references.deleted })}</List.Item>
                {report.references.kept > 0 && <List.Item>{t('reportKept', { count: report.references.kept })}</List.Item>}
              </>
            )}
            {report.references.failed > 0 && <List.Item c="red">{t('reportFailed', { count: report.references.failed })}</List.Item>}
            {!isObject && avatar.consent.documentFileId && (
              <List.Item>{report.consentDocumentDeleted ? t('reportDocumentDeleted') : t('reportDocumentKept')}</List.Item>
            )}
            {report.vendorAssets.map((v, i) => (
              <List.Item key={`${v.provider ?? 'vendor'}-${i}`}>
                {t('reportVendor', { provider: v.provider ?? t('aProvider'), outcome: t.has(`outcomes.${v.outcome}`) ? t(`outcomes.${v.outcome}`) : v.outcome })}
              </List.Item>
            ))}
            {report.vendorAssets.length === 0 && <List.Item>{t('reportNoVendor')}</List.Item>}
            {report.stills && report.stills.deleted > 0 && (
              <List.Item>{isObject ? t('reportObjectStills', { count: report.stills.deleted }) : t('reportStills', { count: report.stills.deleted })}</List.Item>
            )}
            {report.stills && report.stills.failed !== 0 && <List.Item>{isObject ? t('reportObjectStillsPending') : t('reportStillsPending')}</List.Item>}
          </List>
          <Group justify="flex-end">
            <Button onClick={onClose} style={{ minHeight: 44 }}>{t('close')}</Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="sm">
          <Text size="sm">{t('intro', { name })}</Text>
          <Text size="sm" fw={600}>{t('willRemove')}</Text>
          {isObject ? (
            <>
              <List size="sm" spacing={4} data-testid="avatar-delete-list">
                <List.Item>{t('objectItself')}</List.Item>
                <List.Item>{t('vendorCopies')}</List.Item>
                <List.Item>{t('objectStillCopies')}</List.Item>
              </List>
              {names.length > 0 && (
                <Text size="sm" data-testid="object-files-kept">{t('objectFilesKept', { count: names.length, names: names.join(', ') })}</Text>
              )}
            </>
          ) : (
            <>
              <List size="sm" spacing={4} data-testid="avatar-delete-list">
                <List.Item>{t('referenceFiles', { count: names.length, names: names.join(', ') })}</List.Item>
                {documentName && <List.Item>{t('consentDocument', { name: documentName })}</List.Item>}
                <List.Item>{t('vendorCopies')}</List.Item>
                <List.Item>{t('stillCopies')}</List.Item>
              </List>
              <Text size="sm" c="dimmed">{t('sharedFilesKept')}</Text>
              {usage && usage.picturesUsingReferences > 0 && (
                <Text size="sm" c="dimmed">{t('pictures', { count: usage.picturesUsingReferences })}</Text>
              )}
            </>
          )}
          {usage && usage.runs.length > 0 && (
            <Text size="sm" c="dimmed">{t('runsStay', { count: usage.runs.length })}</Text>
          )}
          {variantCount > 0 && <Text size="sm" c="dimmed" data-testid="avatar-delete-variants-stay">{t('variantsStay', { count: variantCount })}</Text>}
          <Text size="sm" c="dimmed">{t('cannotUndo')}</Text>
          {error && <Alert color="red" variant="light">{error}</Alert>}
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
            <Button color="red" onClick={() => void confirm()} loading={busy} style={{ minHeight: 44 }}>{isObject ? t('objectConfirm') : t('confirm')}</Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}

/** The avatar's files, the consent on record (an object: when its statement was confirmed) and where it was used. */
export function AvatarDetailsModal({ projectId, avatar, onClose }: {
  projectId: string;
  avatar: ProjectAvatar | null;
  onClose: () => void;
}) {
  if (!avatar) return null;
  return <Details key={avatar.id} projectId={projectId} avatar={avatar} onClose={onClose} />;
}

function Details({ projectId, avatar, onClose }: { projectId: string; avatar: ProjectAvatar; onClose: () => void }) {
  const t = useT('avatars.details');
  const tAv = useT('avatars');
  const f = useFormat();
  const phone = usePhone();
  const { data: usage, error, isLoading } = useAvatarUsage(projectId, avatar.id);
  const consent = avatar.consent;
  const isObject = isObjectAvatar(avatar);
  const name = useAvatarName()(avatar);
  const variantLabel = (avatar.variantLabel ?? '').trim();
  const variantDescription = (avatar.variantDescription ?? '').trim();
  const statementLine = t('statementVersion', {
    version: consent.statementVersion,
    language: tAv.has(`form.languages.${consent.locale}`) ? tAv(`form.languages.${consent.locale}`) : consent.locale,
  });

  return (
    <Modal opened onClose={onClose} title={name} size="lg" fullScreen={phone} radius="md">
      <Stack gap="sm">
        {(variantLabel || variantDescription) && (
          <>
            <Stack gap={2} data-testid="avatar-variant-details">
              <Text size="sm" fw={600}>{variantLabel ? t('variantTitle', { label: variantLabel }) : t('variantTitleNoLabel')}</Text>
              {variantDescription && <Text size="sm">{variantDescription}</Text>}
            </Stack>
            <Divider />
          </>
        )}
        <Text size="sm" fw={600}>{t('references')}</Text>
        <Stack gap={4}>
          {avatar.references.map((r) => (
            <Group key={r.id} gap="xs" wrap="wrap" justify="space-between">
              <Text size="sm" style={{ flex: '1 1 200px', minWidth: 0 }} truncate>{r.fileName ?? t('unnamedFile')}</Text>
              <Group gap={6}>
                <Badge variant="light" color="gray" fz="xs">{tAv(`kinds.${r.kind}`)}</Badge>
                <Badge variant="light" color="blue" fz="xs">{tAv(`roles.${r.role}`)}</Badge>
                {r.state !== 'ok' && <Badge variant="light" color="red" fz="xs">{tAv(`refState.${r.state}`)}</Badge>}
              </Group>
            </Group>
          ))}
        </Stack>
        <Divider />
        {isObject ? (
          // An object has no person, no consent to withdraw, no keep-until date and no AI label choice.
          <>
            <Text size="sm" fw={600}>{t('objectConfirmation')}</Text>
            <List size="sm" spacing={2} data-testid="object-confirmation">
              {consent.attestedAt && <List.Item>{t('attestedOn', { date: f.date(consent.attestedAt) })}</List.Item>}
              <List.Item>{statementLine}</List.Item>
              <List.Item>{t('objectFilesStay')}</List.Item>
            </List>
          </>
        ) : (
          <>
            <Text size="sm" fw={600}>{t('consent')}</Text>
            <List size="sm" spacing={2}>
              <List.Item>{tAv('card.forSubject', { name: consent.subjectName })} · {consent.basis === 'Self' ? tAv('card.basisSelf') : tAv('card.basisWritten')}</List.Item>
              {consent.attestedAt && <List.Item>{t('attestedOn', { date: f.date(consent.attestedAt) })}</List.Item>}
              <List.Item>{statementLine}</List.Item>
              {consent.revokedAt && <List.Item c="red">{t('revokedOn', { date: f.date(consent.revokedAt) })}</List.Item>}
              {avatar.retentionUntil && <List.Item>{tAv('card.keepUntil', { date: f.date(avatar.retentionUntil) })}</List.Item>}
              <List.Item>{avatar.disclosureLabel === 'Visible' ? t('labelVisible') : t('labelMetadata')}</List.Item>
              {needsPictureConsent(avatar) && <List.Item>{tAv('card.notForPictures')}</List.Item>}
            </List>
          </>
        )}
        <Divider />
        <Text size="sm" fw={600}>{t('usageTitle')}</Text>
        {isLoading ? (
          <Loader size="sm" />
        ) : error || !usage ? (
          <Text size="sm" c="dimmed">{t('usageFailed')}</Text>
        ) : (
          <Stack gap={4} data-testid="avatar-usage">
            {usage.runs.length === 0
              ? <Text size="sm" c="dimmed">{t('usageNone')}</Text>
              : (
                <List size="sm" spacing={2}>
                  {usage.runs.map((run) => (
                    <List.Item key={run.executionId}>
                      {t('usageRun', { name: run.workflowName ?? t('unknownWorkflow'), count: run.takes.length, date: f.date(run.lastUsedAt) })}
                    </List.Item>
                  ))}
                </List>
              )}
            {usage.picturesUsingReferences > 0 && (
              <Text size="sm" c="dimmed">{t('usagePictures', { count: usage.picturesUsingReferences })}</Text>
            )}
          </Stack>
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{t('close')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
