'use client';

import React, { useState } from 'react';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import {
  Button,
  ColorInput,
  Group,
  Modal,
  NumberInput,
  SegmentedControl,
  SimpleGrid,
  Stack,
  Switch,
  Text,
  Tooltip,
  UnstyledButton,
} from '@mantine/core';
import { IconArrowsExchange } from '@tabler/icons-react';
import { sameShape } from '@/lib/editor/refit';
import {
  ASPECT_PRESETS,
  DEFAULT_TIER,
  RESOLUTION_TIERS,
  exceedsPhoneLimit,
  formatSize,
  matchAspect,
  matchTier,
  sizeFor,
  type AspectPreset,
  type ResolutionTier,
} from '@/lib/editor/canvas-size';
import { CANVAS_MAX, CANVAS_MIN, clampCanvasEdge } from '@/lib/editor/model';
import type { CanvasPatch } from '@/lib/editor/history';
import { EditorIcon } from '../ui';
import { useFormat, useT } from '@/i18n';
import { EDITOR_MODAL_Z } from '@/components/editor/ui/z-index';

export interface CanvasPanelProps {
  canvasWidth: number;
  canvasHeight: number;
  backgroundColor: string | null;
  onCanvas(patch: CanvasPatch, label: string, mergeKey?: string): void;
  /**
   * The photo the picture was opened on, at its own size (long side capped), or null when it is not
   * known yet. "Original size" sets the canvas to it and shows the whole photo again.
   */
  originalSize?: { width: number; height: number } | null;
  onOriginalSize?(): void;
  /**
   * Changes the size AND carries every layer along (scaled to fit and centred), as one undo step.
   * When given, and the picture has something on it, a new shape asks "Fit what's there" or "Keep positions".
   */
  onRefit?(size: { width: number; height: number }, label: string): void;
}

/** The tier closest to a size's short side, for a new shape picked from a size that matches none. */
function nearestTier(width: number, height: number): ResolutionTier {
  const short = Math.min(width, height);
  return RESOLUTION_TIERS.reduce(
    (best, t) => (Math.abs(t.shortSide - short) < Math.abs(best.shortSide - short) ? t : best),
    DEFAULT_TIER,
  );
}

/** A small rectangle in the shape's proportions, so the buttons read at a glance. */
function ShapeGlyph({ aspect, active }: { aspect: AspectPreset; active: boolean }) {
  const max = 22;
  const w = aspect.w >= aspect.h ? max : Math.round((max * aspect.w) / aspect.h);
  const h = aspect.h >= aspect.w ? max : Math.round((max * aspect.h) / aspect.w);
  return (
    <div style={{ height: max, display: 'flex', alignItems: 'center', justifyContent: 'center' }} aria-hidden>
      <div
        style={{
          width: w,
          height: h,
          borderRadius: 3,
          border: `1.5px solid ${active ? 'var(--mantine-color-blue-4)' : 'var(--mantine-color-dimmed)'}`,
          background: active ? 'rgba(77,171,247,0.18)' : 'transparent',
        }}
      />
    </div>
  );
}

/**
 * The picture's size: a shape (aspect ratio), a size (720p to 4K, by the short side), or exact
 * pixels; then its background. Changing the size never scales the layers — they keep their pixel
 * positions, and anything that ends up outside the frame stays visible beside it on the canvas.
 */
/** Backdrop used when the canvas goes from transparent to a solid colour. */
const DEFAULT_BACKGROUND = '#000000';

