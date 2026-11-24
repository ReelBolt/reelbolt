'use client';

import React, { useRef, useState } from 'react';
import { Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { useT } from '@/i18n';
import { clampCrop, dragCrop, type CropHandle, type CropRect } from '@/lib/editor/crop';

export interface CropDialogProps {
  opened: boolean;
  onClose(): void;
  /** The picture being cropped: its address and its own pixel size. */
  imageUrl: string;
  width: number;
  height: number;
  /** The box the layer is cropped to now, in the whole picture's pixels; the whole picture when uncropped. */
  initial?: CropRect;
  label: string;
  busy: boolean;
  coarse?: boolean;
  onApply(rect: CropRect): void;
}

const HANDLES: { id: CropHandle; style: React.CSSProperties; cursor: string }[] = [
  { id: 'nw', style: { left: 0, top: 0 }, cursor: 'nwse-resize' },
  { id: 'n', style: { left: '50%', top: 0 }, cursor: 'ns-resize' },
  { id: 'ne', style: { left: '100%', top: 0 }, cursor: 'nesw-resize' },
  { id: 'e', style: { left: '100%', top: '50%' }, cursor: 'ew-resize' },
  { id: 'se', style: { left: '100%', top: '100%' }, cursor: 'nwse-resize' },
  { id: 's', style: { left: '50%', top: '100%' }, cursor: 'ns-resize' },
  { id: 'sw', style: { left: 0, top: '100%' }, cursor: 'nesw-resize' },
  { id: 'w', style: { left: 0, top: '50%' }, cursor: 'ew-resize' },
];

/**
 * "Crop": a window with the picture, a box to drag by its corners and edges (or move as a whole),
 * and the same box as numbers in the picture's own pixels. Nothing changes until "Crop".
 */
export function CropDialog({ opened, onClose, imageUrl, width, height, initial, label, busy, coarse = false, onApply }: CropDialogProps) {
  const t = useT('editor-thumbnail-a.crop');
  const [rect, setRect] = useState<CropRect>(initial ?? { x: 0, y: 0, w: width, h: height });
  const drag = useRef<{ handle: CropHandle; startX: number; startY: number; start: CropRect } | null>(null);
  const stage = useRef<HTMLDivElement | null>(null);
  const [drafts, setDrafts] = useState<Partial<Record<keyof CropRect, string>>>({});

  const shownWidth = Math.min(560, width);
  const k = shownWidth / width;
  const grab = coarse ? 44 : 20;

  const begin = (handle: CropHandle, e: React.PointerEvent) => {
    e.preventDefault();
    e.stopPropagation();
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    drag.current = { handle, startX: e.clientX, startY: e.clientY, start: rect };
  };
  const move = (e: React.PointerEvent) => {
    const d = drag.current;
    if (!d) return;
    setRect(dragCrop(d.start, d.handle, (e.clientX - d.startX) / k, (e.clientY - d.startY) / k, width, height));
  };
  const end = () => {
    drag.current = null;
  };

  const setField = (key: keyof CropRect) => {
    const text = drafts[key];
    setDrafts((p) => ({ ...p, [key]: undefined }));
    const n = Number((text ?? '').trim());
    if (text === undefined || text.trim() === '' || !Number.isFinite(n)) return;
    setRect((r) => clampCrop({ ...r, [key]: n }, width, height));
  };

  const whole = rect.x === 0 && rect.y === 0 && rect.w === width && rect.h === height;
  const start = initial ?? { x: 0, y: 0, w: width, h: height };
  const unchanged = rect.x === start.x && rect.y === start.y && rect.w === start.w && rect.h === start.h;

  return (
    <Modal opened={opened} onClose={onClose} title={t('title', { label })} size="lg" centered fullScreen={coarse && typeof window !== 'undefined' && window.innerWidth < 768}>
      <Stack gap="sm" data-testid="crop-dialog">
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
        <div
          ref={stage}
          style={{ position: 'relative', width: shownWidth, height: height * k, maxWidth: '100%', touchAction: 'none', userSelect: 'none', margin: '0 auto', background: '#111' }}
          onPointerMove={move}
          onPointerUp={end}
          onPointerCancel={end}
        >
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img src={imageUrl} alt="" draggable={false} style={{ width: '100%', height: '100%', display: 'block', opacity: 0.45 }} />
          <div
            data-testid="crop-box"
            onPointerDown={(e) => begin('move', e)}
            style={{
              position: 'absolute',
              left: rect.x * k,
              top: rect.y * k,
              width: rect.w * k,
              height: rect.h * k,
              outline: '2px solid var(--mantine-color-blue-5)',
              backgroundImage: `url(${imageUrl})`,
              backgroundSize: `${width * k}px ${height * k}px`,
              backgroundPosition: `${-rect.x * k}px ${-rect.y * k}px`,
              cursor: 'var(--rb-cursor-move)',
            }}
          >
            {HANDLES.map((h) => (
              <div
                key={h.id}
                role="button"
                tabIndex={0}
                aria-label={t('handle', { side: t(`handles.${h.id}`) })}
                data-testid={`crop-handle-${h.id}`}
                onPointerDown={(e) => begin(h.id, e)}
                onKeyDown={(e) => {
                  const step = e.shiftKey ? 10 : 1;
                  const dx = e.key === 'ArrowLeft' ? -step : e.key === 'ArrowRight' ? step : 0;
                  const dy = e.key === 'ArrowUp' ? -step : e.key === 'ArrowDown' ? step : 0;
                  if (dx || dy) {
                    e.preventDefault();
                    setRect((r) => dragCrop(r, h.id, dx, dy, width, height));
                  }
                }}
                style={{
                  position: 'absolute',
                  ...h.style,
                  width: grab,
                  height: grab,
                  transform: 'translate(-50%, -50%)',
                  cursor: h.cursor,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}
              >
                <span style={{ width: 10, height: 10, background: '#fff', border: '2px solid var(--mantine-color-blue-6)', borderRadius: 2 }} />
              </div>
            ))}
          </div>
        </div>
        <Group grow gap={6} wrap="wrap">
          {/* eslint-disable-next-line i18next/no-literal-string -- ids, not text */}
          {(['x', 'y', 'w', 'h'] as const).map((key) => (
            <TextInput
              key={key}
              size="xs"
              label={t(`fields.${key}`)}
              inputMode="numeric"
              value={drafts[key] ?? String(rect[key])}
              onChange={(e) => {
                const text = e.currentTarget.value;
                setDrafts((p) => ({ ...p, [key]: text }));
              }}
              onBlur={() => setField(key)}
              onKeyDown={(e) => e.key === 'Enter' && setField(key)}
              styles={{ input: { minHeight: coarse ? 44 : undefined, fontSize: 12 }, label: { fontSize: 12 } }}
              aria-label={t(`fieldAria.${key}`)}
              data-testid={`crop-${key}`}
            />
          ))}
        </Group>
        <Group justify="space-between">
          <Button size="xs" fz="xs" variant="subtle" h={coarse ? 44 : undefined} onClick={() => setRect({ x: 0, y: 0, w: width, h: height })} disabled={whole}>
            {t('reset')}
          </Button>
          <Group gap={6}>
            <Button size="xs" fz="xs" variant="default" h={coarse ? 44 : undefined} onClick={onClose}>
              {t('cancel')}
            </Button>
            <Button size="xs" fz="xs" h={coarse ? 44 : undefined} loading={busy} disabled={unchanged} onClick={() => onApply(rect)} data-testid="crop-apply">
              {whole ? t('removeCrop') : t('apply')}
            </Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
