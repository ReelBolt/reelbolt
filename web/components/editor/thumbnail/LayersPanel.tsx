'use client';

import React, { useState } from 'react';
import { Badge, Box, Button, Checkbox, FileButton, Group, Loader, Menu, Progress, Select, Slider, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import {
  IconArrowDown,
  IconArrowUp,
  IconArrowBarToDown,
  IconArrowBarToUp,
  IconDots,
  IconEye,
  IconEyeOff,
  IconFlipHorizontal,
  IconFlipVertical,
  IconGripVertical,
  IconLock,
  IconLockOpen,
  IconMask,
  IconPencil,
  IconTrash,
} from '@tabler/icons-react';
import { useFormat, useT } from '@/i18n';
import { aiMarkNames } from '@/lib/editor/ai-label';
import type { LayerPatch } from '@/lib/editor/history';
import type { EditorLayer } from '@/lib/editor/model';
import {
  BLEND_MODES,
  type BlendMode,
  type EditImageLayerRole,
} from '@/lib/types/image-document';
import { EditorIcon } from '../ui';
import { maskMenuItems, reorderTarget, type LayerMenuAction, type MaskMenuAction } from './layer-actions';

export interface LayersPanelProps {
  /** Ascending z-order: index 0 is the back. The panel lists them front first. */
  layers: readonly EditorLayer[];
  selectedId: string | null;
  onSelect(id: string | null): void;
  onPatch(layerId: string, patch: LayerPatch, label: string, mergeKey?: string): void;
  /** `position` is 1-based from the back, which is what the server's `zOrder` patch takes. */
  onReorder(layerId: string, position: number): void;
  onDelete(layerId: string): void;
  /** True for a layer an AI designer made that can be changed ("Change this graphic"). */
  canRevise?(layer: EditorLayer): boolean;
  /** Opens "Change this graphic" for that layer. */
  onRevise?(layerId: string): void;
  /** Layers whose image could not be loaded: their row says so, and they are left out of an export. */
  missing?: ReadonlySet<string>;
  /** Layers a background job (an AI operation, a designed graphic) is working on: their row shows a working mark. */
  busy?: ReadonlySet<string>;
  /** A touch screen: every button in a row is a 44 px target. */
  coarse?: boolean;
  /** Uploads a picture from this device into the project's files and adds it as a layer. */
  onUploadImage?(file: File): void;
  /** Upload progress 0..100 while one is going, else null. */
  uploadPercent?: number | null;
  /** A plain sentence about a failed upload, or null. */
  uploadError?: string | null;
  /** "Choose several layers" is on: every row shows a tick box (align, spread out, merge work on the ticked ones). */
  picking?: boolean;
  checked?: ReadonlySet<string>;
  onToggleChecked?(id: string): void;
  /**
   * Phone rows (R13.7): 44 px rows with the eye, the lock, the name and one overflow menu (Bring forward, Send back,
   * Bring to front, Send to back, Delete), so Delete is never beside an arrow.
   */
  menuRows?: boolean;
  /**
   * Layer masks: the menu entries and the chip on a row that has a mask. Leave out where masks cannot be
   * edited. `paintingId` is the layer whose mask is open for painting right now.
   */
  masks?: {
    busyId: string | null;
    paintingId: string | null;
    maskOnlyId: string | null;
    onAction(action: MaskMenuAction, layerId: string): void;
  };
}

/** "Upload an image": a button that opens the device's file picker, with progress and a plain error. */
export function UploadImageButton({
  onUpload,
  percent = null,
  error = null,
  coarse = false,
}: {
  onUpload(file: File): void;
  percent?: number | null;
  error?: string | null;
  coarse?: boolean;
}) {
  const t = useT('editor-thumbnail-a.layers');
  const shownLabel = (label: string) => (label === 'Text' ? t('defaultText') : label === 'Merged layer' ? t('defaultMerged') : label);
  return (
    <Stack gap={6}>
      <FileButton onChange={(f) => f && onUpload(f)} accept="image/png,image/jpeg,image/webp,image/avif,image/gif">
        {(props) => (
          <Button
            {...props}
            size="xs" fz="xs"
            variant="light"
            fullWidth
            h={coarse ? 44 : undefined}
            data-testid="layer-upload-image"
            loading={percent !== null}
          >
            {t('upload')}
          </Button>
        )}
      </FileButton>
      {percent !== null && <Progress value={percent} size="sm" aria-label={t('uploadProgress')} />}
      {error && (
        <Text size="xs" c="red" role="alert">
          {error}
        </Text>
      )}
    </Stack>
  );
}

const NO_MISSING: ReadonlySet<string> = new Set();

/**
 * The layer stack: reorder by dragging (or the arrow buttons, which are the keyboard route),
 * show/hide, lock, and — for the selected layer — name, opacity, blend mode, role and flips.
 *
 * Reordering uses the browser's own drag and drop rather than a library: the editor adds no
 * dependency for the canvas or its panels, and the arrow buttons already give a non-pointer way to
 * do exactly the same move.
 */
export function LayersPanel({
  layers,
  selectedId,
  onSelect,
  onPatch,
  onReorder,
  onDelete,
  canRevise,
  onRevise,
  missing = NO_MISSING,
  busy = NO_MISSING,
  coarse = false,
  onUploadImage,
  uploadPercent = null,
  uploadError = null,
  picking = false,
  checked = NO_MISSING,
  onToggleChecked,
  menuRows = false,
  masks,
}: LayersPanelProps) {
  const t = useT('editor-thumbnail-a.layers');
  const shownLabel = (label: string) => (label === 'Text' ? t('defaultText') : label === 'Merged layer' ? t('defaultMerged') : label);
  const tc = useT('editor-thumbnail-a.common');
  const f = useFormat();
  const button = coarse ? 44 : 'sm';
  const [dragId, setDragId] = useState<string | null>(null);
  const [overId, setOverId] = useState<string | null>(null);
  const selected = layers.find((l) => l.id === selectedId) ?? null;
  const frontFirst = [...layers].reverse();

  const positionOf = (id: string) => layers.findIndex((l) => l.id === id) + 1;

  const maskItems = (layer: EditorLayer) =>
    masks
      ? maskMenuItems(layer, { busy: masks.busyId !== null, painting: masks.paintingId !== null }).map((item) => (
          <Menu.Item
            key={item.action}
            mih={coarse ? 44 : undefined}
            disabled={item.disabled}
            data-testid={`layer-mask-${item.action}-${layer.id}`}
            rightSection={item.action === 'only' && masks.maskOnlyId === layer.id ? <Text size="xs" c="dimmed">{t('on')}</Text> : undefined}
            onClick={() => masks.onAction(item.action, layer.id)}
          >
            {t(`maskMenu.${item.action}`)}
          </Menu.Item>
        ))
      : [];

  return (
    <Stack gap="sm">
      {layers.length === 0 ? (
        <Stack gap="xs">
          <Text size="xs" c="dimmed">
            {t('empty')}
          </Text>
          {onUploadImage && (
            <UploadImageButton onUpload={onUploadImage} percent={uploadPercent} error={uploadError} coarse={coarse} />
          )}
        </Stack>
      ) : (
        <Stack gap={4} role="list" aria-label={t('listLabel')}>
          {frontFirst.map((layer) => {
            const index = layers.findIndex((l) => l.id === layer.id);
            const isSelected = layer.id === selectedId;
            return (
              <Group
                key={layer.id}
                role="listitem"
                gap={4}
                // On a touch screen five 44 px buttons leave a 390 px row no room for the layer's name, so the row wraps:
                // eye, lock, name and kind first, then the move and delete buttons beneath.
                wrap={coarse && !menuRows ? 'wrap' : 'nowrap'}
                draggable={!menuRows}
                data-testid={`layer-row-${layer.id}`}
                onDragStart={(e) => {
                  setDragId(layer.id);
                  e.dataTransfer?.setData('text/plain', layer.id);
                  if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
                }}
                onDragOver={(e) => {
                  if (!dragId || dragId === layer.id) return;
                  e.preventDefault();
                  setOverId(layer.id);
                }}
                onDragLeave={() => setOverId((cur) => (cur === layer.id ? null : cur))}
                onDrop={(e) => {
                  e.preventDefault();
                  if (dragId && dragId !== layer.id) onReorder(dragId, positionOf(layer.id));
                  setDragId(null);
                  setOverId(null);
                }}
                onDragEnd={() => {
                  setDragId(null);
                  setOverId(null);
                }}
                style={{
                  padding: '2px 4px',
                  minHeight: menuRows ? 44 : undefined,
                  borderRadius: 6,
                  border: `1px solid ${isSelected ? 'var(--mantine-color-blue-5)' : 'transparent'}`,
                  background: overId === layer.id ? 'rgba(77,171,247,0.18)' : isSelected ? 'rgba(77,171,247,0.08)' : undefined,
                  opacity: dragId === layer.id ? 0.5 : layer.visible ? 1 : 0.55,
                }}
              >
                {picking && (
                  <Checkbox
                    size={coarse ? 'md' : 'xs'}
                    checked={checked.has(layer.id)}
                    onChange={() => onToggleChecked?.(layer.id)}
                    aria-label={t('pick', { name: shownLabel(layer.label) })}
                    data-testid={`layer-pick-${layer.id}`}
                    styles={coarse ? { root: { minWidth: 44, minHeight: 44, display: 'flex', alignItems: 'center', justifyContent: 'center' } } : undefined}
                  />
                )}
                <IconGripVertical size={14} aria-hidden style={{ cursor: 'var(--rb-cursor-grab)', flexShrink: 0 }} />
                <EditorIcon icon={layer.visible ? IconEye : IconEyeOff} size="sm"
                  label={layer.visible ? t('hide', { name: shownLabel(layer.label) }) : t('show', { name: shownLabel(layer.label) })}
                  aria-pressed={!layer.visible}
                  onClick={() => onPatch(layer.id, { visible: !layer.visible }, layer.visible ? t('hide', { name: shownLabel(layer.label) }) : t('show', { name: shownLabel(layer.label) }))} />
                <EditorIcon icon={layer.locked ? IconLock : IconLockOpen} size="sm"
                  color={layer.locked ? 'yellow' : 'gray'}
                  label={layer.locked ? t('unlock', { name: shownLabel(layer.label) }) : t('lock', { name: shownLabel(layer.label) })}
                  aria-pressed={layer.locked}
                  onClick={() => onPatch(layer.id, { locked: !layer.locked }, layer.locked ? t('unlock', { name: shownLabel(layer.label) }) : t('lock', { name: shownLabel(layer.label) }))} />
                <Box
                  component="button"
                  type="button"
                  onClick={() => onSelect(layer.id)}
                  aria-label={t('select', { name: shownLabel(layer.label) })}
                  aria-current={isSelected}
                  style={{
                    flex: 1,
                    minWidth: coarse ? 96 : 0,
                    minHeight: coarse ? 44 : undefined,
                    textAlign: 'left',
                    background: 'none',
                    border: 0,
                    padding: '4px 2px',
                    color: 'inherit',
                    cursor: 'var(--rb-cursor-pointer)',
                    font: 'inherit',
                  }}
                >
                  <Text size="xs" truncate fw={isSelected ? 600 : 400}>
                    {shownLabel(layer.label)}
                  </Text>
                  {layer.aiMark && (
                    <Text size="xs" c="dimmed" truncate data-testid={`layer-ai-${layer.id}`}>
                      {layer.aiMark.subjects.length > 0 ? t('aiMade', { names: f.list(aiMarkNames(layer.aiMark)) }) : t('aiMadePlain')}
                    </Text>
                  )}
                </Box>
                {masks && layer.matteUrl && (
                  <Box
                    component="button"
                    type="button"
                    disabled={masks.busyId !== null || masks.paintingId !== null || layer.locked}
                    onClick={() => masks.onAction('edit', layer.id)}
                    aria-label={t('editMask', { name: shownLabel(layer.label) })}
                    data-testid={`layer-mask-chip-${layer.id}`}
                    style={{
                      width: coarse ? 44 : 24,
                      height: coarse ? 44 : 24,
                      padding: coarse ? 10 : 2,
                      flexShrink: 0,
                      border: 0,
                      background: 'none',
                      cursor: 'var(--rb-cursor-pointer)',
                    }}
                  >
                    {/* eslint-disable-next-line @next/next/no-img-element */}
                    <img src={layer.matteUrl} alt="" width={20} height={20} style={{ width: '100%', height: '100%', objectFit: 'cover', borderRadius: 3, border: '1px solid var(--mantine-color-dark-3)' }} />
                  </Box>
                )}
                {masks && !menuRows && layer.kind !== 'text' && (
                  <Menu position="bottom-end" withinPortal shadow="md" width={200}>
                    <Menu.Target>
                      <EditorIcon icon={IconMask} size="sm" variant="default" tooltip={false} label={t('maskOptions', { name: shownLabel(layer.label) })} data-testid={`layer-mask-menu-${layer.id}`} />
                    </Menu.Target>
                    <Menu.Dropdown>{maskItems(layer)}</Menu.Dropdown>
                  </Menu>
                )}
                <Badge size="xs" fz="xs" variant="light" color={layer.kind === 'generated' ? 'violet' : layer.kind === 'cutout' ? 'yellow' : 'gray'}>
                  {t(`kind.${layer.kind}`)}
                </Badge>
                {busy.has(layer.id) && (
                  <Badge size="xs" fz="xs" variant="light" color="orange" leftSection={<Loader size="xs" color="orange" />} data-testid={`layer-busy-${layer.id}`}>
                    {t('working')}
                  </Badge>
                )}
                {missing.has(layer.id) && (
                  <Badge size="xs" fz="xs" variant="light" color="red">
                    {t('imageMissing')}
                  </Badge>
                )}
                {menuRows ? (
                  <Menu position="bottom-end" withinPortal shadow="md" width={220}>
                    <Menu.Target>
                      <EditorIcon icon={IconDots} size="md" variant="default" tooltip={false} label={t('moreActions', { name: shownLabel(layer.label) })} data-testid={`layer-menu-${layer.id}`} />
                    </Menu.Target>
                    <Menu.Dropdown>
                      {/* eslint-disable-next-line i18next/no-literal-string -- ids, not text */}
                      {(['forward', 'back', 'front', 'backmost'] as const satisfies readonly LayerMenuAction[]).map((action) => {
                        const target = reorderTarget(action, index, layers.length);
                        return (
                          <Menu.Item
                            key={action}
                            mih={44}
                            disabled={target === null}
                            leftSection={action === 'forward' ? <IconArrowUp size={16} /> : action === 'back' ? <IconArrowDown size={16} /> : action === 'front' ? <IconArrowBarToUp size={16} /> : <IconArrowBarToDown size={16} />}
                            data-testid={`layer-menu-${action}-${layer.id}`}
                            onClick={() => target !== null && onReorder(layer.id, target)}
                          >
                            {t(`menu.${action}`)}
                          </Menu.Item>
                        );
                      })}
                      {masks && layer.kind !== 'text' && <Menu.Divider my={8} />}
                      {maskItems(layer)}
                      <Menu.Divider my={8} />
                      <Menu.Item
                        mih={44}
                        color="red"
                        leftSection={<IconTrash size={16} />}
                        data-testid={`layer-menu-delete-${layer.id}`}
                        onClick={() => onDelete(layer.id)}
                      >
                        {t('deleteLayer')}
                      </Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                ) : (
                  <>
                  <Tooltip label={t('menu.forward')} openDelay={400}>
                    <EditorIcon tooltip={false} icon={IconArrowUp} size="sm"
                      label={t('moveForward', { name: shownLabel(layer.label) })}
                      disabled={index === layers.length - 1}
                      onClick={() => onReorder(layer.id, index + 2)} />
                  </Tooltip>
                  <Tooltip label={t('menu.back')} openDelay={400}>
                    <EditorIcon tooltip={false} icon={IconArrowDown} size="sm"
                      label={t('moveBack', { name: shownLabel(layer.label) })}
                      disabled={index === 0}
                      onClick={() => onReorder(layer.id, index)} />
                  </Tooltip>
                  <EditorIcon icon={IconTrash} size="sm"
                    color="red"
                    label={t('delete', { name: shownLabel(layer.label) })}
                    onClick={() => onDelete(layer.id)} />
                  </>
                )}
              </Group>
            );
          })}
        </Stack>
      )}

      {selected && (
        <Stack gap="xs" pt="xs" style={{ borderTop: '1px solid var(--mantine-color-dark-4)' }}>
          {onRevise && canRevise?.(selected) && (
            <Button
              size="xs" fz="xs"
              variant="light"
              leftSection={<IconPencil size={14} />}
              onClick={() => onRevise(selected.id)}
            >
              {t('changeGraphic')}
            </Button>
          )}
          <TextInput
            // Remounts when the layer (or its saved name) changes, so the field never shows a stale name.
            key={`${selected.id}:${selected.label}`}
            size="xs"
            label={t('name')}
            defaultValue={selected.label}
            onBlur={(e) => {
              const next = e.currentTarget.value.trim();
              if (next && next !== selected.label) onPatch(selected.id, { label: next }, t('history.rename'));
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') e.currentTarget.blur();
            }}
          />
          <div>
            <Group justify="space-between">
              <Text size="xs" component="label" id={`opacity-${selected.id}`}>
                {t('opacity')}
              </Text>
              <Text size="xs" c="dimmed">
                {tc('percent', { n: Math.round(selected.opacity * 100) }) /* eslint-disable-line i18next/no-literal-string -- tc is a useT() translator, not copy */}
              </Text>
            </Group>
            <Slider
              size="sm"
              min={0}
              max={100}
              value={Math.round(selected.opacity * 100)}
              onChange={(v) => onPatch(selected.id, { opacity: v / 100 }, t('history.opacity'), `opacity:${selected.id}`)}
              label={null}
              aria-labelledby={`opacity-${selected.id}`}
              thumbLabel={t('opacity')}
              disabled={selected.locked}
            />
          </div>
          <Group grow gap="xs">
            <Select
              size="xs"
              label={t('blend')}
              allowDeselect={false}
              data={BLEND_MODES.map((m) => ({ value: m, label: t(`blendMode.${m}`) }))}
              value={selected.blendMode}
              onChange={(v) => v && onPatch(selected.id, { blendMode: v as BlendMode }, t('history.blend'))}
            />
            <Select
              size="xs"
              label={t('role')}
              allowDeselect={false}
              data={(['background', 'subject', 'overlay'] as const).map((r) => ({ value: r, label: t(`roleName.${r}`) }))}
              value={selected.role}
              onChange={(v) => v && onPatch(selected.id, { role: v as EditImageLayerRole }, t('history.role'))}
            />
          </Group>
          <Group gap="xs">
            <EditorIcon icon={IconFlipHorizontal}
              variant="default"
              label={t('flipH')}
              aria-pressed={selected.transform.flipX}
              disabled={selected.locked}
              onClick={() =>
                onPatch(selected.id, { transform: { ...selected.transform, flipX: !selected.transform.flipX } }, t('flipH'))
              } />
            <EditorIcon icon={IconFlipVertical}
              variant="default"
              label={t('flipV')}
              aria-pressed={selected.transform.flipY}
              disabled={selected.locked}
              onClick={() =>
                onPatch(selected.id, { transform: { ...selected.transform, flipY: !selected.transform.flipY } }, t('flipV'))
              } />
            {selected.locked && (
              <Text size="xs" c="dimmed">
                {t('lockedHint')}
              </Text>
            )}
          </Group>
        </Stack>
      )}
    </Stack>
  );
}
