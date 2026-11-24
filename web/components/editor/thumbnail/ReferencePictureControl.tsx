'use client';

import React, { useCallback, useRef, useState } from 'react';
import { Button, FileButton, Group, Loader, Select, Stack, Text } from '@mantine/core';
import { IconTrash, IconUpload } from '@tabler/icons-react';
import { MAX_REFERENCE_PICTURES } from '@/lib/editor/ai-operations';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { EditorIcon } from '../ui';
import { useT } from '@/i18n';

/** One picture the panel can use as a reference: a project file's id and the name it is shown by. */
export interface ReferencePictureOption {
  value: string;
  label: string;
}

export interface ReferencePictureControlProps {
  /** The project's own pictures, as the caller found them (the same list the canvas offers). */
  pictures: readonly ReferencePictureOption[];
  /**
   * The chosen pictures' project file ids, in the order the AI will see them. Empty: none chosen, which
   * is the ordinary case.
   */
  value: readonly string[];
  onChange(ids: string[]): void;
  /**
   * Uploads one picture from the device and answers it as an ordinary project file. The caller wires
   * this to the app's own `uploadFile`, so an uploaded reference is a project file like any other —
   * never a second way into storage. Absent: only the project's existing pictures are offered.
   */
  onUpload?(file: File): Promise<ReferencePictureOption>;
  /** The tool cannot run here (the server said so): the controls are inert but stay readable. */
  disabled?: boolean;
}

/**
 * The reference pictures of the "edit with words" tool: OPTIONAL pictures of the person or thing the
 * change is about — a face, a badge, a logo — chosen from the project's files or uploaded from the
 * device. They are offered for that one tool because the server takes them only there (see
 * `takesReferencePicture`).
 *
 * <p>Every chosen picture is listed with its position ("2nd: badge.png") because the server sends them
 * to the model in this order: "the second picture" has to mean the second one here. Each is removed by
 * its own button, and once `MAX_REFERENCE_PICTURES` are chosen nothing offers to add a fifth — the
 * limit the server enforces is stated rather than discovered as a refusal.</p>
 *
 * <p>An upload that fails says so in place rather than silently leaving the list as it was.</p>
 */
export function ReferencePictureControl({
  pictures,
  value,
  onChange,
  onUpload,
  disabled = false,
}: ReferencePictureControlProps) {
  const t = useT('editor-thumbnail-b.reference');
  const resetInput = useRef<(() => void) | null>(null);
  const busyRef = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const chosen = value.map((id) => ({
    id,
    label: pictures.find((picture) => picture.value === id)?.label ?? t('fallbackName'),
  }));
  const offered = pictures.filter((picture) => !value.includes(picture.value));
  const full = value.length >= MAX_REFERENCE_PICTURES;

  const add = useCallback(
    (id: string | null) => {
      if (!id || value.includes(id)) return;
      // Appended: the order the pictures are added in is the order the AI is taught to refer to them by.
      onChange([...value, id]);
    },
    [onChange, value],
  );

  const pickFile = useCallback(
    async (file: File | null) => {
      // One upload per pick: a second pick while the first is in flight is the same gesture arriving
      // twice, not a second picture.
      if (!file || !onUpload || busyRef.current) return;
      busyRef.current = true;
      setBusy(true);
      setError(null);
      try {
        const uploaded = await onUpload(file);
        add(uploaded.value);
      } catch (cause) {
        setError(
          t('uploadFailed', { name: file.name, reason: humanizeErrorT(t, cause, t('tryAgain')) }),
        );
      } finally {
        busyRef.current = false;
        setBusy(false);
        // Cleared so the SAME file can be picked again: a change event only fires on a change.
        resetInput.current?.();
      }
    },
    [add, onUpload],
  );

  return (
    <Stack gap={4} data-testid="reference-picture">
      <Text size="xs" fw={500}>
        {t('title')}
      </Text>
      <Text size="xs" c="dimmed">
        {t('help')}
      </Text>

      {chosen.map((picture, index) => (
        <Group
          key={picture.id}
          gap="xs"
          justify="space-between"
          wrap="nowrap"
          data-testid="reference-picture-item"
        >
          <Text size="xs">
            {t.rich('item', { position: index + 1, label: picture.label, b: (c) => <b>{c}</b> })}
          </Text>
          <EditorIcon icon={IconTrash} size="sm"
            disabled={disabled}
            label={t('remove', { label: picture.label })}
            onClick={() => onChange(value.filter((id) => id !== picture.id))} />
        </Group>
      ))}

      {full ? (
        <Text size="xs" c="dimmed" data-testid="reference-picture-full">
          {t('full', { max: MAX_REFERENCE_PICTURES })}
        </Text>
      ) : (
        <>
          <Select
            size="xs"
            label={t('addLabel')}
            placeholder={
              pictures.length === 0
                ? t('noneInProject')
                : offered.length === 0
                  ? t('allAdded')
                  : t('choose')
            }
            data={[...offered]}
            value={null}
            onChange={add}
            searchable
            disabled={disabled || busy}
            nothingFoundMessage={t('noMatch')}
          />
          {onUpload && (
            <FileButton
              onChange={(file) => void pickFile(file)}
              accept="image/*"
              disabled={disabled || busy}
              resetRef={resetInput}
              inputProps={{ 'aria-label': t('uploadAria') }}
            >
              {(props) => (
                <Button
                  {...props}
                  size="compact-xs" fz="xs"
                  variant="light"
                  fullWidth
                  disabled={disabled || busy}
                  leftSection={busy ? <Loader size={12} /> : <IconUpload size={14} />}
                >
                  {t('upload')}
                </Button>
              )}
            </FileButton>
          )}
        </>
      )}

      {error && (
        <Text size="xs" c="red" role="alert">
          {error}
        </Text>
      )}
    </Stack>
  );
}
