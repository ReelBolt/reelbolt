'use client';

import React, { useEffect, useState } from 'react';
import { Alert, Badge, Button, Collapse, Group, Modal, Slider, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { useIsPhoneClass } from '@/lib/editing/use-device-class';
import { IconChevronDown, IconDeviceFloppy, IconDownload } from '@tabler/icons-react';
import { canEncode } from '@/lib/editor/canvas-dom';
import { DEFAULT_QUALITY, EXPORT_FORMATS, exportFormat, MORE_FORMAT_IDS, WEB_FORMAT_IDS, type ExportFormatId } from '@/lib/editor/export-formats';
import { estimateSizeMsg } from '@/lib/editor/labels-msg';
import { fitQuality, UNDER_2_MB } from '@/lib/editor/export-size';
import { DEFAULT_EXPORT_BASE, exportBaseName, exportFileName } from '@/lib/editor/export-name';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useFormat, useT } from '@/i18n';

export type ExportDestination = 'project' | 'download';

export interface ExportRequest {
  format: ExportFormatId;
  /** 0..1, for the lossy formats. */
  quality: number;
  /** Cleaned, with the format's extension. */
  fileName: string;
  destination: ExportDestination;
}

export interface ExportDialogProps {
  opened: boolean;
  onClose(): void;
  /** The picture's size, shown so a 4K export is not a surprise. */
  width: number;
  height: number;
  /** Why nothing can be exported right now (layers still loading, or not loading at all), or null. */
  blocker: string | null;
  /** Something the export will do that the user should know (a layer it leaves out), or null. */
  notice?: string | null;
  /** The exported picture gets the small "AI-generated" label (a visible layer shows an avatar that asks for it). */
  aiLabel?: boolean;
  /** Given when trying again could clear the blocker (a layer that could not be loaded). */
  onRetry?: () => void;
  /** The picture's name, the file name's starting point. */
  defaultName?: string | null;
  /** Makes the file and saves or downloads it; resolves with a sentence about what happened. */
  onExport(request: ExportRequest): Promise<string>;
  /** The size in bytes the file would have at this format and quality (encodes it, saves nothing). */
  onEstimate?(format: ExportFormatId, quality: number): Promise<number>;
}

/**
 * Export: pick a file type — the flat ones (PNG, JPEG, WebP, AVIF) or the layered ones that keep every
 * layer for Photoshop (PSD) or GIMP and Krita (ORA) — then save it to the project's files or download
 * it. AVIF is offered only when this browser can make it.
 */
