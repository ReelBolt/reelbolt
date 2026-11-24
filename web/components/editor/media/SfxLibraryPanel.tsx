'use client';

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Badge, Button, Chip, Group, Loader, Select, Stack, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconCheck, IconPlayerPlay, IconPlayerStop, IconPlus, IconSearch } from '@tabler/icons-react';
import { EarlyPreviewLabel } from '../shell/EarlyPreviewLabel';
import { ApiError } from '@/lib/api/client';
import {
  SFX_LIBRARY_PROJECT_CAP,
  SFX_LIBRARY_RATE_LIMIT,
  addSfxLibraryItem,
  addSfxStarterSet,
  sfxPreviewUrl,
  type SfxLibraryItem,
} from '@/lib/api/sfx-library';
import { useSfxLibrary } from '@/lib/hooks/use-sfx-library';
import { useMediaPool } from '@/lib/hooks/use-media';
import { formatDuration } from '@/lib/editing/media/format';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import type { T } from '@/i18n/translator';
import { useT } from '@/i18n';

const SEARCH_DEBOUNCE_MS = 300;
/** Duration filter choices (maximum length, in ms). */
const DURATION_CHOICES = ['1000', '3000', '10000'] as const;

export interface SfxLibraryPanelProps {
  projectId: string;
  readOnly?: boolean;
  /** Puts a pool item on the timeline at the playhead. Returns a plain refusal, or null when it landed. */
  onPlaceItem?: (item: MediaPoolItem) => string | null | void;
  /** Phone: 44 px controls. */
  touch?: boolean;
}

function addErrorText(t: T, e: unknown): string {
  if (e instanceof ApiError) {
    if (e.code === SFX_LIBRARY_PROJECT_CAP) return t('capReached');
    if (e.code === SFX_LIBRARY_RATE_LIMIT || e.status === 429) return t('rateLimited');
    if (e.status === 404) return t('gone');
  }
  return t('addFailed');
}

/**
 * The "Sound effects" tab: browse and preview the built-in library, add a sound to the project and put it on the timeline.
 * Play and stop are a click or tap, never a hover; one sound plays at a time through one plain audio element.
 * The add is the API call; placing reuses the pool's own place path through `onPlaceItem`. No op is produced here.
 */
