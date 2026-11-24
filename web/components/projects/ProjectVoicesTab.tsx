'use client';

import { describeError, readableServerMessage } from '@/lib/api/describe-error';
import { useMemo, useState } from 'react';
import {
  Alert, Badge, Button, Checkbox, Group, Modal, MultiSelect, NumberInput, Select, Stack, Table, Text, TextInput, Textarea,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconMicrophone, IconPlus, IconSearch, IconShieldCheck, IconTrash } from '@tabler/icons-react';
import { Panel, QuietText } from '@/components/dashboard/Panel';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { VoiceLibraryBrowser } from '@/components/projects/VoiceLibraryBrowser';
import { cloneVoice, createLibraryVoice, deleteVoice, revokeVoiceConsent } from '@/lib/api/voices';
import { useAuth } from '@/lib/hooks/use-auth';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useSelectableProviders } from '@/lib/hooks/use-inference-providers';
import { selectableLabel } from '@/lib/types/inference-provider';
import { useProjectVoices } from '@/lib/hooks/use-voices';
import { useFormat, useT, type T } from '@/i18n';
import {
  DEFAULT_RETENTION_DAYS, MAX_RETENTION_DAYS, REQUIRED_CONSENT_STATEMENT, type ProjectVoice,
} from '@/lib/types/voices';

function errorMessage(tAll: T, e: unknown): string | null {
  return readableServerMessage(e) === null ? null : describeError(tAll, e);
}

/**
 * The project's voice library (docs/voiceover.md "Voices and consent"). A cloned voice is a
 * person's biometric data, so this tab makes the consent state visible on every row, requires the
 * attestation before a clone is created, lets consent be revoked (unselectable at once) and makes
 * delete a real deletion (vendor model + reference recordings + row), reporting what was removed.
 */
