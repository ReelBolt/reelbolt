'use client';

/**
 * The annotation editor (P6): keywords, a note, a colour label, favourite and flag for one source.
 *
 * Edits save on change (R05.8): switches and the colour straight away, the note and keywords after a
 * short pause in typing, each as ONE `PUT …/media/annotations/{mediaRef}`. There is no Save button;
 * a small status line says Saving / Saved, and a failure shows plain text with a Retry. Only the fields the user touched are sent — the server
 * keeps what the body omits — and the colour label is sent as an explicit `null` only when the
 * user cleared it, which is the one shape the server reads as "no label".
 */

import React, { useEffect, useState } from 'react';
import { Button, Group, Select, Stack, Switch, TagsInput, Text, Textarea } from '@mantine/core';
import { MEDIA_COLOR_LABELS, type MediaAnnotation, type MediaColorLabel, type UpsertMediaAnnotationRequest } from '@/lib/editing/media/types';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useIsTouchSurface } from '@/lib/editing/use-device-class';
import { useT } from '@/i18n';

const TOUCH_TARGET = 44;

export interface AnnotationEditorProps {
  /** The stored annotation, or `null` when the source has none yet. */
  annotation: MediaAnnotation | null;
  onSave: (request: UpsertMediaAnnotationRequest) => Promise<void>;
  disabled?: boolean;
}

interface Draft {
  keywords: string[];
  note: string;
  colorLabel: MediaColorLabel | null;
  favourite: boolean;
  flag: boolean;
}

const draftOf = (annotation: MediaAnnotation | null): Draft => ({
  keywords: annotation?.keywords ?? [],
  note: annotation?.note ?? '',
  colorLabel: annotation?.colorLabel ?? null,
  favourite: annotation?.favourite ?? false,
  flag: annotation?.flag ?? false,
});

/** The request carrying only what differs from the stored annotation; `null` when nothing does. */
export function annotationRequest(annotation: MediaAnnotation | null, draft: Draft): UpsertMediaAnnotationRequest | null {
  const stored = draftOf(annotation);
  const request: UpsertMediaAnnotationRequest = {};
  const normalizedKeywords = [...new Set(draft.keywords.map((k) => k.trim().toLowerCase()).filter((k) => k.length > 0))];
  if (normalizedKeywords.join('\u0000') !== stored.keywords.join('\u0000')) request.keywords = normalizedKeywords;
  if (draft.note.trim() !== stored.note) request.note = draft.note.trim();
  if (draft.colorLabel !== stored.colorLabel) request.colorLabel = draft.colorLabel;
  if (draft.favourite !== stored.favourite) request.favourite = draft.favourite;
  if (draft.flag !== stored.flag) request.flag = draft.flag;
  return Object.keys(request).length === 0 ? null : request;
}

/** How long typing must pause before the note or keywords are written. */
export const AUTOSAVE_TEXT_DELAY_MS = 600;