export function CanvasPanel({
  canvasWidth,
  canvasHeight,
  backgroundColor,
  onCanvas,
  originalSize = null,
  onOriginalSize,
  onRefit,
}: CanvasPanelProps) {
  const t = useT('editor-thumbnail-b.canvas');
  const f = useFormat();
  const phone = useDeviceClass() === 'phone';
  const [pending, setPending] = useState<{ size: { width: number; height: number }; label: string } | null>(null);
  const [width, setWidth] = useState<number>(canvasWidth);
  const [height, setHeight] = useState<number>(canvasHeight);
  const [seen, setSeen] = useState({ canvasWidth, canvasHeight });

  // An undo or a preset changed the canvas under the custom fields: follow it.
  if (seen.canvasWidth !== canvasWidth || seen.canvasHeight !== canvasHeight) {
    setSeen({ canvasWidth, canvasHeight });
    setWidth(canvasWidth);
    setHeight(canvasHeight);
  }

  const aspect = matchAspect(canvasWidth, canvasHeight);
  const tier = matchTier(canvasWidth, canvasHeight);
  const customW = clampCanvasEdge(width);
  const customH = clampCanvasEdge(height);
  const customChanged = customW !== canvasWidth || customH !== canvasHeight;

  const apply = (size: { width: number; height: number }, label: string) => {
    if (size.width === canvasWidth && size.height === canvasHeight) return;
    if (onRefit) {
      // The same shape at another size is a plain rescale; a different shape needs the user's choice.
      if (sameShape({ width: canvasWidth, height: canvasHeight }, size)) {
        onRefit(size, label);
        return;
      }
      setPending({ size, label });
      return;
    }
    onCanvas({ canvasWidth: size.width, canvasHeight: size.height }, label);
  };

  return (
    <Stack gap="lg">
      <Modal zIndex={EDITOR_MODAL_Z}
        opened={pending !== null}
        onClose={() => setPending(null)}
        title={pending ? t('changeTo', { size: formatSize(pending.size.width, pending.size.height) }) : ''}
        centered
        size="sm"
      >
        <Stack gap="sm">
          <Text size="sm">{t('refitQuestion')}</Text>
          <Button
            data-testid="canvas-refit-fit"
            fullWidth
            onClick={() => {
              if (pending && onRefit) onRefit(pending.size, pending.label);
              setPending(null);
            }}
          >
            {t('fit')}
          </Button>
          <Text size="xs" c="dimmed" mt={-6}>
            {t('fitHint')}
          </Text>
          <Button
            data-testid="canvas-refit-keep"
            fullWidth
            variant="default"
            onClick={() => {
              if (pending) onCanvas({ canvasWidth: pending.size.width, canvasHeight: pending.size.height }, pending.label);
              setPending(null);
            }}
          >
            {t('keep')}
          </Button>
          <Text size="xs" c="dimmed" mt={-6}>
            {t('keepHint')}
          </Text>
        </Stack>
      </Modal>
      <Stack gap={8}>
        <Text size="sm" fw={600} id="canvas-shape-label">
          {t('shape')}
        </Text>
        <SimpleGrid cols={4} spacing={6} role="group" aria-labelledby="canvas-shape-label">
          {ASPECT_PRESETS.map((preset) => {
            const active = aspect?.id === preset.id;
            return (
              <UnstyledButton
                key={preset.id}
                aria-pressed={active}
                aria-label={t('presetAria', { label: preset.label, hint: preset.hint })}
                onClick={() => apply(sizeFor(preset, tier ?? nearestTier(canvasWidth, canvasHeight)), t('historyShape', { label: preset.label }))}
                style={{
                  padding: '6px 2px',
                  borderRadius: 8,
                  minHeight: 44,
                  textAlign: 'center',
                  border: `1px solid ${active ? 'var(--mantine-color-blue-5)' : 'var(--mantine-color-default-border)'}`,
                  background: active ? 'rgba(77,171,247,0.10)' : 'transparent',
                }}
              >
                <ShapeGlyph aspect={preset} active={active} />
                <Text size="xs" fw={600} mt={2}>
                  {preset.label}
                </Text>
                <Text size="xs" c="dimmed" lh={1.2} truncate>
                  {preset.hint}
                </Text>
              </UnstyledButton>
            );
          })}
        </SimpleGrid>
      </Stack>

      <Stack gap={8}>
        <Text size="sm" fw={600} id="canvas-size-label">
          {t('size')}
        </Text>
        <SegmentedControl
          fullWidth
          size="xs"
          aria-labelledby="canvas-size-label"
          disabled={!aspect}
          data={RESOLUTION_TIERS.map((tier) => ({ value: tier.id, label: tier.label }))}
          value={tier?.id ?? ''}
          onChange={(id) => {
            const next = RESOLUTION_TIERS.find((tier) => tier.id === id);
            if (next && aspect) apply(sizeFor(aspect, next), t('historySize', { label: next.label }));
          }}
        />
        <Text size="xs" c="dimmed">
          {aspect
            ? t('sizeLine', { size: formatSize(canvasWidth, canvasHeight), label: aspect.label })
            : t('sizeLineCustom', { size: formatSize(canvasWidth, canvasHeight) })}
        </Text>
        {onOriginalSize && (
          <Button
            size="xs" fz="xs"
            variant="default"
            fullWidth
            disabled={!originalSize}
            onClick={onOriginalSize}
          >
            {originalSize ? t('originalSizeOf', { size: formatSize(originalSize.width, originalSize.height) }) : t('originalSize')}
          </Button>
        )}
        {exceedsPhoneLimit(canvasWidth, canvasHeight) && (
          <Text size="xs" c="yellow">
            {t('tooLarge')}
          </Text>
        )}
      </Stack>

      <Stack gap={8}>
        <Text size="sm" fw={600}>
          {t('exactSize')}
        </Text>
        <Group gap={6} align="flex-end" wrap="nowrap">
          <NumberInput
            size="xs"
            label={t('width')}
            min={CANVAS_MIN}
            max={CANVAS_MAX}
            value={width}
            onChange={(v) => setWidth(typeof v === 'number' ? v : canvasWidth)}
            style={{ flex: 1, minWidth: 0 }}
          />
          <Tooltip label={t('swap')} openDelay={400}>
            <EditorIcon tooltip={false} icon={IconArrowsExchange} size="sm"
              variant="default"
              label={t('swap')}
              onClick={() => {
                setWidth(height);
                setHeight(width);
              }} />
          </Tooltip>
          <NumberInput
            size="xs"
            label={t('height')}
            min={CANVAS_MIN}
            max={CANVAS_MAX}
            value={height}
            onChange={(v) => setHeight(typeof v === 'number' ? v : canvasHeight)}
            style={{ flex: 1, minWidth: 0 }}
          />
        </Group>
        <Button
          size="xs" fz="xs"
          variant="light"
          fullWidth
          disabled={!customChanged}
          onClick={() => apply({ width: customW, height: customH }, t('historyCustom'))}
        >
          {customChanged ? t('useSize', { size: formatSize(customW, customH) }) : t('useThisSize')}
        </Button>
        <Text size="xs" c="dimmed">
          {t('limits', { min: f.number(CANVAS_MIN, { useGrouping: false }), max: f.number(CANVAS_MAX, { useGrouping: false }) })}
        </Text>
      </Stack>

      <Stack gap="xs">
        <Text size="sm" fw={600}>
          {t('background')}
        </Text>
        <Switch
          size="xs"
          label={t('transparent')}
          checked={backgroundColor === null}
          onChange={(e) =>
            onCanvas({ backgroundColor: e.currentTarget.checked ? null : DEFAULT_BACKGROUND }, t('historyBackground'))
          }
        />
        {backgroundColor !== null && (
          <ColorInput
            size="xs"
            label={t('colourBehind')}
            value={backgroundColor}
            onChange={(color) => onCanvas({ backgroundColor: color }, t('historyBackground'), 'background')}
          />
        )}
      </Stack>
    </Stack>
  );
}
