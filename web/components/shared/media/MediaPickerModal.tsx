'use client';

import { useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { ActionIcon, Button, Group, Modal, Skeleton, Text, TextInput } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconEye, IconMusic, IconPhoto, IconSearch, IconVideo } from '@tabler/icons-react';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useMediaPool } from '@/lib/hooks/use-media';
import { projectFilePreviewUrl, projectFileUrl } from '@/lib/api/files';
import { PHONE_MEDIA_QUERY } from '@/lib/editing/device-class';
import { formatBytes, formatDuration } from '@/lib/editing/media/format';
import type { ProjectFile } from '@/lib/types/project';
import { useT } from '@/i18n';
import {
  PREVIEW_WIDTH,
  THUMB_WIDTH,
  canConfirm,
  effectiveMax,
  fileKindOf,
  filterMediaFiles,
  gridNavigate,
  mediaRefOfFile,
  poolByFileId,
  selectAllVisible,
  toggleSelection,
  type GridKey,
  type MediaFileKind,
  type MediaPickerKind,
} from './media-picker-model';
import { useNearViewport, useVideoPoster } from './use-video-poster';

export type PickedMediaFile = ProjectFile;

export interface MediaPickerModalProps {
  opened: boolean;
  onClose(): void;
  projectId: string;
  kind: MediaPickerKind;
  multiple?: boolean;
  /** Pre-selected file ids. */
  value?: string[];
  onConfirm(files: PickedMediaFile[]): void;
  title?: string;
  maxSelect?: number;
  minSelect?: number;
  /** Extra rule on top of `kind` (e.g. only the project's own uploads). Keep the function stable. */
  filter?: (file: ProjectFile) => boolean;
}

const KIND_ICON = { image: IconPhoto, video: IconVideo, audio: IconMusic } as const;
const GRID_KEYS: readonly string[] = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'];
const TILE_MIN = 44;

function KindPlaceholder({ kind }: { kind: MediaFileKind }) {
  const Icon = KIND_ICON[kind];
  return (
    <div style={{ width: '100%', height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'var(--mantine-color-dark-8)' }} data-testid="media-picker-placeholder">
      <Icon size={32} color="var(--mantine-color-gray-5)" aria-hidden />
    </div>
  );
}

/** A tile's picture: a small WebP for images, the filmstrip's first frame for videos, an icon otherwise. Never the original. */
export function MediaThumb({ projectId, file, poolRef }: { projectId: string; file: ProjectFile; poolRef: string | null }) {
  const kind = fileKindOf(file);
  const [ref, near] = useNearViewport();
  const poster = useVideoPoster(projectId, kind === 'video' ? poolRef : null, near);
  return (
    <div ref={ref} style={{ width: '100%', aspectRatio: '16 / 10', overflow: 'hidden', borderRadius: 6 }}>
      {kind === 'image' ? (
        // eslint-disable-next-line @next/next/no-img-element
        <img
          src={projectFilePreviewUrl(projectId, file.id, THUMB_WIDTH)}
          alt=""
          loading="lazy"
          decoding="async"
          draggable={false}
          style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }}
        />
      ) : kind === 'video' && poster ? (
        <div
          data-testid="media-picker-poster"
          style={{ width: '100%', height: '100%', backgroundImage: `url("${poster.url}")`, backgroundSize: poster.backgroundSize, backgroundPosition: poster.backgroundPosition, backgroundRepeat: 'no-repeat' }}
        />
      ) : (
        <KindPlaceholder kind={kind ?? 'video'} />
      )}
    </div>
  );
}

interface PreviewState {
  file: ProjectFile;
}

function PreviewOverlay({ projectId, preview, onClose }: { projectId: string; preview: PreviewState | null; onClose(): void }) {
  const t = useT('mediaPicker.preview');
  const file = preview?.file;
  const kind = file ? fileKindOf(file) : null;
  return (
    <Modal opened={preview !== null} onClose={onClose} title={file ? t('title', { name: file.originalFileName }) : ''} size="xl" centered closeButtonProps={{ 'aria-label': t('close') }} zIndex={400}>
      {file && kind === 'image' && (
        // eslint-disable-next-line @next/next/no-img-element
        <img src={projectFilePreviewUrl(projectId, file.id, PREVIEW_WIDTH)} alt={file.originalFileName} style={{ width: '100%', maxHeight: '70vh', objectFit: 'contain', display: 'block' }} />
      )}
      {file && kind === 'video' && (
        <video src={projectFileUrl(projectId, file.id)} controls preload="metadata" playsInline style={{ width: '100%', maxHeight: '70vh' }} data-testid="media-picker-preview-video" />
      )}
      {file && kind === 'audio' && <audio src={projectFileUrl(projectId, file.id)} controls preload="metadata" style={{ width: '100%' }} />}
    </Modal>
  );
}

interface BodyProps extends Omit<MediaPickerModalProps, 'opened' | 'title'> {
  phone: boolean;
  onCancel(): void;
}