export function AnnotationEditor({ annotation, onSave, disabled = false }: AnnotationEditorProps) {
  const t = useT('editor-media.annotation');
  const tc = useT('editor-media.color');
  const [draft, setDraft] = useState<Draft>(() => draftOf(annotation));
  const [basedOn, setBasedOn] = useState<string>(annotation?.updatedAt ?? '');
  const touch = useIsTouchSurface();
  const fieldSize = touch ? 'md' : 'xs';
  const inputStyles = touch ? { input: { minHeight: TOUCH_TARGET }, option: { minHeight: TOUCH_TARGET } } : undefined;
  const switchStyles = touch ? { body: { alignItems: 'center' }, label: { minHeight: TOUCH_TARGET, display: 'flex', alignItems: 'center' } } : undefined;
  // True from the first edit until the stored annotation has caught up with the draft.
  const [dirty, setDirty] = useState(false);
  const [typing, setTyping] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failedKey, setFailedKey] = useState<string | null>(null);
  // The request last written; never written twice while the parent's copy catches up.
  const [savedKey, setSavedKey] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const pending = annotationRequest(annotation, draft);
  const pendingKey = pending ? JSON.stringify(pending) : null;

  // A newer stored annotation (another save landed) replaces a draft the user has not touched.
  const storedKey = annotation?.updatedAt ?? '';
  if (storedKey !== basedOn) {
    setBasedOn(storedKey);
    if (!dirty) setDraft(draftOf(annotation));
  }
  if (dirty && !pending && !busy) setDirty(false);

  const edit = (patch: Partial<Draft>, isText: boolean) => {
    setSavedKey(null);
    setDraft({ ...draft, ...patch });
    setDirty(true);
    setTyping(isText);
    setFailedKey(null);
    setMessage(null);
  };

  useEffect(() => {
    if (!dirty || !pending || pendingKey === failedKey || pendingKey === savedKey || disabled || busy) return;
    const timer = setTimeout(() => {
      setBusy(true);
      setMessage(null);
      onSave(pending)
        .then(() => setSavedKey(pendingKey))
        .catch((error: unknown) => {
          setFailedKey(pendingKey);
          setMessage(humanizeErrorT(t, error, t('saveFailed')));
        })
        .finally(() => setBusy(false));
    }, typing ? AUTOSAVE_TEXT_DELAY_MS : 0);
    return () => clearTimeout(timer);
    // `pending` is derived from `pendingKey`; the key is the stable dependency.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dirty, pendingKey, failedKey, savedKey, disabled, busy, typing]);

  const status = busy
    ? t('saving')
    : message
      ? message
      : pending && pendingKey !== savedKey
        ? t('savingSoon')
        : annotation || savedKey
          ? t('saved')
          : t('notAnnotated');

  return (
    <Stack gap="xs" data-testid="annotation-editor">
      <Text size="sm" fw={600}>
        {t('title')}
      </Text>
      <TagsInput
        size={fieldSize}
        styles={inputStyles}
        label={t('keywords')}
        description={t('keywordsHint')}
        value={draft.keywords}
        onChange={(keywords) => edit({ keywords }, true)}
        maxTags={32}
        disabled={disabled}
        data-testid="annotation-keywords"
      />
      <Textarea
        size={fieldSize}
        styles={inputStyles}
        label={t('note')}
        rows={2}
        maxLength={2000}
        value={draft.note}
        onChange={(e) => edit({ note: e.currentTarget.value }, true)}
        disabled={disabled}
        data-testid="annotation-note"
      />
      <Group grow align="flex-end">
        <Select
          size={fieldSize}
          styles={inputStyles}
          label={t('colourLabel')}
          placeholder={t('colourNone')}
          clearable
          data={MEDIA_COLOR_LABELS.map((c) => ({ value: c, label: tc(c) }))}
          value={draft.colorLabel}
          onChange={(v) => edit({ colorLabel: (v as MediaColorLabel | null) ?? null }, false)}
          disabled={disabled}
          data-testid="annotation-color"
        />
        <Switch size={fieldSize} styles={switchStyles} label={t('favourite')} checked={draft.favourite} onChange={(e) => edit({ favourite: e.currentTarget.checked }, false)} disabled={disabled} data-testid="annotation-favourite" />
        <Switch size={fieldSize} styles={switchStyles} label={t('flag')} checked={draft.flag} onChange={(e) => edit({ flag: e.currentTarget.checked }, false)} disabled={disabled} data-testid="annotation-flag" />
      </Group>
      <Group justify="space-between">
        <Text size="xs" c="dimmed" role="status" data-testid="annotation-message">
          {status}
        </Text>
        {failedKey !== null && !busy ? (
          <Button size={touch ? 'md' : 'compact-sm'} fz="xs" variant="default" style={touch ? { minHeight: TOUCH_TARGET, minWidth: TOUCH_TARGET } : undefined} onClick={() => setFailedKey(null)} data-testid="annotation-retry">
            {t('tryAgain')}
          </Button>
        ) : null}
      </Group>
    </Stack>
  );
}