export function ProjectVoicesTab({ projectId }: { projectId: string }) {
  const t = useT('voices');
  const tAll = useT();
  const f = useFormat();
  const { data: voices, mutate, isLoading } = useProjectVoices(projectId);
  const [cloneOpen, setCloneOpen] = useState(false);
  const [libraryOpen, setLibraryOpen] = useState(false);
  const [browseOpen, setBrowseOpen] = useState(false);
  const [pendingDelete, setPendingDelete] = useState<ProjectVoice | null>(null);
  const [pendingRevoke, setPendingRevoke] = useState<ProjectVoice | null>(null);
  const [busy, setBusy] = useState(false);

  const list = voices ?? [];
  const registeredIds = useMemo(() => new Set(list.map((v) => v.remoteVoiceId)), [list]);

  const onRevoke = async () => {
    if (!pendingRevoke) return;
    setBusy(true);
    try {
      await revokeVoiceConsent(projectId, pendingRevoke.id);
      notifications.show({ color: 'yellow', message: t('tab.revoked', { name: pendingRevoke.name }) });
      await mutate();
    } catch (e) {
      notifications.show({ color: 'red', message: errorMessage(tAll, e) ?? t('tab.revokeFailed') });
    } finally {
      setBusy(false);
      setPendingRevoke(null);
    }
  };

  const onDelete = async () => {
    if (!pendingDelete) return;
    setBusy(true);
    try {
      const result = await deleteVoice(projectId, pendingDelete.id);
      const remote = pendingDelete.source === 'Cloned'
        ? result.remoteModelDeleted ? 'deleted' : 'failed'
        : 'library';
      notifications.show({
        color: result.remoteModelDeleted || pendingDelete.source !== 'Cloned' ? 'green' : 'orange',
        message: t('tab.deleted', { name: pendingDelete.name, remote, note: result.remoteDeletionNote ?? t('tab.unknown'), count: result.referenceFilesDeleted }),
      });
      await mutate();
    } catch (e) {
      notifications.show({ color: 'red', message: errorMessage(tAll, e) ?? t('tab.deleteFailed') });
    } finally {
      setBusy(false);
      setPendingDelete(null);
    }
  };

  return (
    <Stack gap="md">
      <Group justify="space-between" align="flex-start">
        <div>
          <Text fw={650}>{t('tab.title')}</Text>
          <QuietText>
            {t('tab.intro')}
          </QuietText>
        </div>
        <Group gap="xs">
          <Button size="xs" variant="default" leftSection={<IconSearch size={14} />} onClick={() => setBrowseOpen(true)}>{t('tab.browse')}</Button>
          <Button size="xs" variant="default" leftSection={<IconPlus size={14} />} onClick={() => setLibraryOpen(true)}>{t('tab.addLibrary')}</Button>
          <Button size="xs" leftSection={<IconMicrophone size={14} />} onClick={() => setCloneOpen(true)}>{t('tab.clone')}</Button>
        </Group>
      </Group>

      {list.length === 0 && !isLoading ? (
        <Panel id="voices-empty">
          <Stack align="flex-start" gap="xs">
            <Text fw={650}>{t('tab.emptyTitle')}</Text>
            <QuietText>{t('tab.emptyBody')}</QuietText>
            <Badge variant="light" color="gray">{t('tab.emptyBadge')}</Badge>
          </Stack>
        </Panel>
      ) : (
        <Table striped highlightOnHover withTableBorder>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('tab.colName')}</Table.Th>
              <Table.Th>{t('tab.colSource')}</Table.Th>
              <Table.Th>{t('tab.colProvider')}</Table.Th>
              <Table.Th>{t('tab.colConsent')}</Table.Th>
              <Table.Th>{t('tab.colRetention')}</Table.Th>
              <Table.Th>{t('tab.colUsable')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {list.map((v) => (
              <Table.Tr key={v.id} data-testid={`voice-row-${v.id}`}>
                <Table.Td>
                  <Text size="sm" fw={600}>{v.name}</Text>
                  {v.description && <Text size="xs" c="dimmed">{v.description}</Text>}
                  <Text size="xs" c="dimmed" ff="monospace">{v.remoteVoiceId}</Text>
                </Table.Td>
                <Table.Td><Badge variant="light" color={v.source === 'Cloned' ? 'grape' : 'blue'}>{v.source === 'Cloned' ? t('tab.sourceCloned') : v.source === 'Library' ? t('tab.sourceLibrary') : v.source}</Badge></Table.Td>
                <Table.Td><Text size="sm">{v.inferenceProviderName ?? (v.inferenceProviderId ? t('tab.providerRemoved') : t('tab.providerDefault'))}</Text></Table.Td>
                <Table.Td>
                  {v.source === 'Library' ? (
                    <Text size="xs" c="dimmed">{t('tab.notAPerson')}</Text>
                  ) : v.consentRevokedAt ? (
                    <Text size="xs" c="red">{t('tab.consentRevoked', { date: f.date(v.consentRevokedAt) })}</Text>
                  ) : v.consentAttestedAt ? (
                    <Stack gap={0}>
                      <Text size="xs">{t('tab.consentAttested', { date: f.date(v.consentAttestedAt) })}</Text>
                      <Text size="xs" c="dimmed">{t('tab.consentFor', { name: v.consentSubjectName })}</Text>
                    </Stack>
                  ) : (
                    <Text size="xs" c="red">{t('tab.consentMissing')}</Text>
                  )}
                </Table.Td>
                <Table.Td>
                  <Text size="xs">{v.retentionUntil ? t('tab.retentionUntil', { date: f.date(v.retentionUntil) }) : '—'}</Text>
                </Table.Td>
                <Table.Td>
                  {v.selectable
                    ? <Badge color="green" variant="light" leftSection={<IconShieldCheck size={12} />}>{t('tab.selectable')}</Badge>
                    : <Badge color="red" variant="light">{v.unselectableReason?.replaceAll('_', ' ') ?? t('tab.notSelectable')}</Badge>}
                </Table.Td>
                <Table.Td>
                  <Group gap="xs" justify="flex-end" wrap="nowrap">
                    {v.source === 'Cloned' && !v.consentRevokedAt && (
                      <Button size="compact-xs" variant="subtle" color="yellow" onClick={() => setPendingRevoke(v)}>{t('tab.revokeConsent')}</Button>
                    )}
                    <Button size="compact-xs" variant="subtle" color="red" leftSection={<IconTrash size={12} />} onClick={() => setPendingDelete(v)}>{t('tab.delete')}</Button>
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <CloneVoiceModal projectId={projectId} opened={cloneOpen} onClose={() => setCloneOpen(false)} onCreated={() => { setCloneOpen(false); void mutate(); }} />
      <VoiceLibraryBrowser projectId={projectId} opened={browseOpen} onClose={() => setBrowseOpen(false)} onAdded={() => { void mutate(); }} registeredIds={registeredIds} />
      <LibraryVoiceModal projectId={projectId} opened={libraryOpen} onClose={() => setLibraryOpen(false)} onCreated={() => { setLibraryOpen(false); void mutate(); }} />

      <ConfirmModal
        opened={pendingRevoke !== null}
        onClose={() => setPendingRevoke(null)}
        onConfirm={onRevoke}
        title={t('tab.revokeConsent')}
        message={t('tab.revokeMessage', { name: pendingRevoke?.name ?? '', days: 30 })}
        confirmLabel={t('tab.revokeConsent')}
        loading={busy}
      />
      <ConfirmModal
        opened={pendingDelete !== null}
        onClose={() => setPendingDelete(null)}
        onConfirm={onDelete}
        title={t('tab.deleteTitle')}
        message={pendingDelete?.source === 'Cloned'
          ? t('tab.deleteClonedMessage', { count: pendingDelete.referenceProjectFileIds.length })
          : t('tab.deleteLibraryMessage', { name: pendingDelete?.name ?? '' })}
        confirmLabel={t('tab.delete')}
        loading={busy}
      />
    </Stack>
  );
}

/**
 * The speech-synthesis models this workspace may choose: the member-facing selectable list, which
 * every role but Billing may read (the API refuses Billing, so it is not asked).
 */
function useSpeechModels() {
  const { user } = useAuth();
  const role = user?.orgRole ?? null;
  return useSelectableProviders('SpeechSynthesis', !!role && role !== 'Billing');
}

function CloneVoiceModal({ projectId, opened, onClose, onCreated }: { projectId: string; opened: boolean; onClose: () => void; onCreated: () => void }) {
  const t = useT('voices');
  const tAll = useT();
  const { data: files } = useProjectFiles(projectId);
  const { data: providers } = useSpeechModels();
  const [name, setName] = useState('');
  const [subject, setSubject] = useState('');
  const [fileIds, setFileIds] = useState<string[]>([]);
  const [providerId, setProviderId] = useState<string | null>(null);
  const [retentionDays, setRetentionDays] = useState<number>(DEFAULT_RETENTION_DAYS);
  const [attested, setAttested] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const audioFiles = useMemo(
    () => (files ?? []).filter((f) => f.mimeType.startsWith('audio/') || f.mimeType.startsWith('video/'))
      .map((f) => ({ value: f.id, label: f.originalFileName })),
    [files],
  );
  // Only Fish Audio exposes a cloning API, so only its models are offered here.
  const speechProviders = useMemo(
    () => (providers ?? []).filter((p) => p.capability === 'SpeechSynthesis' && p.kind === 'FishAudio')
      .map((p) => ({ value: p.id, label: selectableLabel(p) })),
    [providers],
  );

  const canSubmit = name.trim().length > 0 && subject.trim().length > 0 && fileIds.length > 0 && attested && !saving;

  const submit = async () => {
    setSaving(true);
    setError(null);
    try {
      await cloneVoice(projectId, {
        name: name.trim(),
        referenceProjectFileIds: fileIds,
        consentStatement: REQUIRED_CONSENT_STATEMENT,
        consentSubjectName: subject.trim(),
        inferenceProviderId: providerId,
        retentionDays,
      });
      notifications.show({ color: 'green', message: t('clone.done', { name: name.trim(), subject: subject.trim() }) });
      setName(''); setSubject(''); setFileIds([]); setAttested(false);
      onCreated();
    } catch (e) {
      setError(errorMessage(tAll, e) ?? t('clone.failed'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t('clone.title')} size="lg">
      <Stack gap="sm">
        <Alert color="grape" variant="light" title={t('clone.biometricTitle')}>
          {t('clone.biometricBody')}
        </Alert>
        <TextInput label={t('clone.voiceName')} placeholder={t('clone.voiceNamePlaceholder')} value={name} onChange={(e) => setName(e.currentTarget.value)} required />
        <MultiSelect
          label={t('clone.references')}
          description={t('clone.referencesHint')}
          data={audioFiles}
          value={fileIds}
          onChange={setFileIds}
          searchable
          nothingFoundMessage={t('clone.noMedia')}
          required
        />
        {speechProviders.length > 0 && (
          <Select
            label={t('clone.speechModel')}
            description={t('clone.speechModelHint')}
            data={speechProviders}
            value={providerId}
            onChange={setProviderId}
            clearable
          />
        )}
        <TextInput label={t('clone.whose')} placeholder={t('clone.whosePlaceholder')} value={subject} onChange={(e) => setSubject(e.currentTarget.value)} required />
        <NumberInput
          label={t('clone.keepFor')}
          description={t('clone.keepForHint', { max: MAX_RETENTION_DAYS })}
          min={1}
          max={MAX_RETENTION_DAYS}
          value={retentionDays}
          onChange={(v) => setRetentionDays(typeof v === 'number' ? v : DEFAULT_RETENTION_DAYS)}
        />
        <Textarea label={t('clone.consentStatement')} value={REQUIRED_CONSENT_STATEMENT} readOnly autosize minRows={3} />
        <Checkbox
          label={t('clone.attest')}
          checked={attested}
          onChange={(e) => setAttested(e.currentTarget.checked)}
        />
        {error && <Alert color="red" variant="light">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t('clone.cancel')}</Button>
          <Button onClick={submit} disabled={!canSubmit} loading={saving}>{t('clone.submit')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function LibraryVoiceModal({ projectId, opened, onClose, onCreated }: { projectId: string; opened: boolean; onClose: () => void; onCreated: () => void }) {
  const t = useT('voices');
  const tAll = useT();
  const { data: providers } = useSpeechModels();
  const [name, setName] = useState('');
  const [remoteVoiceId, setRemoteVoiceId] = useState('');
  const [description, setDescription] = useState('');
  const [providerId, setProviderId] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const speechProviders = useMemo(
    () => (providers ?? []).filter((p) => p.capability === 'SpeechSynthesis').map((p) => ({ value: p.id, label: selectableLabel(p) })),
    [providers],
  );

  const submit = async () => {
    setSaving(true);
    setError(null);
    try {
      await createLibraryVoice(projectId, { name: name.trim(), remoteVoiceId: remoteVoiceId.trim(), description: description.trim() || null, inferenceProviderId: providerId });
      setName(''); setRemoteVoiceId(''); setDescription('');
      onCreated();
    } catch (e) {
      setError(errorMessage(tAll, e) ?? t('library.failed'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t('library.title')}>
      <Stack gap="sm">
        <QuietText>{t('library.intro')}</QuietText>
        <TextInput label={t('library.name')} value={name} onChange={(e) => setName(e.currentTarget.value)} required />
        <TextInput label={t('library.voiceId')} value={remoteVoiceId} onChange={(e) => setRemoteVoiceId(e.currentTarget.value)} required ff="monospace" />
        <TextInput label={t('library.description')} description={t('library.descriptionHint')} value={description} onChange={(e) => setDescription(e.currentTarget.value)} />
        {speechProviders.length > 0 && (
          <Select label={t('library.speechModel')} data={speechProviders} value={providerId} onChange={setProviderId} clearable description={t('library.speechModelHint')} />
        )}
        {error && <Alert color="red" variant="light">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t('library.cancel')}</Button>
          <Button onClick={submit} disabled={!name.trim() || !remoteVoiceId.trim() || saving} loading={saving}>{t('library.submit')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