export function ExportDialog({
  opened,
  onClose,
  width,
  height,
  blocker,
  notice = null,
  aiLabel = false,
  onRetry,
  defaultName,
  onExport,
  onEstimate,
}: ExportDialogProps) {
  const t = useT('editor-thumbnail-b.export');
  const f = useFormat();
  const isPhone = useIsPhoneClass();
  const [format, setFormat] = useState<ExportFormatId>('png');
  const [quality, setQuality] = useState(DEFAULT_QUALITY);
  const [name, setName] = useState(() => exportBaseName(defaultName ?? '') || DEFAULT_EXPORT_BASE);
  const [busy, setBusy] = useState<ExportDestination | null>(null);
  const [result, setResult] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  const [avif, setAvif] = useState<boolean | null>(null);
  const [moreOpen, setMoreOpen] = useState(false);
  const [measured, setMeasured] = useState<{ key: string; bytes: number | null } | null>(null);
  const [fitting, setFitting] = useState(false);
  const [fitNote, setFitNote] = useState<string | null>(null);

  useEffect(() => {
    if (!opened || avif !== null) return;
    let alive = true;
    void canEncode('image/avif').then((ok) => {
      if (alive) setAvif(ok);
    });
    return () => {
      alive = false;
    };
  }, [opened, avif]);

  const info = exportFormat(format);
  const blocked = blocker !== null;

  // The file size, worked out by really encoding the picture once the controls stop moving.
  useEffect(() => {
    if (!opened || !onEstimate || blocked) return;
    let alive = true;
    const key = `${format}:${info.lossy ? quality : 1}`;
    const timer = setTimeout(() => {
      onEstimate(format, quality).then(
        (bytes) => alive && setMeasured({ key, bytes }),
        () => alive && setMeasured({ key, bytes: null }),
      );
    }, 350);
    return () => {
      alive = false;
      clearTimeout(timer);
    };
  }, [opened, onEstimate, blocked, format, quality, info.lossy]);
  const estimate: { bytes: number } | 'working' | 'unknown' =
    measured && measured.key === `${format}:${info.lossy ? quality : 1}`
      ? measured.bytes === null
        ? 'unknown'
        : { bytes: measured.bytes }
      : 'working';

  const underTwoMb = async () => {
    if (!onEstimate) return;
    setFitting(true);
    setFitNote(null);
    try {
      const fit = await fitQuality((q) => onEstimate(format, q), UNDER_2_MB);
      setQuality(fit.quality);
      setFitNote(
        fit.fits
          ? t('qualitySet', { percent: f.percent(Math.round(fit.quality * 100) / 100), size: t.msg(estimateSizeMsg(fit.bytes)) })
          : t('overTwoMb'),
      );
    } catch {
      setFitNote(t('sizeFailed'));
    } finally {
      setFitting(false);
    }
  };
  const fileName = exportFileName(name, info.extension);
  const unavailable = (id: ExportFormatId) => id === 'avif' && avif === false;

  const run = async (destination: ExportDestination) => {
    setBusy(destination);
    setResult(null);
    try {
      const text = await onExport({ format, quality, fileName, destination });
      setResult({ tone: 'ok', text });
    } catch (cause: unknown) {
      setResult({
        tone: 'error',
        text: humanizeErrorT(t, cause, t('exportFailed')),
      });
    } finally {
      setBusy(null);
    }
  };

  const formatsOf = (ids: readonly ExportFormatId[]) => ids.map((id) => exportFormat(id));
  const renderFormat = (f: (typeof EXPORT_FORMATS)[number]) => {
            const active = f.id === format;
            const off = unavailable(f.id);
            return (
              <UnstyledButton
                key={f.id}
                role="radio"
                aria-checked={active}
                aria-disabled={off}
                disabled={off}
                onClick={() => {
                  if (off) return;
                  setFormat(f.id);
                  setResult(null);
                }}
                style={{
                  display: 'flex',
                  gap: 10,
                  alignItems: 'flex-start',
                  padding: '8px 10px',
                  minHeight: 44,
                  borderRadius: 8,
                  opacity: off ? 0.5 : 1,
                  cursor: off ? 'var(--rb-cursor-not-allowed)' : 'var(--rb-cursor-pointer)',
                  border: `1px solid ${active ? 'var(--mantine-color-blue-5)' : 'var(--mantine-color-default-border)'}`,
                  background: active ? 'rgba(77,171,247,0.10)' : 'transparent',
                }}
              >
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Group gap={6}>
                    <Text size="sm" fw={600}>
                      {f.label}
                    </Text>
                    {f.layered && (
                      <Badge size="xs" fz="xs" variant="light" color="violet">
                        {t('keepsLayers')}
                      </Badge>
                    )}
                  </Group>
                  <Text size="xs" c="dimmed">
                    {off ? t('avifUnsupported') : f.description}
                  </Text>
                </div>
              </UnstyledButton>
            );
          };

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t('title')}
      size="lg"
      radius="lg"
      centered={!isPhone}
      fullScreen={!!isPhone}
      closeButtonProps={{ size: isPhone ? 44 : undefined, 'aria-label': t('close') }}
    >
      <Stack gap="md">
        <Stack gap={6} role="radiogroup" aria-label={t('fileType')}>
          {formatsOf(WEB_FORMAT_IDS).map(renderFormat)}
          <UnstyledButton
            onClick={() => setMoreOpen((o) => !o)}
            aria-expanded={moreOpen}
            data-testid="export-more-formats"
            style={{ minHeight: 44, display: 'flex', alignItems: 'center', gap: 6 }}
          >
            <IconChevronDown size={16} style={{ transform: moreOpen ? 'rotate(180deg)' : undefined }} />
            <Text size="sm" fw={600}>
              {t('moreFormats')}
            </Text>
            <Text size="xs" c="dimmed">
              {t('moreFormatsHint')}
            </Text>
          </UnstyledButton>
          <Collapse expanded={moreOpen}>
            <Stack gap={6}>{formatsOf(MORE_FORMAT_IDS).map(renderFormat)}</Stack>
          </Collapse>
        </Stack>

        {info.lossy && (
          <div>
            <Group justify="space-between">
              <Text size="xs" id="export-quality-label">
                {t('quality')}
              </Text>
              <Text size="xs" c="dimmed">
                {f.percent(Math.round(quality * 100) / 100)}
              </Text>
            </Group>
            <Slider
              min={40}
              max={100}
              step={1}
              value={Math.round(quality * 100)}
              onChange={(v) => setQuality(v / 100)}
              label={null}
              aria-labelledby="export-quality-label"
              thumbLabel={t('quality')}
            />
          </div>
        )}

        {onEstimate && (
          <Group gap="xs" justify="space-between" wrap="wrap">
            <Text size="sm" data-testid="export-estimate" aria-live="polite">
              {estimate === 'working'
                ? t('estimating')
                : estimate === 'unknown'
                  ? t('estimateUnknown')
                  : t('fileSize', { size: t.msg(estimateSizeMsg(estimate.bytes)) })}
            </Text>
            {info.lossy && (
              <Button size="xs" fz="xs" variant="light" loading={fitting} disabled={blocked} onClick={() => void underTwoMb()}>
                {t('underTwoMbButton')}
              </Button>
            )}
          </Group>
        )}
        {fitNote && (
          <Text size="xs" c="dimmed" data-testid="export-fit-note">
            {fitNote}
          </Text>
        )}

        <TextInput
          label={t('fileName')}
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          description={t('savedAs', { fileName, width: f.number(width, { useGrouping: false }), height: f.number(height, { useGrouping: false }) })}
        />

        {notice && (
          <Alert color="yellow" variant="light" p="xs">
            <Text size="xs">{notice}</Text>
          </Alert>
        )}
        {aiLabel && (
          <Text size="xs" c="dimmed" data-testid="export-ai-label-note">
            {t('aiLabelNote')}
          </Text>
        )}
        {blocker && (
          <Group gap="xs" wrap="nowrap" justify="space-between">
            <Text size="xs" c="dimmed">
              {blocker}
            </Text>
            {onRetry && (
              <Button size="xs" fz="xs" variant="light" onClick={onRetry}>
                {t('tryAgain')}
              </Button>
            )}
          </Group>
        )}
        {result && (
          <Alert color={result.tone === 'ok' ? 'green' : 'red'} variant="light" p="xs">
            <Text size="xs">{result.text}</Text>
          </Alert>
        )}

        <Group grow gap="xs">
          <Button
            variant="default"
            leftSection={<IconDownload size={16} />}
            loading={busy === 'download'}
            disabled={blocker !== null || (busy !== null && busy !== 'download')}
            onClick={() => void run('download')}
          >
            {t('download')}
          </Button>
          <Button
            leftSection={<IconDeviceFloppy size={16} />}
            loading={busy === 'project'}
            disabled={blocker !== null || (busy !== null && busy !== 'project')}
            onClick={() => void run('project')}
          >
            {t('saveToProject')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