function MediaPickerBody({ projectId, kind, multiple = false, value, onConfirm, maxSelect, minSelect, filter, phone, onCancel }: BodyProps) {
  const t = useT('mediaPicker');
  const { data: files, error, isLoading } = useProjectFiles(projectId);
  const { data: pool } = useMediaPool(projectId);
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<string[]>(() => [...(value ?? [])]);
  const [active, setActive] = useState(0);
  const [preview, setPreview] = useState<PreviewState | null>(null);
  const gridRef = useRef<HTMLDivElement>(null);

  const rules = useMemo(() => ({ multiple, maxSelect, minSelect }), [multiple, maxSelect, minSelect]);
  const visible = useMemo(() => filterMediaFiles(files, kind, query, filter), [files, kind, query, filter]);
  const offered = useMemo(() => filterMediaFiles(files, kind, '', filter), [files, kind, filter]);
  const pooled = useMemo(() => poolByFileId(pool), [pool]);
  const max = effectiveMax(rules);
  const atCap = selected.length >= max;
  const ok = canConfirm(selected.length, rules);
  const role = multiple ? 'checkbox' : 'radio';

  const columns = (): number => {
    const tiles = gridRef.current?.querySelectorAll<HTMLElement>('[data-tile]');
    if (!tiles || tiles.length === 0) return 1;
    const top = tiles[0].offsetTop;
    let n = 0;
    while (n < tiles.length && tiles[n].offsetTop === top) n += 1;
    return Math.max(n, 1);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLButtonElement>, index: number) => {
    if (!GRID_KEYS.includes(e.key)) return;
    e.preventDefault();
    const next = gridNavigate(index, e.key as GridKey, visible.length, phone ? 2 : columns());
    setActive(next);
    gridRef.current?.querySelectorAll<HTMLElement>('[data-tile]')[next]?.focus();
  };

  const confirm = () => {
    if (!ok) return;
    const byId = new Map((files ?? []).map((f) => [f.id, f]));
    onConfirm(selected.map((id) => byId.get(id)).filter((f): f is ProjectFile => f !== undefined));
  };

  const hint = multiple && atCap && Number.isFinite(max) ? t('limit', { max }) : !ok && (minSelect ?? 1) > 1 ? t('needMin', { min: minSelect ?? 1 }) : null;
  const countText = multiple && Number.isFinite(max) ? t('selectedMax', { count: selected.length, max }) : t('selected', { count: selected.length });

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: phone ? 'calc(100dvh - 76px)' : 'min(70vh, 640px)' }}>
      <Group gap="xs" wrap="wrap" mb="sm" style={{ flex: '0 0 auto' }}>
        <TextInput
          style={{ flex: '1 1 200px' }}
          aria-label={t('search.label')}
          placeholder={t('search.placeholder')}
          leftSection={<IconSearch size={16} />}
          value={query}
          onChange={(e) => { setQuery(e.currentTarget.value); setActive(0); }}
          styles={{ input: { minHeight: TILE_MIN } }}
        />
        {multiple && (
          <>
            <Button variant="default" style={{ minHeight: TILE_MIN }} disabled={visible.length === 0 || atCap} onClick={() => setSelected((cur) => selectAllVisible(cur, visible.map((f) => f.id), rules))}>
              {t('selectAll')}
            </Button>
            <Button variant="default" style={{ minHeight: TILE_MIN }} disabled={selected.length === 0} onClick={() => setSelected([])}>
              {t('clear')}
            </Button>
          </>
        )}
      </Group>

      <div style={{ flex: '1 1 auto', overflowY: 'auto', minHeight: 0 }}>
        {isLoading ? (
          <div role="status" aria-label={t('loading')} style={{ display: 'grid', gridTemplateColumns: phone ? 'repeat(2, 1fr)' : 'repeat(auto-fill, minmax(150px, 1fr))', gap: 8 }}>
            {Array.from({ length: 6 }, (_, i) => <Skeleton key={i} height={110} radius="md" data-testid="media-picker-skeleton" />)}
          </div>
        ) : error ? (
          <Text c="red" size="sm" role="alert">{t('loadError')}</Text>
        ) : visible.length === 0 ? (
          <Text size="sm" c="dimmed" role="status">{offered.length === 0 ? t('empty.none') : t('empty.search', { query: query.trim() })}</Text>
        ) : (
          <div
            ref={gridRef}
            role={multiple ? 'group' : 'radiogroup'}
            aria-label={t('gridLabel')}
            style={{ display: 'grid', gridTemplateColumns: phone ? 'repeat(2, 1fr)' : 'repeat(auto-fill, minmax(150px, 1fr))', gap: 8 }}
          >
            {visible.map((f, index) => {
              const checked = selected.includes(f.id);
              const fk = fileKindOf(f);
              const item = pooled.get(f.id);
              const duration = fk === 'video' || fk === 'audio' ? item?.probe?.durationSec : null;
              const meta = duration != null ? t('tileMetaDuration', { size: formatBytes(f.sizeBytes), duration: formatDuration(duration) }) : formatBytes(f.sizeBytes);
              const blocked = !checked && multiple && atCap;
              return (
                <div key={f.id} style={{ position: 'relative' }}>
                  <button
                    type="button"
                    role={role}
                    aria-checked={checked}
                    aria-label={f.originalFileName}
                    aria-disabled={blocked || undefined}
                    data-tile
                    data-file-id={f.id}
                    tabIndex={index === Math.min(active, visible.length - 1) ? 0 : -1}
                    onFocus={() => setActive(index)}
                    onKeyDown={(e) => onKeyDown(e, index)}
                    onClick={() => { if (!blocked) setSelected((cur) => toggleSelection(cur, f.id, rules)); }}
                    style={{
                      display: 'block', width: '100%', minHeight: TILE_MIN, padding: 4, textAlign: 'left', cursor: blocked ? 'var(--rb-cursor-not-allowed)' : 'var(--rb-cursor-pointer)',
                      opacity: blocked ? 0.55 : 1, color: 'inherit', background: 'var(--mantine-color-body)', borderRadius: 8,
                      border: checked ? '2px solid var(--mantine-primary-color-filled)' : '2px solid var(--mantine-color-default-border)',
                    }}
                  >
                    <MediaThumb projectId={projectId} file={f} poolRef={item ? mediaRefOfFile(f.id) : null} />
                    <Text size="sm" fw={500} lineClamp={1} mt={4} title={f.originalFileName}>{f.originalFileName}</Text>
                    <Text size="xs" c="dimmed">{meta}</Text>
                  </button>
                  <ActionIcon
                    variant="filled"
                    color="dark"
                    size={TILE_MIN}
                    radius="xl"
                    aria-label={t('preview.open', { name: f.originalFileName })}
                    onClick={() => setPreview({ file: f })}
                    style={{ position: 'absolute', top: 8, right: 8, opacity: 0.9 }}
                  >
                    <IconEye size={18} />
                  </ActionIcon>
                </div>
              );
            })}
          </div>
        )}
      </div>

      <div style={{ flex: '0 0 auto', position: 'sticky', bottom: 0, paddingTop: 12, marginTop: 8, background: 'var(--mantine-color-body)', borderTop: '1px solid var(--mantine-color-default-border)' }}>
        <Group justify="space-between" wrap="wrap" gap="xs">
          <div>
            <Text size="sm" role="status" aria-live="polite" data-testid="media-picker-count">{countText}</Text>
            {hint && <Text size="xs" c="dimmed">{hint}</Text>}
          </div>
          <Group gap="xs">
            <Button variant="default" style={{ minHeight: TILE_MIN }} onClick={onCancel}>{t('cancel')}</Button>
            <Button style={{ minHeight: TILE_MIN }} disabled={!ok} onClick={confirm}>{t('confirm')}</Button>
          </Group>
        </Group>
      </div>

      <PreviewOverlay projectId={projectId} preview={preview} onClose={() => setPreview(null)} />
    </div>
  );
}

