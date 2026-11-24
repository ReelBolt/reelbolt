'use client';

import React, { useRef, useState } from 'react';
import { Alert, Button, FileButton, Group, Modal, NumberInput, ScrollArea, SegmentedControl, Select, Stack, Table, Text } from '@mantine/core';
import { useIsTouchSurface } from '@/lib/editing/use-device-class';
import type { EditOp } from '@/lib/editing/engine';
import type { CaptionLook, EditTimeline, TextPosition } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import { compileImportCues, findCaptionTrack, MAX_IMPORT_BYTES, parseSubtitleFile, type ParsedSubtitles } from '@/lib/editing/subtitles/subtitles';
import { useT } from '@/i18n';

export interface SubtitleImportDialogProps {
  opened: boolean;
  onClose: () => void;
  doc: EditTimeline;
  readOnly: boolean;
  look: CaptionLook;
  position: TextPosition;
  language: string;
  newItemId: () => string;
  newTrackId: () => string;
  onApply: (ops: readonly EditOp[], label: string) => void;
}


/**
 * R12.9: import an SRT or WebVTT file as captions. The file is read in the browser; the cues are
 * previewed (count, first rows, anything skipped and why) before anything is written, then added to
 * the chosen track as one undo step, replacing what is there or appended to it.
 */
