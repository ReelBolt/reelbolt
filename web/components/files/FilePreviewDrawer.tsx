'use client';

import { CheckedAudio, CheckedImage, CheckedVideo } from '@/components/outputs/CheckedVideo';
import { useEffect, useState } from 'react';
import { ActionIcon, Badge, Button, Drawer, Group, Stack, Tabs, Text, Textarea } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconArrowsMove, IconChevronLeft, IconChevronRight, IconDownload, IconPencil, IconPhotoEdit, IconRefresh, IconWand, IconTrash,
} from '@tabler/icons-react';
import { useAssistantShell } from '@/components/assistant/AssistantShellProvider';
import { projectFilePreviewUrl, projectFileUrl, updateProjectFileContent } from '@/lib/api/files';
import { useProjectFileContent } from '@/lib/hooks/use-files';
import { dirOf, fileKind, timeAgo } from '@/lib/files/model';
import { formatBytes } from '@/lib/utils/bytes';
import { canEditAsThumbnail } from '@/lib/editor/open-thumbnail-editor';
import type { ProjectFile } from '@/lib/types/project';
import { parseFileSummary } from '@/lib/files/summary';
import { useFormat, useT } from '@/i18n';
import { KindIcon } from './file-kind';

interface Props {
  projectId: string;
  file: ProjectFile | null;
  /** The listing the drawer was opened from, for prev/next. */
  siblings: ProjectFile[];
  onSelect: (f: ProjectFile) => void;
  onClose: () => void;
  onSaved: () => void;
  onDownload: (f: ProjectFile) => void;
  onMove: (f: ProjectFile) => void;
  onDelete: (f: ProjectFile) => void;
  onReindex: (f: ProjectFile) => void;
  onRename?: (f: ProjectFile) => void;
  onEditAsThumbnail?: (f: ProjectFile) => void;
}

// Keyed on file.id by the caller, so switching files remounts it and `content` is seeded once.
function TextEditor({ projectId, file, initial, onSaved }: { projectId: string; file: ProjectFile; initial: string; onSaved: () => void }) {
  const t = useT('files.preview');
  const [content, setContent] = useState(initial);
  const [saving, setSaving] = useState(false);
  const dirty = content !== initial;
  const save = async () => {
    setSaving(true);
    try {
      await updateProjectFileContent(projectId, file.id, content);
      notifications.show({ title: t('savedTitle'), message: t('savedMessage', { name: file.originalFileName }), color: 'green' });
      onSaved();
    } catch {
      notifications.show({ title: t('saveFailedTitle'), message: t('saveFailedMessage'), color: 'red' });
    } finally {
      setSaving(false);
    }
  };
  return (
    <Stack gap="sm">
      <Textarea
        aria-label={t('contentsOf', { name: file.originalFileName })}
        value={content} onChange={(e) => setContent(e.currentTarget.value)}
        autosize minRows={10} maxRows={26} styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 13 } }}
      />
      <Group justify="flex-end">
        <Button onClick={save} loading={saving} disabled={!dirty} style={{ minHeight: 44 }}>{t('saveChanges')}</Button>
      </Group>
    </Stack>
  );
}

function Preview({ projectId, file, onSaved }: { projectId: string; file: ProjectFile; onSaved: () => void }) {
  const t = useT('files.preview');
  const kind = fileKind(file);
  const url = projectFileUrl(projectId, file.id);
  const { data, error, isLoading } = useProjectFileContent(projectId, kind === 'text' ? file.id : null);

  if (kind === 'video')
    return <CheckedVideo key={file.id} src={url} controls playsInline preload="metadata" style={{ width: '100%', maxHeight: '60dvh', background: '#08080c', borderRadius: 12 }} aria-label={file.originalFileName} />;
  if (kind === 'audio')
    return <CheckedAudio key={file.id} src={url} controls preload="metadata" style={{ width: '100%' }} aria-label={file.originalFileName} />;
  if (kind === 'image')
    return <CheckedImage src={projectFilePreviewUrl(projectId, file.id, 1920)} alt={file.originalFileName} style={{ width: '100%', maxHeight: '60dvh', objectFit: 'contain', borderRadius: 12 }} />;
  if (kind === 'text') {
    if (isLoading) return <Text size="sm" c="dimmed">{t('loadingContents')}</Text>;
    if (error) return <Text size="sm" c="dimmed">{t('cannotPreview')}</Text>;
    return <TextEditor key={file.id} projectId={projectId} file={file} initial={data?.content ?? ''} onSaved={onSaved} />;
  }
  return <Text size="sm" c="dimmed">{t('noPreview')}</Text>;
}

