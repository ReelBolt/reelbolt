'use client';

import { useState } from 'react';
import { Box, Button, Group, Modal, NumberInput, Radio, Select, Stack, Text, TextInput } from '@mantine/core';
import type { EditTimeline, TextPosition, TitleTemplate } from '@/lib/editing/types';
import { sanitizeTitleText, hasRemovedCharacters, REMOVED_CHARACTERS_NOTE } from '@/lib/editing/title-text';
import { trackDisplayName } from '@/lib/editing/media/place';
import { CARD_DEFAULT_SECONDS, TITLE_DEFAULT_SECONDS, TITLE_TEMPLATE_CHOICES, type GraphicSpec } from '@/lib/editing/structure/graphics';
import { ColorSwatchPicker } from './ColorSwatchPicker';
import { TitlePositionPad } from './TitlePositionPad';
import { useT, type T } from '@/i18n';

const AUTO_TRACK = 'auto';

interface DialogBase {
  opened: boolean;
  doc: EditTimeline;
  onClose: () => void;
  /** Places the graphic; the caller applies it as one undo step and reports where it landed. */
  onSubmit: (spec: GraphicSpec, trackId: string | null) => void;
}

function trackChoices(doc: EditTimeline, t: T): Array<{ value: string; label: string }> {
  return [{ value: AUTO_TRACK, label: t('autoTrack') }, ...doc.tracks.filter((t) => t.kind === 'video').map((t) => ({ value: t.id, label: trackDisplayName(t) }))];
}

const previewBox = {
  aspectRatio: '16 / 9',
  background: '#101114',
  borderRadius: 8,
  display: 'flex',
  overflow: 'hidden',
} as const;

function TitlePreview({ text, template }: { text: string; template: TitleTemplate }) {
  const t = useT('editor-misc.graphics');
  const shown = text.trim() === '' ? t('yourTitle') : text;
  const lower = template === 'lowerThird';
  const base = { color: '#ffffff', fontWeight: 700, fontSize: lower ? 16 : 24, lineHeight: 1.2, padding: '4px 10px', maxWidth: '90%', textAlign: 'center' as const };
  const styled =
    template === 'boxed'
      ? { ...base, background: 'rgba(0,0,0,0.75)', border: '1px solid rgba(255,255,255,0.3)' }
      : template === 'outline'
        ? { ...base, textShadow: '-1px -1px 0 #000, 1px -1px 0 #000, -1px 1px 0 #000, 1px 1px 0 #000, 0 0 6px #000' }
        : base;
  return (
    <Box style={{ ...previewBox, background: template === 'card' ? '#1d3557' : '#101114', alignItems: lower ? 'flex-end' : 'center', justifyContent: lower ? 'flex-start' : 'center', padding: lower ? '0 0 12px 14px' : 0 }} data-testid="title-preview" data-template={template} aria-label={t('titlePreview')} role="img">
      <div style={styled}>{shown}</div>
    </Box>
  );
}

/** R10.5: add a title with a template, text, length and track. */
export function AddTitleDialog({ opened, doc, onClose, onSubmit }: DialogBase) {
  const t = useT('editor-misc.graphics');
  const [template, setTemplate] = useState<TitleTemplate>('plain');
  const [position, setPosition] = useState<TextPosition | null>(null);
  const [text, setText] = useState(() => t('defaultTitle'));
  const [seconds, setSeconds] = useState<number>(TITLE_DEFAULT_SECONDS);
  const [track, setTrack] = useState<string>(AUTO_TRACK);
  const clean = sanitizeTitleText(text);
  const ready = clean.trim().length > 0 && seconds > 0;
  return (
    <Modal opened={opened} onClose={onClose} title={t('addTitleTitle')} size="lg" centered>
      <Stack gap="sm" data-testid="add-title-dialog">
        <TitlePreview text={clean} template={template} />
        <Radio.Group label={t('style')} value={template} onChange={(v) => setTemplate(v as TitleTemplate)}>
          <Stack gap={6} mt={6}>
            {TITLE_TEMPLATE_CHOICES.map((c) => (
              <Radio key={c.id} value={c.id} label={t(`templates.${c.id}.label`)} description={t(`templates.${c.id}.hint`)} data-testid={`title-template-${c.id}`} />
            ))}
          </Stack>
        </Radio.Group>
        <TitlePositionPad value={position} onChange={setPosition} disabled={template === 'lowerThird' || template === 'card'} />
        {(template === 'lowerThird' || template === 'card') && (
          <Text size="xs" c="dimmed" data-testid="title-position-note">
            {t('positionNote')}
          </Text>
        )}
        <TextInput label={t('text')} value={text} onChange={(e) => setText(e.currentTarget.value)} onKeyDown={(e) => e.stopPropagation()} error={hasRemovedCharacters(text) ? REMOVED_CHARACTERS_NOTE : undefined} data-testid="title-text" />
        <Group grow align="flex-start">
          <NumberInput label={t('length')} min={0.5} max={600} step={0.5} decimalScale={1} value={seconds} onChange={(v) => setSeconds(typeof v === 'number' ? v : TITLE_DEFAULT_SECONDS)} onKeyDown={(e) => e.stopPropagation()} data-testid="title-seconds" />
          <Select label={t('track')} data={trackChoices(doc, t)} value={track} allowDeselect={false} onChange={(v) => setTrack(v ?? AUTO_TRACK)} comboboxProps={{ withinPortal: true }} data-testid="title-track" />
        </Group>
        <Text size="xs" c="dimmed">
          {t('titleHint')}
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t('cancel')}
          </Button>
          <Button disabled={!ready} onClick={() => onSubmit({ kind: 'title', text: clean, template, ...(position && template !== 'lowerThird' && template !== 'card' ? { position } : {}), seconds }, track === AUTO_TRACK ? null : track)} data-testid="title-add">
            {t('addTitle')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** R10.5: add a colour card (a solid colour) with a chosen colour, length and track. */
export function AddColorCardDialog({ opened, doc, onClose, onSubmit }: DialogBase) {
  const t = useT('editor-misc.graphics');
  const [color, setColor] = useState('#000000');
  const [seconds, setSeconds] = useState<number>(CARD_DEFAULT_SECONDS);
  const [track, setTrack] = useState<string>(AUTO_TRACK);
  return (
    <Modal opened={opened} onClose={onClose} title={t('addCardTitle')} size="md" centered>
      <Stack gap="sm" data-testid="add-card-dialog">
        <Box style={{ ...previewBox, background: color }} data-testid="card-preview" role="img" aria-label={t('cardPreview', { color })} />
        <ColorSwatchPicker value={color} onChange={setColor} testIdPrefix="card-swatch" />
        <Group grow align="flex-start">
          <NumberInput label={t('length')} min={0.5} max={600} step={0.5} decimalScale={1} value={seconds} onChange={(v) => setSeconds(typeof v === 'number' ? v : CARD_DEFAULT_SECONDS)} onKeyDown={(e) => e.stopPropagation()} data-testid="card-seconds" />
          <Select label={t('track')} data={trackChoices(doc, t)} value={track} allowDeselect={false} onChange={(v) => setTrack(v ?? AUTO_TRACK)} comboboxProps={{ withinPortal: true }} data-testid="card-track" />
        </Group>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t('cancel')}
          </Button>
          <Button onClick={() => onSubmit({ kind: 'colorCard', color, seconds }, track === AUTO_TRACK ? null : track)} data-testid="card-add">
            {t('addCard')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