export function SubtitleImportDialog({ opened, onClose, doc, readOnly, look, position, language, newItemId, newTrackId, onApply }: SubtitleImportDialogProps) {
  const tx = useT('editor.panels.subtitleImportDialog');
  const touch = useIsTouchSurface();
  const [fileName, setFileName] = useState<string | null>(null);
  const [parsed, setParsed] = useState<ParsedSubtitles | null>(null);
  const [fatal, setFatal] = useState<string | null>(null);
  const [mode, setMode] = useState<'replace' | 'append'>('append');
  const [target, setTarget] = useState<string>('__new__');
  const [offsetSec, setOffsetSec] = useState(0);
  const resetRef = useRef<() => void>(null);
  const existing = findCaptionTrack(doc);
  const videoTracks = doc.tracks.filter((t) => t.kind === 'video');
  const size = touch ? 'md' : 'compact-xs';
  const style = touch ? { minHeight: 44 } : undefined;

  const pick = async (file: File | null) => {
    if (!file) return;
    setFileName(file.name);
    setParsed(null);
    setFatal(null);
    if (file.size > MAX_IMPORT_BYTES) {
      setFatal(tx('thatFileIsToo'));
      return;
    }
    try {
      setParsed(parseSubtitleFile(await file.text()));
    } catch {
      setFatal(tx('thatFileCouldNot'));
    }
  };

  const trackId = target === '__new__' ? null : target;
  let previewCounter = 0;
  const plan = parsed && parsed.cues.length > 0 ? compileImportCues(doc, { cues: parsed.cues, trackId, mode, offsetSec, language, look, position, newItemId: () => `preview-${++previewCounter}`, newTrackId: () => 'preview-track' }) : null;
  const rate = doc.settings.rate;
  const fps = rate.den === 0 ? 30 : rate.num / rate.den;

  const add = () => {
    if (!parsed) return;
    const real = compileImportCues(doc, { cues: parsed.cues, trackId, mode, offsetSec, language, look, position, newItemId, newTrackId });
    if (real.ops.length === 0) return;
    onApply(real.ops, `Import ${real.inserted} caption${real.inserted === 1 ? '' : 's'}`);
    close();
  };
  const close = () => {
    setParsed(null);
    setFileName(null);
    setFatal(null);
    setOffsetSec(0);
    resetRef.current?.();
    onClose();
  };

  return (
    <Modal opened={opened} onClose={close} title={tx('importCaptions')} size="lg" fullScreen={touch} closeButtonProps={{ 'aria-label': 'Close', size: touch ? 44 : undefined }} data-testid="subtitle-import-dialog">
      <Stack gap="sm">
        <Group gap="xs">
          <FileButton onChange={(f) => void pick(f)} accept=".srt,.vtt,text/vtt,application/x-subrip" resetRef={resetRef}>
            {(props) => (
              <Button {...props} size={size} fz="xs" style={style} data-testid="subtitle-import-choose">
                {tx('chooseAnSrtOr')}
              </Button>
            )}
          </FileButton>
          {fileName && <Text size="xs" c="dimmed" data-testid="subtitle-import-file">{fileName}</Text>}
        </Group>
        {fatal && <Alert color="red" p="xs" data-testid="subtitle-import-error">{fatal}</Alert>}
        {parsed && parsed.problems.length > 0 && (
          <Alert color={parsed.cues.length > 0 ? 'yellow' : 'red'} p="xs" title={parsed.cues.length > 0 ? tx('someCaptionsWereSkipped') : tx('nothingToImport')} data-testid="subtitle-import-problems">
            <Stack gap={2}>
              {parsed.problems.slice(0, 6).map((p) => (
                <Text key={p} size="xs">{p}</Text>
              ))}
              {parsed.problems.length > 6 && <Text size="xs">{tx('and')} {parsed.problems.length - 6} {tx('more')}</Text>}
            </Stack>
          </Alert>
        )}
        {parsed && parsed.cues.length > 0 && (
          <>
            <Text size="sm" fw={600} data-testid="subtitle-import-count">
              {tx('captionsFound', { count: parsed.cues.length, format: parsed.format === 'vtt' ? tx('webvtt') : tx('srt') })}
            </Text>
            <ScrollArea.Autosize mah={180}>
              <Table withTableBorder fz="xs" data-testid="subtitle-import-preview">
                <Table.Tbody>
                  {parsed.cues.slice(0, 8).map((c, i) => (
                    <Table.Tr key={i}>
                      <Table.Td ff="monospace">{framesToTimecode(Math.max(0, Math.round((c.startSec + offsetSec) * fps)), rate)}</Table.Td>
                      <Table.Td>{c.text.replace(/\n/g, ' / ')}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </ScrollArea.Autosize>
            {parsed.cues.length > 8 && <Text size="xs" c="dimmed">{tx('and2')} {parsed.cues.length - 8} {tx('more2')}</Text>}
            <Select
              label={tx('putCaptionsOn')}
              size={touch ? 'md' : 'xs'}
              data={[{ value: '__new__', label: existing ? tx('captionsTrack', { name: existing.name }) : tx('newTrackSubtitles') }, ...videoTracks.filter((t) => t.id !== existing?.id).map((t) => ({ value: t.id, label: t.name }))]}
              value={target}
              onChange={(v) => setTarget(v ?? '__new__')}
              allowDeselect={false}
              data-testid="subtitle-import-track"
            />
            <div>
              <Text size="xs" fw={500} mb={2}>
                {tx('ifTheTrackAlready')}
              </Text>
              <SegmentedControl size={touch ? 'md' : 'xs'} data={[{ value: 'append', label: tx('addToThem') }, { value: 'replace', label: tx('replaceThem') }]} value={mode} onChange={(v) => setMode(v as 'replace' | 'append')} data-testid="subtitle-import-mode" />
            </div>
            <NumberInput label={tx('startTheCaptionsThis')} size={touch ? 'md' : 'xs'} step={0.5} decimalScale={2} value={offsetSec} onChange={(v) => setOffsetSec(typeof v === 'number' ? v : 0)} data-testid="subtitle-import-offset" />
            {plan && (
              <Text size="xs" c="dimmed" data-testid="subtitle-import-summary">
                {tx('importSummary', { inserted: plan.inserted, removed: plan.removed, skipped: plan.skipped })}
              </Text>
            )}
          </>
        )}
        <Group justify="flex-end" gap={touch ? 'sm' : 6}>
          <Button size={size} fz="xs" style={style} variant="subtle" onClick={close} data-testid="subtitle-import-cancel">
            {tx('cancel')}
          </Button>
          <Button size={size} fz="xs" style={style} disabled={readOnly || !plan || plan.ops.length === 0 || plan.inserted === 0} onClick={add} data-testid="subtitle-import-apply">
            {tx('addCaptions')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