export function FilePreviewDrawer({ projectId, file, siblings, onSelect, onClose, onSaved, onDownload, onMove, onDelete, onReindex, onRename, onEditAsThumbnail }: Props) {
  const t = useT('files.preview');
  const tr = useT();
  const fm = useFormat();
  const { openWithPrompt } = useAssistantShell();
  const index = file ? siblings.findIndex((s) => s.id === file.id) : -1;
  const prev = index > 0 ? siblings[index - 1] : null;
  const next = index >= 0 && index < siblings.length - 1 ? siblings[index + 1] : null;

  useEffect(() => {
    if (!file) return;
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement | null)?.tagName;
      if (tag === 'TEXTAREA' || tag === 'INPUT') return;
      if (e.key === 'ArrowLeft' && prev) onSelect(prev);
      if (e.key === 'ArrowRight' && next) onSelect(next);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [file, prev, next, onSelect]);

  const indexLabel = (s: string) => (s === 'Indexed' ? t('indexStatus.indexed') : s === 'Failed' ? t('indexStatus.failed') : s === 'Pending' ? t('indexStatus.pending') : s === 'Processing' ? t('indexStatus.processing') : s === 'NotIndexed' ? t('indexStatus.notIndexed') : s);
  if (!file) return null;
  const path = file.originalPath || file.originalFileName;

  return (
    <Drawer
      opened onClose={onClose} closeButtonProps={{ size: 44 }} position="right" size="min(100%, 560px)" padding="md"
      title={
        <Group gap="sm" wrap="nowrap">
          <KindIcon kind={fileKind(file)} size={32} />
          <div style={{ minWidth: 0 }}>
            <Text fw={650} truncate>{file.originalFileName}</Text>
            <Text size="xs" c="dimmed" truncate>{dirOf(file) ? `/${dirOf(file)}` : t('projectRoot')}</Text>
          </div>
        </Group>
      }
    >
      <Stack gap="md">
        <Preview projectId={projectId} file={file} onSaved={onSaved} />

        <Group gap="xs" justify="space-between" wrap="nowrap">
          <Group gap="xs">
            <ActionIcon variant="default" size={44} disabled={!prev} onClick={() => prev && onSelect(prev)} aria-label={t('previousFile')}><IconChevronLeft size={18} /></ActionIcon>
            <ActionIcon variant="default" size={44} disabled={!next} onClick={() => next && onSelect(next)} aria-label={t('nextFile')}><IconChevronRight size={18} /></ActionIcon>
          </Group>
          <Text size="xs" c="dimmed">{index >= 0 ? t('position', { index: index + 1, total: siblings.length }) : ''}</Text>
        </Group>

        <Tabs defaultValue="summary" keepMounted={false}>
          <Tabs.List grow>
            <Tabs.Tab value="summary" style={{ minHeight: 44 }}>{t('summaryTab')}</Tabs.Tab>
            <Tabs.Tab value="details" style={{ minHeight: 44 }}>{t('detailsTab')}</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panel value="summary" pt="md">
            {file.agentSummary ? (
              <SummaryBody raw={file.agentSummary} />
            ) : (
              <Text size="sm" c="dimmed">
                {file.summaryStatus === 'Failed' ? t('summaryFailed')
                  : file.summaryStatus === 'Pending' || file.summaryStatus === 'Processing' ? t('summaryPending')
                  : t('summaryNone')}
              </Text>
            )}
            <Text size="xs" c="dimmed" mt="sm" data-testid="summary-index-state">
              {file.indexingStatus === 'Indexed' ? t('indexedNote')
                : file.indexingStatus === 'Failed' ? t('indexFailedNote')
                : t('notIndexedNote')}
            </Text>
          </Tabs.Panel>
          <Tabs.Panel value="details" pt="md">
            <Stack gap={6}>
              <Detail label={t('type')} value={file.mimeType || t('unknownType')} />
              <Detail label={t('size')} value={formatBytes(file.sizeBytes, fm)} />
              <Detail label={t('uploaded')} value={timeAgo(file.uploadedAt, undefined, tr, fm.locale)} />
              <Detail label={t('origin')} value={file.category === 'outputFiles' ? t('originRender') : file.category === 'agentFiles' ? t('originAgent') : t('originUser')} />
              <Group justify="space-between" mih={32}>
                <Text size="sm" c="dimmed">{t('searchIndex')}</Text>
                <Group gap="xs">
                  <Badge variant="light" color={file.indexingStatus === 'Indexed' ? 'green' : file.indexingStatus === 'Failed' ? 'red' : 'gray'}>{indexLabel(file.indexingStatus)}</Badge>
                  <Button size="compact-sm" variant="subtle" leftSection={<IconRefresh size={14} />} onClick={() => onReindex(file)}>{t('reindex')}</Button>
                </Group>
              </Group>
              {file.indexingError && <Text size="xs" c="red">{file.indexingError}</Text>}
            </Stack>
          </Tabs.Panel>
        </Tabs>

        <Group gap="xs">
          <Button variant="default" leftSection={<IconDownload size={16} />} onClick={() => onDownload(file)} style={{ minHeight: 44 }}>{t('download')}</Button>
          {onRename && <Button variant="default" leftSection={<IconPencil size={16} />} onClick={() => onRename(file)} style={{ minHeight: 44 }}>{t('rename')}</Button>}
          <Button variant="default" leftSection={<IconArrowsMove size={16} />} onClick={() => onMove(file)} style={{ minHeight: 44 }}>{t('move')}</Button>
          <Button variant="default" color="red" leftSection={<IconTrash size={16} />} onClick={() => onDelete(file)} style={{ minHeight: 44 }}>{t('delete')}</Button>
        </Group>

        {onEditAsThumbnail && canEditAsThumbnail(file) && (
          <Button variant="light" leftSection={<IconPhotoEdit size={16} />} style={{ minHeight: 44 }} onClick={() => onEditAsThumbnail(file)}>
            {t('editThumbnail')}
          </Button>
        )}

        <Button
          variant="light" leftSection={<IconWand size={16} />} style={{ minHeight: 44 }}
          onClick={() => openWithPrompt(t('askPrompt', { path }), { projectId, fileId: file.id })}
        >
          {t('askAssistant')}
        </Button>

      </Stack>
    </Drawer>
  );
}

function SummaryBody({ raw }: { raw: string }) {
  const { text, extras } = parseFileSummary(raw);
  return (
    <Stack gap="xs">
      {text && <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>{text}</Text>}
      {extras.map((x) => (
        <div key={x.label}>
          <Text size="xs" c="dimmed" fw={600}>{x.label}</Text>
          <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>{x.text}</Text>
        </div>
      ))}
    </Stack>
  );
}

function Detail({ label, value }: { label: string; value: string }) {
  return (
    <Group justify="space-between" wrap="nowrap" gap="md" mih={28}>
      <Text size="sm" c="dimmed">{label}</Text>
      <Text size="sm" ta="right" style={{ wordBreak: 'break-all' }}>{value}</Text>
    </Group>
  );
}
