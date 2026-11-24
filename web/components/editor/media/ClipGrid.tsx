'use client';

import React, { useRef } from 'react';
import { Badge, Box, Group, Menu, Stack, Text, UnstyledButton } from '@mantine/core';
import { PLACE_MODES, PLACE_OVER, placeChoiceHintMsg, placeChoiceLabelMsg, type PlaceChoice } from '../place/placeChoices';
import { TilePoster } from './TilePoster';
import { moveTargets, type AnnotationIndex } from '@/lib/editing/media/pool-browse';
import { useMediaQuery } from '@mantine/hooks';
import { IconDots, IconFlag, IconFlagFilled, IconFolderPlus, IconMusic, IconPhoto, IconStar, IconStarFilled, IconVideo } from '@tabler/icons-react';
import { formatBytes, formatDuration, formatProbeSummary } from '@/lib/editing/media/format';
import { CLIP_KIND_MIME_PREFIX, writeDragPayload } from '@/lib/editing/media/bins';
import type { MediaBin, MediaKind, MediaPoolItem, MediaRef } from '@/lib/editing/media/types';
import { EditorIcon } from '../ui';
import { classifyTileScrub } from '@/lib/editing/timeline-gestures';
import { TouchDragChip, useTouchLiftDrag } from './useTouchLiftDrag';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';

/** How the pool is laid out. Grid and list differ in density; filmstrip is a single scrolling row. */
export type MediaViewMode = 'grid' | 'list' | 'filmstrip';

export interface ClipGridProps {
  items: MediaPoolItem[];
  viewMode: MediaViewMode;
  selectedMediaRef: MediaRef | null;
  /** The clip the strip currently describes, drawn as the highlighted tile. */
  subjectMediaRef: MediaRef | null;
  onSelect: (item: MediaPoolItem) => void;
  /**
   * The pointer entered a tile.
   *
   * There is deliberately **no** matching "left" callback. A tile cannot know whether the pointer
   * left it for the strip below — which must keep the subject so the user can actually scrub — or
   * for the rest of the page, which must drop it. Only `MediaPool` can tell those two apart,
   * because only it owns the bounding box that contains both the grid and the strip. Giving the
   * tile a `mouseleave` that cleared the subject is exactly the defect QA vetoed: the strip
   * unmounted before the pointer could arrive.
   *
   * A tile that must not clear on leave is also why the previous `(mediaRef | null)` signature
   * became non-nullable: "nothing is hovered" is no longer a thing a tile can say.
   */
  onHover: (mediaRef: MediaRef) => void;
  /**
   * Focus landed on a tile — the keyboard half of the same act, and the reason the subject no
   * longer depends on a pointer at all. A keyboard user reaches the strip's slider by tabbing
   * through the tiles: focusing one adopts it as the subject, and moving focus onward to the
   * slider does not release it (the release rule lives in `MediaPool`).
   */
  onFocusTile: (mediaRef: MediaRef) => void;
  /**
   * The tile menu's "Add to bin" (the path into a bin that needs no drag, so it works by touch).
   * Without it the tile has no menu.
   */
  addToBin?: { bins: readonly MediaBin[]; onAdd: (item: MediaPoolItem, binId: string) => void };
  /** R05.2: the project's annotations, for the colour dot and the star / flag state. */
  annotations?: AnnotationIndex;
  /** R05.2: one-tap star and flag; each saves at once. Without them the tile shows the state only. */
  onToggleStar?: (item: MediaPoolItem) => void;
  onToggleFlag?: (item: MediaPoolItem) => void;
  /** R05.4: put the source on the timeline (the same four choices as the source viewer). Without it the tile has no Place button. */
  onPlace?: (item: MediaPoolItem, mode: PlaceChoice) => void;
  /** "Enhance..." on a video tile's menu (the new file is only added to the pool). */
  onEnhance?: (item: MediaPoolItem) => void;
  placeDisabled?: boolean;
  /**
   * R04.6: a finger pressed on a tile and dragged sideways. `fraction` is 0..1 across the tile; the pool turns it into a
   * position in the clip. Vertical drags never reach it (the page scrolls). `onTouchScrubEnd` fires when the finger lifts or is taken.
   */
  onTouchScrub?: (mediaRef: MediaRef, fraction: number) => void;
  onTouchScrubEnd?: () => void;
  /** The position label shown over the tile being scrubbed by touch. */
  touchScrubLabel?: { mediaRef: MediaRef; text: string } | null;
  /** Needed for posters. */
  projectId?: string;
  /** Bins by id, for the bin chips on a tile. */
  binNames?: ReadonlyMap<string, string>;
}

