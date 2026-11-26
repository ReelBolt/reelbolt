'use client';

import React, { useState, useSyncExternalStore } from 'react';
import Link from 'next/link';
import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconCopy, IconDownload, IconFolderSearch, IconShare3, IconTrash } from '@tabler/icons-react';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';
import { formatBytes, timeAgo } from '@/lib/files/model';
import { shareModeOf, type ShareMode } from './deliver-phone';
import { formatLength, formatNameMsg, renderStatusLineMsg } from './deliver-model';
import { useT } from '@/i18n';
import { PostTextButton } from '@/components/outputs/PostTextDialog';
import { PublishButton } from '@/components/publishing/PublishDialog';
import { CheckedAudio, CheckedVideo } from '@/components/outputs/CheckedVideo';
import { useDownloadGuard } from '@/lib/outputs/output-check';
import { EDITOR_MODAL_Z } from '@/components/editor/ui/z-index';

export interface FilmCardProps {
  projectId: string;
  render: EditTimelineRenderSummary;
  /** The revision on screen, for the status line. */
  revision: number;
  /** The playable URL of the film (the inline stream; never a storage key). */
  playUrl: string;
  /** The URL that saves the film as a file. */
  downloadUrl: string;
  /** Resolves true once the film is gone; false keeps the dialog open (the panel has said why). */
  onDelete: (render: EditTimelineRenderSummary) => Promise<boolean>;
  onCopyLink: (render: EditTimelineRenderSummary) => void;
  /** Phone: offer Share (the file itself when the device can, else the link, else no button). */
  share?: boolean;
  /** The Deliver page on a timeline the person cannot change: the film can be watched and saved, not deleted. */
  readOnly?: boolean;
}

const BUTTON_HEIGHT = { minHeight: 44 } as const;

const noopSubscribe = (): (() => void) => () => {};

const mimeOf = (fileName: string): string => {
  const ext = fileName.split('.').pop()?.toLowerCase();
  return ext === 'mp3' ? 'audio/mpeg' : ext === 'wav' ? 'audio/wav' : ext === 'mov' ? 'video/quicktime' : ext === 'webm' ? 'video/webm' : 'video/mp4';
};

/** The path of the project's Files tab, focused on one file. */
export const filesPath = (projectId: string, fileId: string): string => `/projects/${projectId}?tab=files&file=${encodeURIComponent(fileId)}`;

/**
 * The latest finished film, first thing in Deliver: the player, its facts (length, size, how long ago) and the
 * four things people do with a film. Delete asks first; the row leaves the queue once the server removed it.
 */