function defaultTitle(t: ReturnType<typeof useT>, kind: MediaPickerKind, multiple: boolean): string {
  if (multiple) {
    switch (kind) {
      case 'image': return t('titleMulti.image');
      case 'video': return t('titleMulti.video');
      case 'audio': return t('titleMulti.audio');
      case 'videoAudio': return t('titleMulti.videoAudio');
      case 'both': return t('titleMulti.both');
    }
  }
  switch (kind) {
    case 'image': return t('title.image');
    case 'video': return t('title.video');
    case 'audio': return t('title.audio');
    case 'videoAudio': return t('title.videoAudio');
    case 'both': return t('title.both');
  }
}

/**
 * One reusable picker for the project's images and videos: a thumbnail grid with search, single (radio)
 * or multi (checkbox) choice, and a larger preview on demand. Thumbnails are small derivatives, never
 * the original file.
 */
export function MediaPickerModal(props: MediaPickerModalProps) {
  const { opened, onClose, title, kind, multiple = false } = props;
  const t = useT('mediaPicker');
  const phone = useMediaQuery(PHONE_MEDIA_QUERY) ?? false;
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={title ?? defaultTitle(t, kind, multiple)}
      fullScreen={phone}
      size="xl"
      centered={!phone}
      closeButtonProps={{ 'aria-label': t('close'), style: { minWidth: TILE_MIN, minHeight: TILE_MIN } }}
    >
      <MediaPickerBody
        projectId={props.projectId}
        kind={kind}
        multiple={multiple}
        value={props.value}
        maxSelect={props.maxSelect}
        minSelect={props.minSelect}
        filter={props.filter}
        phone={phone}
        onCancel={onClose}
        onClose={onClose}
        onConfirm={props.onConfirm}
      />
    </Modal>
  );
}
