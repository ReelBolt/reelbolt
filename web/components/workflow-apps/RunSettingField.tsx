'use client';

import { useState } from 'react';
import { NumberInput, SegmentedControl, Select, Stack, Switch, Textarea } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { uploadFile } from '@/lib/api/files';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useProjectVoices } from '@/lib/hooks/use-voices';
import { useProjectAvatars } from '@/lib/hooks/use-avatars';
import type { StepChangeBriefField } from '@/lib/types/change-requests';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { AvatarRunInput } from './AvatarRunInput';
import { FileSlot } from './FileSlot';
import { APP_LANGUAGES, LENGTH_MAX_SEC, LENGTH_MIN_SEC, SOURCE_LANGUAGES, appRoleOf, resolveText } from './run-model';

interface Props {
  projectId: string;
  field: StepChangeBriefField;
  value: unknown;
  onChange: (value: unknown) => void;
  phone: boolean;
}

interface VoiceValue {
  mode?: 'None' | 'Narrated' | string;
  projectVoiceId?: string | null;
}

/**
 * One run setting of a finished app run in the change dialog (docs/design/app-inputs.md 29), in the run page's own control:
 * language, length, narration and voice, design, card sound, avatar, notes or a files slot. The value is the brief's shape
 * (`app-input-change.ts` `changedBrief` turns the edits into the body).
 */
export function RunSettingField({ projectId, field, value, onChange, phone }: Props) {
  const t = useT('apps.run.page');
  const id = `app-change-${field.key.replace(/[^A-Za-z0-9]/g, '-')}`;
  const label = field.label;

  if (field.kind === 'notes') {
    return (
      <Textarea id={id} label={label} minRows={2} autosize maxRows={8}
        value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.currentTarget.value)} />
    );
  }
  if (field.kind === 'files') return <FilesSetting projectId={projectId} field={field} value={value} onChange={onChange} phone={phone} id={id} />;

  switch (field.field) {
    case 'language':
    case 'sourceLanguage': {
      const list = field.field === 'language' ? APP_LANGUAGES : SOURCE_LANGUAGES;
      return (
        <Select id={id} label={label} data={list.map((l) => ({ value: l.value, label: resolveText(t, l.label) }))}
          value={typeof value === 'string' && value ? value : 'auto'} onChange={(v) => onChange(v ?? 'auto')}
          allowDeselect={false} comboboxProps={{ withinPortal: true }} />
      );
    }
    case 'lengthSec': {
      const choices = (field.input.choices ?? []).filter((c): c is number => typeof c === 'number');
      return choices.length > 0 ? (
        <div>
          <span id={`${id}-label`} style={{ fontSize: 'var(--mantine-font-size-sm)', fontWeight: 500 }}>{label}</span>
          <SegmentedControl aria-labelledby={`${id}-label`} fullWidth value={String(typeof value === 'number' ? value : choices[0])}
            onChange={(v) => onChange(Number(v))} data={choices.map((c) => ({ value: String(c), label: t('seconds', { count: c }) }))} />
        </div>
      ) : (
        <NumberInput id={id} label={label} min={LENGTH_MIN_SEC} max={LENGTH_MAX_SEC} step={5}
          value={typeof value === 'number' ? value : ''} onChange={(v) => onChange(typeof v === 'number' ? v : null)} />
      );
    }
    case 'voice':
      return <VoiceSetting projectId={projectId} label={label} value={value as VoiceValue | null} onChange={onChange} id={id} />;
    case 'designSystem':
      return (
        <Select id={id} label={label} data={[{ value: 'project', label: t('designProject') }, { value: 'none', label: t('designNone') }]}
          value={value === 'none' ? 'none' : 'project'} onChange={(v) => onChange(v ?? 'project')}
          allowDeselect={false} comboboxProps={{ withinPortal: true }} />
      );
    case 'overlayCardAudio':
      return (
        <Select id={id} label={label}
          data={[
            { value: 'agent', label: t('overlayCardAudioAgent') },
            { value: 'keep', label: t('overlayCardAudioKeep') },
            { value: 'mute', label: t('overlayCardAudioMute') },
          ]}
          value={typeof value === 'string' ? value : 'agent'} onChange={(v) => onChange(v ?? 'agent')}
          allowDeselect={false} comboboxProps={{ withinPortal: true }} />
      );
    case 'avatar':
      return <AvatarSetting projectId={projectId} label={label} value={value as { avatarId?: string } | null} onChange={onChange} />;
    default:
      return null;
  }
}

function VoiceSetting({ projectId, label, value, onChange, id }: { projectId: string; label: string; value: VoiceValue | null; onChange: (v: unknown) => void; id: string }) {
  const t = useT('apps.run.page');
  const { data: voices } = useProjectVoices(projectId);
  const narrated = value?.mode !== 'None';
  const data = [
    { value: '', label: t('defaultVoice') },
    ...(voices ?? []).map((v) => ({ value: v.id, label: v.selectable ? v.name : t('consentNeeded', { name: v.name }), disabled: !v.selectable })),
  ];
  return (
    <Stack gap="xs">
      <Switch label={label} description={narrated ? t('narrationOn') : t('narrationOff')} checked={narrated}
        onChange={(e) => onChange(e.currentTarget.checked ? { mode: 'Narrated' } : { mode: 'None' })}
        styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }} />
      {narrated && (
        <Select id={id} aria-label={t('voice')} data={data} value={value?.projectVoiceId ?? ''}
          onChange={(v) => onChange(v ? { mode: 'Narrated', projectVoiceId: v } : { mode: 'Narrated' })}
          allowDeselect={false} comboboxProps={{ withinPortal: true }} />
      )}
    </Stack>
  );
}

function AvatarSetting({ projectId, label, value, onChange }: { projectId: string; label: string; value: { avatarId?: string } | null; onChange: (v: unknown) => void }) {
  const { data: avatars } = useProjectAvatars(projectId, true);
  return (
    <AvatarRunInput projectId={projectId} label={label} avatars={avatars} value={value?.avatarId ?? null}
      onChange={(avatarId) => onChange(avatarId ? { avatarId } : null)} />
  );
}

function FilesSetting({ projectId, field, value, onChange, phone, id }: { projectId: string; field: StepChangeBriefField; value: unknown; onChange: (v: unknown) => void; phone: boolean; id: string }) {
  const t = useT('apps.run.page');
  const tAll = useT();
  const { data: files, mutate } = useProjectFiles(projectId);
  const [uploading, setUploading] = useState(false);
  const role = appRoleOf(field.role);
  if (!role) return null;
  const selected = Array.isArray(value) ? (value as string[]) : [];
  const upload = async (file: File) => {
    setUploading(true);
    try {
      const saved = await uploadFile(projectId, file);
      await mutate();
      onChange([...selected, saved.id]);
    } catch (err: unknown) {
      notifications.show({ title: t('uploadFailedTitle'), message: describeError(tAll, err, t('uploadFailedMessage')), color: 'red' });
    } finally {
      setUploading(false);
    }
  };
  return (
    <FileSlot projectId={projectId} input={{ ...field.input, label: field.label }} role={role} files={files ?? []} selected={selected} phone={phone}
      uploading={uploading} onChange={(ids) => onChange(ids)} onUpload={(f) => void upload(f)} inputId={id} />
  );
}
