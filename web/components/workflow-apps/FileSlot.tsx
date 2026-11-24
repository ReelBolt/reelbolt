'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { Button, Checkbox, Drawer, Group, Text } from '@mantine/core';
import { IconUpload } from '@tabler/icons-react';
import type { AppFileRole, AppManifestInput } from '@/lib/types/workflow';
import type { ProjectFile } from '@/lib/types/project';
import { DEFAULT_MAX_FILES, acceptFor, fileHasRole } from './run-model';
import { MediaPickerModal, MediaThumb } from '@/components/shared/media/MediaPickerModal';
import type { MediaPickerKind } from '@/components/shared/media/media-picker-model';
import { useT } from '@/i18n';
import styles from './AppRunPage.module.css';

/** The media picker kind a role is chosen with, or null for roles that are not pictures/sound/video (code, documents). */
function pickerKindFor(role: AppFileRole): MediaPickerKind | null {
  switch (role) {
    case 'video': return 'video';
    case 'image': return 'image';
    case 'music':
    case 'sfx': return 'audio';
    case 'insertContent':
    case 'overlay': return 'both';
    default: return null;
  }
}

interface FileSlotProps {
  /** The project the files belong to (the media picker lists them itself). */
  projectId: string;
  input: AppManifestInput;
  role: AppFileRole;
  files: readonly ProjectFile[];
  selected: readonly string[];
  /** Phone: the slot is one button that opens the project file sheet. */
  phone: boolean;
  uploading: boolean;
  onChange: (fileIds: string[]) => void;
  onUpload: (file: File) => void;
  inputId: string;
  /** Bumped by the page when a fix asks for files: opens the sheet on a phone, the file chooser otherwise. */
  addSignal?: number;
  /** One plain line under the label: what a usable file looks like, with an optional link to the guide. */
  hint?: { text: string; linkText: string; href: string };
}

/**
 * One `files` input: the project's files that fit the role, a choice among them, and a one-click
 * upload. The selection is written to `brief.sourceFiles` by the page; nothing here edits a step.
 */