/** The tile menu lists placement, enhance and every bin: taller than a short window, so it scrolls inside the room it has. */
export const TILE_MENU_FIT = {
  flip: true,
  shift: true,
  size: {
    padding: 8,
    apply({ availableHeight, elements }: { availableHeight: number; elements: { floating: HTMLElement } }) {
      elements.floating.style.maxHeight = `${Math.max(160, Math.floor(availableHeight))}px`;
      elements.floating.style.overflowY = 'auto';
    },
  },
};

const KIND_ICON: Record<MediaKind, React.ComponentType<{ size?: number }>> = {
  video: IconVideo,
  audio: IconMusic,
  image: IconPhoto,
};

const kindLabels = (t: T): Record<MediaKind, string> => ({
  video: t('kindVideo'),
  audio: t('kindAudio'),
  image: t('kindImage'),
});

/**
 * The pool's clip list.
 *
 * Deliberately image-free: nothing here renders a `<video>`, an `<img>` or a thumbnail, because no
 * usable media URL exists until a derivative row has been fetched and none is fetched until a clip
 * is hovered. A tile therefore shows what is actually known — kind, name, probe summary and size —
 * rather than an empty or broken frame.
 *
 * Each tile is draggable and writes an opaque-mediaRef drag payload, which is what `BinTree` reads
 * to add the clip to a bin.
 */
export function ClipGrid({
  items,
  viewMode,
  selectedMediaRef,
  subjectMediaRef,
  onSelect,
  onHover,
  onFocusTile,
  addToBin,
  onTouchScrub,
  onTouchScrubEnd,
  touchScrubLabel,
  annotations,
  onToggleStar,
  onToggleFlag,
  onPlace,
  onEnhance,
  placeDisabled,
  projectId,
  binNames,
}: ClipGridProps) {
  const tileExtras = { onTouchScrub, onTouchScrubEnd, touchScrubLabel, annotations, onToggleStar, onToggleFlag, onPlace, onEnhance, placeDisabled, projectId, binNames };
  const coarsePointer = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  if (viewMode === 'list') {
    return (
      <Stack gap={4} data-testid="media-clip-list">
        {items.map((item) => (
          <ClipTile
            key={item.mediaRef}
            item={item}
            viewMode="list"
            selected={item.mediaRef === selectedMediaRef}
            subject={item.mediaRef === subjectMediaRef}
            onSelect={onSelect}
            onHover={onHover}
            onFocusTile={onFocusTile}
            addToBin={addToBin}
            coarsePointer={coarsePointer}
            {...tileExtras}
          />
        ))}
      </Stack>
    );
  }

  return (
    <Group
      gap="xs"
      wrap={viewMode === 'filmstrip' ? 'nowrap' : 'wrap'}
      align="stretch"
      data-testid={viewMode === 'filmstrip' ? 'media-clip-filmstrip' : 'media-clip-grid'}
      style={viewMode === 'filmstrip' ? { overflowX: 'auto', paddingBottom: 4 } : undefined}
    >
      {items.map((item) => (
        <ClipTile
          key={item.mediaRef}
          item={item}
          viewMode={viewMode}
          selected={item.mediaRef === selectedMediaRef}
          subject={item.mediaRef === subjectMediaRef}
          onSelect={onSelect}
          onHover={onHover}
          onFocusTile={onFocusTile}
          addToBin={addToBin}
          coarsePointer={coarsePointer}
          {...tileExtras}
        />
      ))}
    </Group>
  );
}

type TileExtras = Pick<ClipGridProps, 'onTouchScrub' | 'onTouchScrubEnd' | 'touchScrubLabel' | 'annotations' | 'onToggleStar' | 'onToggleFlag' | 'onPlace' | 'onEnhance' | 'placeDisabled' | 'projectId' | 'binNames'>;

