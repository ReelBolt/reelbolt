'use client';

import { useMemo, useState, type ComponentProps } from 'react';
import { Button, Group, Input, Stack, Text } from '@mantine/core';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useMediaPool } from '@/lib/hooks/use-media';
import { useT } from '@/i18n';
import type { ProjectFile } from '@/lib/types/project';
import { MediaPickerModal, MediaThumb, type PickedMediaFile } from './MediaPickerModal';
import { filterMediaFiles, mediaRefOfFile, poolByFileId, type MediaPickerKind } from './media-picker-model';

export interface MediaPickerFieldProps {
  projectId: string | undefined;
  kind: MediaPickerKind;
  multiple?: boolean;
  /** Chosen file ids. */
  value: readonly string[];
  onChange(files: PickedMediaFile[]): void;
  label?: string;
  description?: string;
  error?: string | null;
  disabled?: boolean;
  maxSelect?: number;
  minSelect?: number;
  title?: string;
  filter?: (file: ProjectFile) => boolean;
  size?: ComponentProps<typeof Input.Wrapper>['size'];
  wrapperProps?: Record<string, unknown>;
  /** Id of the trigger button (for label `htmlFor` and focus). */
  id?: string;
}

/**
 * The picker as a form field: the chosen files as small thumbnails with their names, and a Choose button
 * that opens {@link MediaPickerModal}. A chosen id that is no longer in the project reads "File no longer
 * available" rather than disappearing.
 */
export function MediaPickerField({
  projectId, kind, multiple = false, value, onChange, label, description, error, disabled, maxSelect, minSelect, title, filter, size = 'xs', wrapperProps, id,
}: MediaPickerFieldProps) {
  const t = useT('mediaPicker.field');
  const [opened, setOpened] = useState(false);
  const { data: files } = useProjectFiles(projectId ?? '');
  const { data: pool } = useMediaPool(projectId ?? '');
  const pooled = useMemo(() => poolByFileId(pool), [pool]);
  const offered = useMemo(() => filterMediaFiles(files, kind, '', filter), [files, kind, filter]);
  const byId = useMemo(() => new Map((files ?? []).map((f) => [f.id, f])), [files]);
  const empty = offered.length === 0;
  const loaded = files !== undefined;

  return (
    <Input.Wrapper label={label} description={description} error={error ?? undefined} size={size} {...wrapperProps}>
      <Stack gap={6} mt={4}>
        {value.length === 0 ? (
          <Text size="sm" c="dimmed">{empty && loaded ? t('empty') : t('none')}</Text>
        ) : (
          value.map((fileId) => {
            const f = byId.get(fileId);
            return (
              <Group key={fileId} gap="xs" wrap="nowrap" data-testid="media-picker-chosen">
                {f ? (
                  <div style={{ width: 64, flex: '0 0 64px' }}>
                    <MediaThumb projectId={projectId ?? ''} file={f} poolRef={pooled.has(fileId) ? mediaRefOfFile(fileId) : null} />
                  </div>
                ) : null}
                <Text size="sm" lineClamp={1} c={f || !loaded ? undefined : 'orange'} style={{ minWidth: 0 }}>
                  {f ? f.originalFileName : loaded ? t('missing') : '…'}
                </Text>
              </Group>
            );
          })
        )}
        <div>
          <Button
            id={id}
            variant="default"
            size="xs"
            style={{ minHeight: 44 }}
            disabled={disabled || !projectId || (loaded && empty)}
            onClick={() => setOpened(true)}
          >
            {multiple ? t('chooseMany') : value.length > 0 ? t('change') : t('choose')}
          </Button>
        </div>
      </Stack>
      {projectId ? (
        <MediaPickerModal
          opened={opened}
          onClose={() => setOpened(false)}
          projectId={projectId}
          kind={kind}
          multiple={multiple}
          value={[...value]}
          maxSelect={maxSelect}
          minSelect={minSelect}
          title={title}
          filter={filter}
          onConfirm={(picked) => { setOpened(false); onChange(picked); }}
        />
      ) : null}
    </Input.Wrapper>
  );
}