export function FileSlot({ projectId, input, role, files, selected, phone, uploading, onChange, onUpload, inputId, addSignal = 0, hint }: FileSlotProps) {
  const t = useT('apps.run.slot');
  const fileInput = useRef<HTMLInputElement>(null);
  const [sheet, setSheet] = useState(false);
  const label = input.label?.trim() || t('yourFiles', { role });
  const candidates = files.filter((f) => fileHasRole(f, role));
  const max = input.max ?? DEFAULT_MAX_FILES;
  const pickerKind = pickerKindFor(role);
  const [picker, setPicker] = useState(false);

  // A bumped signal opens the sheet on a phone (state adjusted while rendering) or the file chooser.
  const [seenSignal, setSeenSignal] = useState(addSignal);
  if (addSignal !== seenSignal) {
    setSeenSignal(addSignal);
    if (phone && addSignal > 0) {
      if (pickerKind) setPicker(true);
      else setSheet(true);
    }
  }
  useEffect(() => {
    if (!phone && addSignal > 0) fileInput.current?.click();
  }, [addSignal, phone]);

  const inRole = useCallback((f: ProjectFile) => fileHasRole(f, role), [role]);
  const chosenFiles = selected.map((id) => files.find((f) => f.id === id)).filter((f): f is ProjectFile => f !== undefined);

  const toggle = (id: string, checked: boolean) => {
    if (checked) {
      if (selected.length >= max) return;
      onChange([...selected, id]);
    } else {
      onChange(selected.filter((s) => s !== id));
    }
  };

  const list = (
    <div className={styles.fileList}>
      {candidates.length === 0 ? (
        <Text size="sm" className={styles.muted}>
          {t('none', { role })}
        </Text>
      ) : (
        candidates.map((f) => (
          <div key={f.id} className={styles.fileRow}>
            <Checkbox
              label={f.originalFileName}
              checked={selected.includes(f.id)}
              onChange={(e) => toggle(f.id, e.currentTarget.checked)}
            />
          </div>
        ))
      )}
    </div>
  );

  const upload = (
    <>
      <input
        ref={fileInput}
        type="file"
        hidden
        accept={acceptFor(role)}
        multiple={false}
        data-testid={`upload-${role}`}
        onChange={(e) => {
          const file = e.currentTarget.files?.[0];
          e.currentTarget.value = '';
          if (file) onUpload(file);
        }}
      />
      <Button
        variant="default"
        size="sm"
        leftSection={<IconUpload size={16} />}
        loading={uploading}
        onClick={() => fileInput.current?.click()}
        style={{ minHeight: 44 }}
      >
        {t('upload', { role })}
      </Button>
    </>
  );

  const summary = selected.length === 0 ? t('summaryNone', { max }) : t('summaryChosen', { count: selected.length, max });

  if (pickerKind) {
    return (
      <div className={styles.field}>
        <span className={styles.fieldLabel} id={`${inputId}-label`}>
          {label}
        </span>
        {hint && (
          <Text size="sm" className={styles.muted} id={`${inputId}-hint`}>
            {hint.text}{' '}
            <a href={hint.href} target="_blank" rel="noreferrer">
              {hint.linkText}
            </a>
          </Text>
        )}
        <div role="group" aria-labelledby={`${inputId}-label`}>
          {chosenFiles.length > 0 && (
            <div className={styles.fileList}>
              {chosenFiles.map((f) => (
                <Group key={f.id} gap="xs" wrap="nowrap">
                  <div style={{ width: 64, flex: '0 0 64px' }}>
                    <MediaThumb projectId={projectId} file={f} poolRef={null} />
                  </div>
                  <Text size="sm" lineClamp={1} style={{ minWidth: 0 }}>{f.originalFileName}</Text>
                </Group>
              ))}
            </div>
          )}
          {candidates.length === 0 && (
            <Text size="sm" className={styles.muted}>
              {t('none', { role })}
            </Text>
          )}
        </div>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 6, flexWrap: 'wrap' }}>
          <Button
            id={inputId}
            variant="default"
            size="sm"
            fullWidth={phone}
            disabled={candidates.length === 0}
            onClick={() => setPicker(true)}
            style={{ minHeight: 44 }}
            aria-labelledby={`${inputId}-label ${inputId}`}
            aria-describedby={`${inputId}-summary`}
          >
            {t('choose', { role })}
          </Button>
          {upload}
          <Text size="sm" className={styles.muted} id={`${inputId}-summary`}>
            {summary}
          </Text>
        </div>
        <MediaPickerModal
          opened={picker}
          onClose={() => setPicker(false)}
          projectId={projectId}
          kind={pickerKind}
          multiple={max > 1}
          maxSelect={max}
          minSelect={0}
          value={[...selected]}
          filter={inRole}
          title={label}
          onConfirm={(picked) => {
            setPicker(false);
            onChange(picked.map((f) => f.id));
          }}
        />
      </div>
    );
  }

  return (
    <div className={styles.field}>
      <span className={styles.fieldLabel} id={`${inputId}-label`}>
        {label}
      </span>
      {hint && (
        <Text size="sm" className={styles.muted} id={`${inputId}-hint`}>
          {hint.text}{' '}
          <a href={hint.href} target="_blank" rel="noreferrer">
            {hint.linkText}
          </a>
        </Text>
      )}
      {phone ? (
        <>
          <Button id={inputId} variant="default" fullWidth onClick={() => setSheet(true)} style={{ minHeight: 44 }} aria-labelledby={`${inputId}-label ${inputId}`} aria-describedby={`${inputId}-summary`}>
            {t('choose', { role })}
          </Button>
          <Text size="sm" className={styles.muted} id={`${inputId}-summary`} mt={4}>
            {summary}
          </Text>
          <Drawer opened={sheet} onClose={() => setSheet(false)} position="bottom" title={label} size="75dvh">
            {list}
            <div style={{ marginTop: 12 }}>{upload}</div>
            <Button fullWidth mt="md" style={{ minHeight: 48 }} onClick={() => setSheet(false)}>
              {t('done')}
            </Button>
          </Drawer>
        </>
      ) : (
        <>
          <div id={inputId} tabIndex={-1} role="group" aria-labelledby={`${inputId}-label`}>
            {list}
          </div>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 6, flexWrap: 'wrap' }}>
            {upload}
            <Text size="sm" className={styles.muted}>
              {summary}
            </Text>
          </div>
        </>
      )}
    </div>
  );
}