interface ClipTileProps extends TileExtras {
  item: MediaPoolItem;
  viewMode: MediaViewMode;
  selected: boolean;
  /** Whether this tile is the clip the strip describes. */
  subject: boolean;
  onSelect: (item: MediaPoolItem) => void;
  onHover: (mediaRef: MediaRef) => void;
  onFocusTile: (mediaRef: MediaRef) => void;
  addToBin?: ClipGridProps['addToBin'];
  coarsePointer: boolean;
}

const LABEL_COLOR: Record<string, string> = {
  red: 'var(--mantine-color-red-6)',
  orange: 'var(--mantine-color-orange-6)',
  yellow: 'var(--mantine-color-yellow-5)',
  green: 'var(--mantine-color-green-6)',
  blue: 'var(--mantine-color-blue-6)',
  violet: 'var(--mantine-color-violet-6)',
  grey: 'var(--mantine-color-gray-6)',
};

function ClipTile({ item, viewMode, selected, subject, onSelect, onHover, onFocusTile, addToBin, onTouchScrub, onTouchScrubEnd, touchScrubLabel, annotations, onToggleStar, onToggleFlag, onPlace, onEnhance, placeDisabled, projectId, binNames }: ClipTileProps) {
  const t = useT('editor-media.tile');
  const tp = useT('editor-misc.place');
  const tEn = useT('editor-enhance');
  const tcl = useT('editor-media.colorLower');
  const Icon = KIND_ICON[item.kind];
  const isStrip = viewMode === 'filmstrip';
  const isList = viewMode === 'list';
  const annotation = annotations?.get(item.mediaRef) ?? null;

  // Only what is known is shown; a source never analysed gets ONE muted line.
  const probeSummary = item.probe ? formatProbeSummary(item.probe) : null;
  const duration = item.probe ? formatDuration(item.probe.durationSec) : null;

  // R04.6: press-and-drag over the tile scrubs the strip once the finger has clearly gone sideways.
  const touchRef = useRef<{ id: number; x: number; y: number; state: 'pending' | 'scrub' | 'scroll' } | null>(null);
  const swallowClickRef = useRef(false);
  const scrubLabel = touchScrubLabel && touchScrubLabel.mediaRef === item.mediaRef ? touchScrubLabel.text : null;
  const endTouch = () => {
    if (touchRef.current?.state === 'scrub') onTouchScrubEnd?.();
    touchRef.current = null;
  };
  const scrubTo = (event: React.PointerEvent<HTMLElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    if (rect.width <= 0) return;
    onTouchScrub?.(item.mediaRef, Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)));
  };

  const width = isStrip ? 190 : viewMode === 'grid' ? 190 : '100%';
  const chips = item.bins.map((pin) => binNames?.get(pin.binId)).filter((n): n is string => Boolean(n));

  const tileRef = React.useRef<HTMLButtonElement | null>(null);
  const lift = useTouchLiftDrag(tileRef, { mediaRef: item.mediaRef, kind: item.kind });

  const tile = (
    <UnstyledButton
      ref={tileRef}
      data-testid="media-clip-tile"
      data-media-ref={item.mediaRef}
      data-subject={subject ? 'true' : 'false'}
      aria-pressed={selected}
      onClick={() => {
        if (swallowClickRef.current) {
          swallowClickRef.current = false;
          return;
        }
        onSelect(item);
      }}
      onPointerDown={(event: React.PointerEvent<HTMLButtonElement>) => {
        swallowClickRef.current = false;
        if (event.pointerType !== 'touch' || !onTouchScrub) return;
        touchRef.current = { id: event.pointerId, x: event.clientX, y: event.clientY, state: 'pending' };
      }}
      onPointerMove={(event: React.PointerEvent<HTMLButtonElement>) => {
        const t = touchRef.current;
        if (!t || t.id !== event.pointerId || t.state === 'scroll') return;
        // A long press has lifted the tile: the finger now carries it, so it must not also scrub.
        if (lift.isLifted()) {
          if (t.state === 'scrub') onTouchScrubEnd?.();
          touchRef.current = null;
          return;
        }
        if (t.state === 'pending') {
          const kind = classifyTileScrub(event.clientX - t.x, event.clientY - t.y);
          if (kind === 'pending') return;
          if (kind === 'scroll') {
            t.state = 'scroll';
            return;
          }
          t.state = 'scrub';
          swallowClickRef.current = true;
        }
        scrubTo(event);
      }}
      onPointerUp={endTouch}
      onPointerCancel={endTouch}
      // Enter/focus only. Clearing on `mouseleave`/`blur` is what made the strip unreachable: the
      // pointer leaves the tile on its way to the strip, so the cell unmounted mid-journey. The
      // release decision belongs to the hover region in `MediaPool`, which contains both.
      onMouseEnter={() => onHover(item.mediaRef)}
      onFocus={() => onFocusTile(item.mediaRef)}
      draggable
      onDragStart={(event: React.DragEvent<HTMLButtonElement>) => {
        if (!event.dataTransfer) return;
        writeDragPayload(event.dataTransfer, { kind: 'clip', mediaRef: item.mediaRef });
        // The kind rides in the type list (readable while dragging, unlike the data) so the timeline can
        // show "no" over a track the source cannot go on.
        event.dataTransfer.setData(`${CLIP_KIND_MIME_PREFIX}${item.kind}`, '1');
        event.dataTransfer.effectAllowed = 'copy';
      }}
      style={{
        display: 'block',
        flex: 1,
        minWidth: 0,
        width: '100%',
        padding: 8,
        paddingBottom: 6,
        borderRadius: 8,
        textAlign: 'left',
        // Vertical drags stay with the page; sideways drags are the scrub.
        touchAction: 'pan-y',
        position: 'relative',
        WebkitTouchCallout: 'none',
        WebkitUserSelect: 'none',
        userSelect: 'none',
      }}
    >
      {scrubLabel ? (
        <Text
          data-testid="media-tile-scrub-label"
          role="status"
          size="xs"
          fw={600}
          style={{ position: 'absolute', top: 12, left: 12, zIndex: 1, padding: '2px 8px', borderRadius: 4, background: 'var(--mantine-color-dark-8)', color: 'var(--mantine-color-gray-0)', pointerEvents: 'none' }}
        >
          {scrubLabel}
        </Text>
      ) : null}
      <Box style={isList ? { display: 'flex', gap: 10, alignItems: 'center' } : undefined}>
        <Box style={isList ? { width: 72, flex: '0 0 72px' } : undefined}>
          {projectId ? <TilePoster projectId={projectId} item={item} height={isList ? 42 : 100} /> : null}
        </Box>
        <Box style={isList ? { flex: 1, minWidth: 0 } : undefined}>
          <Group gap={6} wrap="nowrap" mt={isList ? 0 : 6}>
            <Icon size={14} />
            <Text size="sm" fw={600} lineClamp={1} title={item.fileName} style={{ flex: 1, minWidth: 0 }}>
              {item.fileName}
            </Text>
            {annotation?.colorLabel ? (
              <span
                data-testid="media-tile-color"
                data-color={annotation.colorLabel}
                role="img"
                aria-label={t('colourLabel', { color: tcl(annotation.colorLabel) })}
                style={{ width: 10, height: 10, borderRadius: 5, background: LABEL_COLOR[annotation.colorLabel], flex: '0 0 10px' }}
              />
            ) : null}
          </Group>
          <Text size="xs" c="dimmed" mt={2} data-testid="media-tile-facts">
            {[kindLabels(t)[item.kind], duration, probeSummary !== 'Not analysed' ? probeSummary : null, formatBytes(item.sizeBytes)].filter(Boolean).join(' · ')}
          </Text>
          {item.probe === null ? (
            <Text size="xs" c="dimmed" data-testid="media-tile-unanalysed">
              {t('notAnalysed')}
            </Text>
          ) : null}
          {chips.length > 0 ? (
            <Group gap={4} mt={4} wrap="wrap" data-testid="media-tile-bins">
              {chips.slice(0, 3).map((name) => (
                <Badge key={name} size="xs" fz="xs" variant="outline" color="gray" style={{ textTransform: 'none' }}>
                  {name}
                </Badge>
              ))}
              {chips.length > 3 ? <Text size="xs" c="dimmed">+{chips.length - 3}</Text> : null}
            </Group>
          ) : null}
        </Box>
      </Box>
    </UnstyledButton>
  );

  const showStar = Boolean(onToggleStar);
  const showFlag = Boolean(onToggleFlag);
  const canEnhance = Boolean(onEnhance) && item.kind === 'video';
  const hasMenu = Boolean(addToBin) || Boolean(onPlace) || canEnhance;

  return (
    <Box
      style={{
        width,
        flex: isStrip ? '0 0 auto' : undefined,
        display: 'flex',
        flexDirection: isList ? 'row' : 'column',
        alignItems: isList ? 'center' : 'stretch',
        borderRadius: 8,
        border: `1px solid ${selected ? 'var(--mantine-color-blue-6)' : subject ? 'var(--mantine-color-gray-5)' : 'var(--mantine-color-gray-3)'}`,
        background: selected ? 'var(--mantine-color-blue-0)' : 'transparent',
      }}
      data-testid="media-tile-wrap"
    >
      {tile}
      <TouchDragChip mediaRef={item.mediaRef} label={item.fileName} />
      {/* The tile's own controls sit BESIDE the picture button (a row under it, or a column at the end of a list row), never on top of it. */}
      <Group gap={4} wrap="nowrap" justify={isList ? 'flex-end' : 'space-between'} px={8} pb={isList ? 0 : 6} pr={isList ? 8 : undefined} style={{ flex: '0 0 auto' }}>
      <Group gap={2} wrap="nowrap">
        {showStar ? (
          <EditorIcon icon={annotation?.favourite ? IconStarFilled : IconStar}
            variant={annotation?.favourite ? 'filled' : 'default'}
            color={annotation?.favourite ? 'yellow' : 'gray'}
            label={annotation?.favourite ? t('unstar', { name: item.fileName }) : t('star', { name: item.fileName })}
            aria-pressed={annotation?.favourite ?? false}
            data-testid="media-tile-star"
            data-for-media-ref={item.mediaRef}
            onClick={() => onToggleStar?.(item)} />
        ) : null}
        {showFlag ? (
          <EditorIcon icon={annotation?.flag ? IconFlagFilled : IconFlag}
            variant={annotation?.flag ? 'filled' : 'default'}
            color={annotation?.flag ? 'red' : 'gray'}
            label={annotation?.flag ? t('unflag', { name: item.fileName }) : t('flag', { name: item.fileName })}
            aria-pressed={annotation?.flag ?? false}
            data-testid="media-tile-flag"
            data-for-media-ref={item.mediaRef}
            onClick={() => onToggleFlag?.(item)} />
        ) : null}
      </Group>
      {hasMenu ? (
        <Menu position="bottom-end" withinPortal middlewares={TILE_MENU_FIT}>
          <Menu.Target>
            <EditorIcon icon={IconDots} size="lg"
              variant="default"
              label={t('more', { name: item.fileName })}
              data-testid="media-tile-menu"
              data-for-media-ref={item.mediaRef} />
          </Menu.Target>
          <Menu.Dropdown>
            {onPlace ? (
              <>
                <Menu.Label>{t('putOnTimeline')}</Menu.Label>
                {[...PLACE_MODES, PLACE_OVER].map((choice) => (
                  <Menu.Item key={choice.mode} mih={44} disabled={placeDisabled} onClick={() => onPlace(item, choice.mode)} data-testid={`tile-place-${choice.mode}`}>
                    <div>{tp.msg(placeChoiceLabelMsg(choice.mode))}</div>
                    <Text size="xs" c="dimmed" maw={240}>
                      {tp.msg(placeChoiceHintMsg(choice.mode))}
                    </Text>
                  </Menu.Item>
                ))}
              </>
            ) : null}
            {canEnhance ? (
              <Menu.Item mih={44} onClick={() => onEnhance?.(item)} data-testid="tile-enhance">
                {tEn('pool.action')}
              </Menu.Item>
            ) : null}
            {addToBin ? (
              <>
                <Menu.Label>{t('addToBin')}</Menu.Label>
                {moveTargets(addToBin.bins).length === 0 ? (
                  <Menu.Item mih={44} disabled>
                    {t('noBins')}
                  </Menu.Item>
                ) : (
                  moveTargets(addToBin.bins).map((bin) => (
                    <Menu.Item
                      key={bin.id}
                      mih={44}
                      leftSection={<IconFolderPlus size={16} />}
                      onClick={() => addToBin.onAdd(item, bin.id)}
                      data-testid={`add-to-bin-${bin.id}`}
                    >
                      {bin.name}
                    </Menu.Item>
                  ))
                )}
              </>
            ) : null}
          </Menu.Dropdown>
        </Menu>
      ) : null}
      </Group>
    </Box>
  );
}