export function SfxLibraryPanel({ projectId, readOnly = false, onPlaceItem, touch = false }: SfxLibraryPanelProps) {
  const t = useT('editor-media.sfx');
  const [search, setSearch] = useState('');
  const [category, setCategory] = useState<string | null>(null);
  const [maxDuration, setMaxDuration] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [debounced] = useDebouncedValue(search, SEARCH_DEBOUNCE_MS);
  const [playingId, setPlayingId] = useState<string | null>(null);
  const [addingId, setAddingId] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const [addedIds, setAddedIds] = useState<ReadonlySet<string>>(new Set());
  const [problem, setProblem] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const { mutate: mutatePool } = useMediaPool(projectId);

  const { data, error, isLoading } = useSfxLibrary(true, {
    search: debounced,
    category,
    maxDurationMs: maxDuration === null ? null : Number(maxDuration),
    page,
  });
  const items = data?.items ?? [];
  const pages = data ? Math.max(1, Math.ceil(data.total / Math.max(1, data.pageSize))) : 1;
  const control = touch ? 44 : undefined;

  useEffect(() => {
    const audio = audioRef.current;
    return () => audio?.pause();
  }, []);

  const toggle = useCallback(
    (item: SfxLibraryItem) => {
      const audio = audioRef.current;
      if (!audio) return;
      setProblem(null);
      if (playingId === item.id) {
        audio.pause();
        setPlayingId(null);
        return;
      }
      audio.pause();
      audio.src = sfxPreviewUrl(item.id);
      setPlayingId(item.id);
      void Promise.resolve(audio.play()).catch(() => {
        setPlayingId(null);
        setProblem(t('playFailed'));
      });
    },
    [playingId, t],
  );

  const placeAdded = useCallback(
    async (mediaRef: string) => {
      const pool = await mutatePool();
      const poolItem = pool?.find((p) => p.mediaRef === mediaRef);
      if (!poolItem || !onPlaceItem) return null;
      return onPlaceItem(poolItem) ?? null;
    },
    [mutatePool, onPlaceItem],
  );

  const add = async (item: SfxLibraryItem) => {
    setAddingId(item.id);
    setProblem(null);
    setMessage(null);
    try {
      const result = await addSfxLibraryItem(projectId, item.id);
      setAddedIds((prev) => new Set(prev).add(item.id));
      const refusal = await placeAdded(result.mediaRef);
      if (refusal) setProblem(refusal);
      else setMessage(onPlaceItem ? t('addedAndPlaced', { name: item.title }) : t('added', { name: item.title }));
    } catch (e) {
      setProblem(addErrorText(t, e));
    } finally {
      setAddingId(null);
    }
  };

  const addStarterSet = async () => {
    setStarting(true);
    setProblem(null);
    setMessage(null);
    try {
      const result = await addSfxStarterSet(projectId);
      await mutatePool();
      setMessage(t('starterAdded', { count: result.items.length }));
    } catch (e) {
      setProblem(addErrorText(t, e));
    } finally {
      setStarting(false);
    }
  };

  return (
    <Stack gap="sm" p="xs" data-testid="sfx-panel">
      <Group gap="xs" justify="space-between" wrap="nowrap">
        <Text fw={600} size="sm">{t('title')}</Text>
        <EarlyPreviewLabel />
      </Group>
      <Text size="xs" c="dimmed">{t('intro')}</Text>
      <TextInput
        aria-label={t('search')}
        placeholder={t('searchPlaceholder')}
        leftSection={<IconSearch size={14} />}
        value={search}
        onChange={(e) => { setSearch(e.currentTarget.value); setPage(1); }}
        maxLength={100}
        styles={control ? { input: { minHeight: control, fontSize: 16 } } : undefined}
        data-testid="sfx-search"
      />
      <Group gap={6} wrap="wrap" role="group" aria-label={t('categories')} data-testid="sfx-categories">
        <Chip size="xs" checked={category === null} onChange={() => { setCategory(null); setPage(1); }} variant="outline">
          {t('allCategories')}
        </Chip>
        {(data?.categories ?? []).map((c) => (
          <Chip
            key={c.category}
            size="xs"
            variant="outline"
            checked={category === c.category}
            onChange={() => { setCategory(category === c.category ? null : c.category); setPage(1); }}
          >
            {c.category} ({c.count})
          </Chip>
        ))}
      </Group>
      <Select
        aria-label={t('duration')}
        placeholder={t('anyDuration')}
        data={DURATION_CHOICES.map((v) => ({ value: v, label: t('maxSeconds', { seconds: Number(v) / 1000 }) }))}
        value={maxDuration}
        onChange={(v) => { setMaxDuration(v); setPage(1); }}
        clearable
        allowDeselect
        styles={control ? { input: { minHeight: control, fontSize: 16 } } : undefined}
        data-testid="sfx-duration"
      />
      <Button
        variant="default"
        size="xs"
        fz="xs"
        h={control}
        leftSection={<IconPlus size={14} />}
        loading={starting}
        disabled={readOnly}
        onClick={() => void addStarterSet()}
        data-testid="sfx-starter-set"
      >
        {t('starterSet')}
      </Button>

      {problem ? <Alert color="red" variant="light" data-testid="sfx-problem">{problem}</Alert> : null}
      {message ? <Text size="xs" c="green" role="status" data-testid="sfx-message">{message}</Text> : null}
      {error ? <Alert color="red" variant="light" data-testid="sfx-load-error">{t('loadFailed')}</Alert> : null}
      {isLoading ? <Loader size="sm" aria-label={t('loading')} /> : null}
      {!isLoading && !error && items.length === 0 ? <Text size="xs" c="dimmed" data-testid="sfx-empty">{t('empty')}</Text> : null}

      <Stack gap={4} data-testid="sfx-list">
        {items.map((item) => {
          const playing = playingId === item.id;
          const added = addedIds.has(item.id);
          return (
            <Group key={item.id} gap="xs" wrap="nowrap" align="center" data-testid="sfx-row" data-playing={playing ? 'true' : undefined}>
              <Button
                variant={playing ? 'filled' : 'default'}
                size="compact-sm"
                fz="xs"
                h={control}
                miw={control}
                px={8}
                aria-label={playing ? t('stop', { name: item.title }) : t('play', { name: item.title })}
                onClick={() => toggle(item)}
                data-testid="sfx-play"
              >
                {playing ? <IconPlayerStop size={16} /> : <IconPlayerPlay size={16} />}
              </Button>
              <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                <Text size="sm" truncate>{item.title}</Text>
                <Group gap={4} wrap="wrap">
                  {item.tags.slice(0, 3).map((tag) => (
                    <Badge key={tag} size="xs" variant="light" fz={12} tt="none">{tag}</Badge>
                  ))}
                </Group>
              </Stack>
              <Text size="xs" c="dimmed" data-testid="sfx-duration-label">{formatDuration(item.durationMs / 1000)}</Text>
              <Button
                size="compact-sm"
                fz="xs"
                h={control}
                miw={control}
                variant={added ? 'light' : 'filled'}
                leftSection={added ? <IconCheck size={14} /> : <IconPlus size={14} />}
                loading={addingId === item.id}
                disabled={readOnly}
                aria-label={t('addNamed', { name: item.title })}
                onClick={() => void add(item)}
                data-testid="sfx-add"
              >
                {added ? t('addedLabel') : t('add')}
              </Button>
            </Group>
          );
        })}
      </Stack>

      {pages > 1 ? (
        <Group gap="xs" justify="center">
          <Button size="sm" fz="xs" h={control} variant="default" disabled={page <= 1} onClick={() => setPage((p) => p - 1)} data-testid="sfx-prev">{t('previous')}</Button>
          <Text size="xs">{t('pageOf', { page, pages })}</Text>
          <Button size="sm" fz="xs" h={control} variant="default" disabled={page >= pages} onClick={() => setPage((p) => p + 1)} data-testid="sfx-next">{t('next')}</Button>
        </Group>
      ) : null}

      <audio ref={audioRef} preload="none" onEnded={() => setPlayingId(null)} onError={() => { setPlayingId(null); }} data-testid="sfx-audio" />
    </Stack>
  );
}