export function FilmCard({ projectId, render, revision, playUrl, downloadUrl, onDelete, onCopyLink, share = false, readOnly = false }: FilmCardProps) {
  const t = useT('editor.deliver.filmCard');
  const guardDownload = useDownloadGuard();
  const [sharing, setSharing] = useState(false);
  const fileName = render.outputFileName;
  const hasLink = Boolean(render.outputFileId);
  // What this device can share is only known in the browser; the server render shows no Share button.
  const shareMode = useSyncExternalStore(
    noopSubscribe,
    (): ShareMode => (share ? shareModeOf(navigator, new File([new Uint8Array(1)], fileName, { type: mimeOf(fileName) }), hasLink) : 'none'),
    (): ShareMode => 'none',
  );

  const onShare = async () => {
    setSharing(true);
    try {
      if (shareMode === 'files') {
        const response = await fetch(downloadUrl, { credentials: 'include' });
        if (!response.ok) throw new Error('download failed');
        const blob = await response.blob();
        await navigator.share({ files: [new File([blob], fileName, { type: blob.type || mimeOf(fileName) })], title: fileName });
      } else if (render.outputFileId) {
        await navigator.share({ title: fileName, url: `${window.location.origin}${filesPath(projectId, render.outputFileId)}` });
      }
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError')) {
        notifications.show({ color: 'red', title: t('couldNotShare'), message: t('sharingDidNotWork') });
      }
    } finally {
      setSharing(false);
    }
  };
  const [confirming, setConfirming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const audioOnly = render.preset === 'AudioOnly';
  const facts = [
    formatLength(render.durationSec),
    render.sizeBytes ? formatBytes(render.sizeBytes) : null,
    ((name) => (typeof name === 'string' ? name : t.msg(name)))(formatNameMsg(render.preset)),
    timeAgo(render.completedAt ?? render.createdAt ?? render.startedAt),
  ].filter((x): x is string => x !== null);

  const confirmDelete = async () => {
    setDeleting(true);
    try {
      if (await onDelete(render)) setConfirming(false);
    } finally {
      setDeleting(false);
    }
  };

  return (
    <Stack gap="xs" data-testid="film-card">
      {audioOnly ? (
        <CheckedAudio controls src={playUrl} aria-label={t('previewOf', { outputFileName: render.outputFileName })} style={{ width: '100%' }} data-testid="film-player" />
      ) : (
        <CheckedVideo controls preload="metadata" playsInline src={playUrl} aria-label={t('previewOf2', { outputFileName: render.outputFileName })} style={{ width: '100%', maxHeight: 'min(34vh, 300px)', background: 'var(--mantine-color-dark-9)', borderRadius: 'var(--mantine-radius-md)' }} data-testid="film-player" />
      )}
      <Text fw={600} size="sm" truncate title={render.outputFileName} data-testid="film-name">
        {render.outputFileName}
      </Text>
      <Text size="xs" c="dimmed" data-testid="film-facts">
        {facts.join(' · ')}
      </Text>
      <Group gap="xs" data-testid="film-actions">
        <Button component="a" href={downloadUrl} onClick={guardDownload(downloadUrl, render.outputFileName)} download={render.outputFileName} leftSection={<IconDownload size={16} />} style={BUTTON_HEIGHT} data-testid="film-download">
          {t('download')}
        </Button>
        {share && shareMode !== 'none' ? (
          <Button onClick={() => void onShare()} loading={sharing} leftSection={<IconShare3 size={16} />} style={BUTTON_HEIGHT} data-testid="film-share" data-share-mode={shareMode}>
            {shareMode === 'files' ? t('shareFilm') : t('shareLink')}
          </Button>
        ) : null}
        {render.outputFileId ? (
          <Button component={Link} href={filesPath(projectId, render.outputFileId)} variant="light" leftSection={<IconFolderSearch size={16} />} style={BUTTON_HEIGHT} data-testid="film-open-files">
            {t('openInProjectFiles')}
          </Button>
        ) : null}
        {render.outputFileId ? (
          <Button variant="light" leftSection={<IconCopy size={16} />} onClick={() => onCopyLink(render)} style={BUTTON_HEIGHT} data-testid="film-copy-link">
            {t('copyLink')}
          </Button>
        ) : null}
        {!readOnly && (
          <Button variant="light" color="red" leftSection={<IconTrash size={16} />} onClick={() => setConfirming(true)} style={BUTTON_HEIGHT} data-testid="film-delete">
            {t('delete')}
          </Button>
        )}
        {!audioOnly && render.outputFileId && <PostTextButton projectId={projectId} outputId={render.outputFileId} />}
        {!audioOnly && render.outputFileId && <PublishButton projectId={projectId} outputId={render.outputFileId} outputKind="project_file" />}
      </Group>
      {render.captionFiles && render.captionFiles.length > 0 && (
        <Stack gap={4} data-testid="film-caption-files">
          <Text size="xs" fw={600}>{t('captionFiles')}</Text>
          {render.captionFiles.map((f) => (
            <Group key={f.fileId} gap="xs" wrap="wrap">
              <Text size="xs" truncate>{f.fileName} · {formatBytes(f.sizeBytes)}</Text>
              <Button component="a" size="xs" fz="xs" variant="light" href={`/api/v1/projects/${projectId}/files/${f.fileId}/download`} download={f.fileName} leftSection={<IconDownload size={14} />} style={BUTTON_HEIGHT} data-testid={`film-caption-${f.format}`}>
                {t('download2')} {f.format === 'srt' ? t('srt') : t('webvtt')}
              </Button>
            </Group>
          ))}
        </Stack>
      )}
      <Text size="xs" c="dimmed" data-testid="film-status-line">
        {t.msg(renderStatusLineMsg(render.revision, revision))}
      </Text>
      <Modal zIndex={EDITOR_MODAL_Z} opened={confirming} onClose={() => (deleting ? undefined : setConfirming(false))} title={t('deleteThisFilm')} centered closeButtonProps={{ size: 44 }}>
        <Stack gap="sm">
          <Text size="sm">
            {t('willBeRemovedFrom', { outputFileName: render.outputFileName })}
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirming(false)} disabled={deleting} style={BUTTON_HEIGHT}>
              {t('keepIt')}
            </Button>
            <Button color="red" onClick={() => void confirmDelete()} loading={deleting} style={BUTTON_HEIGHT} data-testid="film-delete-confirm">
              {t('deleteFilm')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
