'use client';

import { useState } from 'react';
import { ActionIcon, Button, Group, Modal, SimpleGrid, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconChevronLeft, IconChevronRight, IconDownload } from '@tabler/icons-react';
import { useT } from '@/i18n';

export interface GalleryPicture {
  id: string;
  /** Inline URL of the picture (the output download route, never a storage key). */
  src: string;
  /** Optional large (not full-size) picture for the modal; falls back to `src`. */
  largeSrc?: string;
  /** Optional small preview for the grid (the modal always uses the full `src`). */
  thumbSrc?: string;
  /** URL that saves the picture as a file. */
  downloadUrl: string;
  fileName: string;
  /** Optional item label (for example the colour this picture was made for). Never required. */
  caption?: string | null;
}

const DOWNLOAD_GAP_MS = 400;

/**
 * All the pictures of a run together: a responsive grid, each opens full size with previous/next, each can be
 * downloaded, and "Download all" saves them one after the other. No draft/final distinction for pictures.
 */
export function PictureGallery({ pictures }: { pictures: readonly GalleryPicture[] }) {
  const t = useT('outputs.gallery');
  const [index, setIndex] = useState<number | null>(null);
  const current = index === null ? null : pictures[index] ?? null;
  const label = (p: GalleryPicture, i: number) => p.caption || t('pictureN', { n: i + 1 });

  const downloadAll = async () => {
    for (const p of pictures) {
      const a = document.createElement('a');
      a.href = p.downloadUrl;
      a.download = p.fileName;
      document.body.appendChild(a);
      a.click();
      a.remove();
      await new Promise((r) => setTimeout(r, DOWNLOAD_GAP_MS));
    }
  };

  return (
    <Stack gap="sm" data-testid="picture-gallery">
      <Group justify="space-between" wrap="nowrap">
        <Text fw={600} size="sm">{t('title', { count: pictures.length })}</Text>
        {pictures.length > 1 && (
          <Button variant="default" size="xs" leftSection={<IconDownload size={14} />} onClick={() => void downloadAll()} style={{ minHeight: 36 }}>
            {t('downloadAll')}
          </Button>
        )}
      </Group>
      <SimpleGrid cols={{ base: 2, sm: 3, lg: 4 }} spacing="sm" role="list" aria-label={t('listLabel')}>
        {pictures.map((p, i) => (
          <Stack key={p.id} gap={4} role="listitem">
            <UnstyledButton onClick={() => setIndex(i)} aria-label={t('open', { name: label(p, i) })}>
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img src={p.thumbSrc ?? p.src} alt={label(p, i)} loading="lazy" decoding="async" style={{ width: '100%', aspectRatio: '1 / 1', objectFit: 'cover', borderRadius: 'var(--mantine-radius-md)', display: 'block' }} />
            </UnstyledButton>
            <Group justify="space-between" gap={4} wrap="nowrap">
              <Text size="xs" c="dimmed" lineClamp={1}>{p.caption ?? ''}</Text>
              <ActionIcon component="a" href={p.downloadUrl} download={p.fileName} variant="subtle" size={32} aria-label={t('download', { name: label(p, i) })}>
                <IconDownload size={16} />
              </ActionIcon>
            </Group>
          </Stack>
        ))}
      </SimpleGrid>
      <Modal
        opened={current !== null}
        onClose={() => setIndex(null)}
        size="xl"
        centered
        title={current && index !== null ? `${label(current, index)} (${index + 1}/${pictures.length})` : ''}
        closeButtonProps={{ size: 44, 'aria-label': t('close') }}
      >
        {current && index !== null && (
          <Stack gap="sm">
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img src={current.largeSrc ?? current.src} alt={label(current, index)} style={{ width: '100%', maxHeight: '70vh', objectFit: 'contain', display: 'block' }} />
            <Group justify="space-between">
              <Group gap="xs">
                <ActionIcon variant="default" size={44} disabled={index === 0} onClick={() => setIndex(index - 1)} aria-label={t('previous')}>
                  <IconChevronLeft size={20} />
                </ActionIcon>
                <ActionIcon variant="default" size={44} disabled={index >= pictures.length - 1} onClick={() => setIndex(index + 1)} aria-label={t('next')}>
                  <IconChevronRight size={20} />
                </ActionIcon>
              </Group>
              <Button component="a" href={current.downloadUrl} download={current.fileName} leftSection={<IconDownload size={16} />} style={{ minHeight: 44 }}>
                {t('downloadThis')}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}
