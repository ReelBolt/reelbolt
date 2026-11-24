'use client';

import React, { useState } from 'react';
import {
  Alert,
  Button,
  ColorInput,
  Group,
  Input,
  NumberInput,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Text,
  Textarea,
} from '@mantine/core';
import { IconChevronDown, IconChevronUp, IconTypography } from '@tabler/icons-react';
import {
  EDITOR_FONTS,
  TEXT_LIMITS,
  findFont,
  nearestWeight,
  validateTextContent,
} from '@/lib/editor/text-content';
import type { TextAlign, TextContent } from '@/lib/types/image-document';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';

export interface TextPanelProps {
  /** The selected text layer's content, or null when the selection is not a text layer. */
  content: TextContent | null;
  layerId: string | null;
  onAdd(): void;
  /** Called with valid content only; `mergeKey` groups a run of edits into one undo step. */
  onChange(content: TextContent, mergeKey: string): void;
  /** Phone: the basics first (words, font, size, colour, capitals, outline and shadow), the rest under "More" (R13.8). */
  phone?: boolean;
}

const alignData = (t: T): { value: TextAlign; label: string }[] => [
  { value: 'left', label: t('alignLeft') },
  { value: 'center', label: t('alignCentre') },
  { value: 'right', label: t('alignRight') },
];

const num = (v: string | number, fallback: number) => (typeof v === 'number' && Number.isFinite(v) ? v : fallback);

/**
 * Edits one text layer. The panel keeps its own draft of the fields so a half-typed value (an empty
 * box, a font size mid-edit) never reaches the document; only content that passes
 * {@link validateTextContent} is committed, and the reasons it would not are shown beside the field
 * instead of surfacing later as a refused save.
 */
export function TextPanel({ content, layerId, onAdd, onChange, phone = false }: TextPanelProps) {
  const t = useT('editor-thumbnail-b.text');
  return (
    <Stack gap="sm">
      <Button size="xs" fz="xs" variant="light" h={phone ? 44 : undefined} leftSection={<IconTypography size={14} />} onClick={onAdd}>
        {t('add')}
      </Button>
      {content && layerId ? (
        // Keyed by layer: switching layers starts a fresh draft.
        <TextFields key={layerId} content={content} layerId={layerId} onChange={onChange} phone={phone} />
      ) : (
        <Text size="xs" c="dimmed">
          {t('hint')}
        </Text>
      )}
    </Stack>
  );
}

function TextFields({
  content,
  layerId,
  onChange,
  phone,
}: {
  content: TextContent;
  layerId: string;
  onChange: TextPanelProps['onChange'];
  phone: boolean;
}) {
  const t = useT('editor-thumbnail-b.text');
  const [more, setMore] = useState(false);
  const [draft, setDraft] = useState(content);
  const [seen, setSeen] = useState(content);
  // The document changed under us (undo, redo, a reload): take its version as the new draft.
  if (content !== seen) {
    setSeen(content);
    setDraft(content);
  }

  const problems = validateTextContent(draft);
  const font = findFont(draft.fontFamily);

  const update = (patch: Partial<TextContent>) => {
    const next = { ...draft, ...patch };
    setDraft(next);
    if (validateTextContent(next).length === 0) onChange(next, `text:${layerId}`);
  };

  if (phone) {
    return (
      <PhoneTextFields
        draft={draft}
        update={update}
        problems={problems}
        more={more}
        onMore={setMore}
        weightsOf={font?.weights ?? [400, 700]}
      />
    );
  }

  return (
    <Stack gap="xs">
      <Textarea
        size="xs"
        label={t('words')}
        autosize
        minRows={2}
        maxRows={6}
        maxLength={TEXT_LIMITS.maxLength}
        value={draft.text}
        onChange={(e) => update({ text: e.currentTarget.value })}
      />

      <Group grow gap="xs" align="flex-end">
        <Select
          size="xs"
          label={t('font')}
          searchable
          allowDeselect={false}
          data={EDITOR_FONTS.map((f) => ({ value: f.family, label: f.family }))}
          value={draft.fontFamily}
          onChange={(family) => {
            const next = family ? findFont(family) : null;
            if (next) update({ fontFamily: next.family, fontWeight: nearestWeight(next, draft.fontWeight) });
          }}
        />
        <Select
          size="xs"
          label={t('weight')}
          allowDeselect={false}
          data={(font?.weights ?? [400, 700]).map((w) => ({ value: String(w), label: String(w) }))}
          value={String(draft.fontWeight)}
          onChange={(w) => w && update({ fontWeight: Number(w) })}
        />
      </Group>
      {font && (
        <Text size="xs" c="dimmed">
          {font.note}
        </Text>
      )}

      <Group grow gap="xs">
        <NumberInput
          size="xs"
          label={t('size')}
          min={TEXT_LIMITS.fontSize.min}
          max={TEXT_LIMITS.fontSize.max}
          value={draft.fontSize}
          onChange={(v) => update({ fontSize: num(v, draft.fontSize) })}
        />
        <ColorInput size="xs" label={t('colour')} value={draft.color} onChange={(color) => update({ color })} />
      </Group>

      <SegmentedControl
        size="xs"
        fullWidth
        aria-label={t('alignment')}
        data={alignData(t)}
        value={draft.align}
        onChange={(v) => update({ align: v as TextAlign })}
      />

      <Group grow gap="xs">
        <NumberInput
          size="xs"
          label={t('lineHeight')}
          step={0.05}
          decimalScale={2}
          min={TEXT_LIMITS.lineHeight.min}
          max={TEXT_LIMITS.lineHeight.max}
          value={draft.lineHeight}
          onChange={(v) => update({ lineHeight: num(v, draft.lineHeight) })}
        />
        <NumberInput
          size="xs"
          label={t('letterSpacing')}
          min={TEXT_LIMITS.letterSpacing.min}
          max={TEXT_LIMITS.letterSpacing.max}
          value={draft.letterSpacing}
          onChange={(v) => update({ letterSpacing: num(v, draft.letterSpacing) })}
        />
      </Group>

      <Switch
        size="xs"
        label={t('allCaps')}
        checked={draft.uppercase}
        onChange={(e) => update({ uppercase: e.currentTarget.checked })}
      />

      <Switch
        size="xs"
        label={t('wrap')}
        checked={draft.maxWidth !== null}
        onChange={(e) => update({ maxWidth: e.currentTarget.checked ? 900 : null })}
      />
      {draft.maxWidth !== null && (
        <NumberInput
          size="xs"
          label={t('wrapWidth')}
          min={TEXT_LIMITS.maxWidth.min}
          max={TEXT_LIMITS.maxWidth.max}
          value={draft.maxWidth}
          onChange={(v) => update({ maxWidth: num(v, draft.maxWidth ?? 900) })}
        />
      )}

      <Switch
        size="xs"
        label={t('outline')}
        checked={draft.stroke !== null}
        onChange={(e) => update({ stroke: e.currentTarget.checked ? { color: '#000000', width: 6 } : null })}
      />
      {draft.stroke && (
        <Group grow gap="xs">
          <ColorInput
            size="xs"
            label={t('outlineColour')}
            value={draft.stroke.color}
            onChange={(color) => update({ stroke: { ...(draft.stroke ?? { width: 6 }), color } })}
          />
          <NumberInput
            size="xs"
            label={t('outlineWidth')}
            min={TEXT_LIMITS.strokeWidth.min}
            max={TEXT_LIMITS.strokeWidth.max}
            value={draft.stroke.width}
            onChange={(v) => update({ stroke: { ...(draft.stroke ?? { color: '#000000' }), width: num(v, draft.stroke?.width ?? 6) } })}
          />
        </Group>
      )}

      <Switch
        size="xs"
        label={t('shadow')}
        checked={draft.shadow !== null}
        onChange={(e) =>
          update({ shadow: e.currentTarget.checked ? { color: 'rgba(0,0,0,0.6)', blur: 18, offsetX: 0, offsetY: 8 } : null })
        }
      />
      {draft.shadow && (
        <Stack gap="xs">
          <ColorInput
            size="xs"
            label={t('shadowColour')}
            value={draft.shadow.color}
            onChange={(color) => update({ shadow: { ...(draft.shadow ?? { blur: 18, offsetX: 0, offsetY: 8 }), color } })}
          />
          <Group grow gap="xs">
            <NumberInput
              size="xs"
              label={t('blur')}
              min={TEXT_LIMITS.shadowBlur.min}
              max={TEXT_LIMITS.shadowBlur.max}
              value={draft.shadow.blur}
              onChange={(v) =>
                update({ shadow: { ...(draft.shadow as NonNullable<TextContent['shadow']>), blur: num(v, draft.shadow?.blur ?? 18) } })
              }
            />
            <NumberInput
              size="xs"
              label={t('across')}
              min={TEXT_LIMITS.shadowOffset.min}
              max={TEXT_LIMITS.shadowOffset.max}
              value={draft.shadow.offsetX}
              onChange={(v) =>
                update({ shadow: { ...(draft.shadow as NonNullable<TextContent['shadow']>), offsetX: num(v, draft.shadow?.offsetX ?? 0) } })
              }
            />
            <NumberInput
              size="xs"
              label={t('down')}
              min={TEXT_LIMITS.shadowOffset.min}
              max={TEXT_LIMITS.shadowOffset.max}
              value={draft.shadow.offsetY}
              onChange={(v) =>
                update({ shadow: { ...(draft.shadow as NonNullable<TextContent['shadow']>), offsetY: num(v, draft.shadow?.offsetY ?? 8) } })
              }
            />
          </Group>
        </Stack>
      )}

      {problems.length > 0 && (
        <Alert color="yellow" variant="light" p="xs">
          {problems.map((p) => (
            <Text key={p} size="xs">
              {p}
            </Text>
          ))}
        </Alert>
      )}
    </Stack>
  );
}

/** The outline and shadow strength people see (1 to 24), read from the outline width, else from the shadow blur. */
export function decorationStrength(c: TextContent): number {
  if (c.stroke) return Math.max(1, Math.min(24, Math.round(c.stroke.width)));
  if (c.shadow) return Math.max(1, Math.min(24, Math.round(c.shadow.blur / 3)));
  return 6;
}

/** Turns outline and shadow on (both, at `strength`) or off (both). Colours and detail already set are kept. */
export function withDecoration(c: TextContent, on: boolean, strength = decorationStrength(c)): Partial<TextContent> {
  if (!on) return { stroke: null, shadow: null };
  return {
    stroke: { color: c.stroke?.color ?? '#000000', width: strength },
    shadow: c.shadow
      ? { ...c.shadow, blur: strength * 3 }
      : { color: 'rgba(0,0,0,0.6)', blur: strength * 3, offsetX: 0, offsetY: 8 },
  };
}

function PhoneTextFields({
  draft,
  update,
  problems,
  more,
  onMore,
  weightsOf,
}: {
  draft: TextContent;
  update(patch: Partial<TextContent>): void;
  problems: string[];
  more: boolean;
  onMore(open: boolean): void;
  weightsOf: readonly number[];
}) {
  const t = useT('editor-thumbnail-b.text');
  const decorated = draft.stroke !== null || draft.shadow !== null;
  const tall = { input: { minHeight: 44 } };
  return (
    <Stack gap="sm" data-testid="text-panel-phone">
      <Textarea
        label={t('words')}
        autosize
        minRows={2}
        maxRows={6}
        maxLength={TEXT_LIMITS.maxLength}
        value={draft.text}
        onChange={(e) => update({ text: e.currentTarget.value })}
      />
      <Select
        label={t('font')}
        allowDeselect={false}
        styles={tall}
        data={EDITOR_FONTS.map((f) => ({ value: f.family, label: f.family }))}
        value={draft.fontFamily}
        onChange={(family) => {
          const next = family ? findFont(family) : null;
          if (next) update({ fontFamily: next.family, fontWeight: nearestWeight(next, draft.fontWeight) });
        }}
      />
      <Group grow gap="xs" align="flex-end">
        <NumberInput
          label={t('size')}
          styles={tall}
          min={TEXT_LIMITS.fontSize.min}
          max={TEXT_LIMITS.fontSize.max}
          value={draft.fontSize}
          onChange={(v) => update({ fontSize: num(v, draft.fontSize) })}
        />
        <Input.Wrapper label={t('colour')}>
          <Input
            component="input"
            type="color"
            styles={tall}
            aria-label={t('textColour')}
            value={/^#[0-9a-f]{6}$/i.test(draft.color) ? draft.color : '#ffffff'}
            onChange={(e) => update({ color: e.currentTarget.value })}
            data-testid="text-colour"
          />
        </Input.Wrapper>
      </Group>
      <Switch
        label={t('allCaps')}
        checked={draft.uppercase}
        onChange={(e) => update({ uppercase: e.currentTarget.checked })}
      />
      <Switch
        label={t('decoration')}
        description={t('decorationHint')}
        checked={decorated}
        onChange={(e) => update(withDecoration(draft, e.currentTarget.checked))}
        data-testid="text-decoration-switch"
      />
      {decorated && (
        <NumberInput
          label={t('decorationStrength')}
          styles={tall}
          min={1}
          max={24}
          value={decorationStrength(draft)}
          onChange={(v) => update(withDecoration(draft, true, Math.max(1, Math.min(24, num(v, decorationStrength(draft))))))}
          data-testid="text-decoration-strength"
        />
      )}

      <Button
        variant="subtle"
        h={44}
        rightSection={more ? <IconChevronUp size={16} /> : <IconChevronDown size={16} />}
        aria-expanded={more}
        onClick={() => onMore(!more)}
        data-testid="text-more"
      >
        {t('more')}
      </Button>
      {more && (
        <Stack gap="sm" data-testid="text-more-fields">
          <Select
            label={t('weight')}
            allowDeselect={false}
            styles={tall}
            data={weightsOf.map((w) => ({ value: String(w), label: String(w) }))}
            value={String(draft.fontWeight)}
            onChange={(w) => w && update({ fontWeight: Number(w) })}
          />
          <SegmentedControl
            fullWidth
            aria-label={t('alignment')}
            data={alignData(t)}
            value={draft.align}
            onChange={(v) => update({ align: v as TextAlign })}
            styles={{ label: { minHeight: 36, display: 'flex', alignItems: 'center', justifyContent: 'center' } }}
          />
          <Group grow gap="xs">
            <NumberInput
              label={t('lineHeight')}
              styles={tall}
              step={0.05}
              decimalScale={2}
              min={TEXT_LIMITS.lineHeight.min}
              max={TEXT_LIMITS.lineHeight.max}
              value={draft.lineHeight}
              onChange={(v) => update({ lineHeight: num(v, draft.lineHeight) })}
            />
            <NumberInput
              label={t('letterSpacing')}
              styles={tall}
              min={TEXT_LIMITS.letterSpacing.min}
              max={TEXT_LIMITS.letterSpacing.max}
              value={draft.letterSpacing}
              onChange={(v) => update({ letterSpacing: num(v, draft.letterSpacing) })}
            />
          </Group>
          <Switch
            label={t('wrap')}
            checked={draft.maxWidth !== null}
            onChange={(e) => update({ maxWidth: e.currentTarget.checked ? 900 : null })}
          />
          {draft.maxWidth !== null && (
            <NumberInput
              label={t('wrapWidth')}
              styles={tall}
              min={TEXT_LIMITS.maxWidth.min}
              max={TEXT_LIMITS.maxWidth.max}
              value={draft.maxWidth}
              onChange={(v) => update({ maxWidth: num(v, draft.maxWidth ?? 900) })}
            />
          )}
          {draft.stroke && (
            <Group grow gap="xs">
              <ColorInput
                label={t('outlineColour')}
                styles={tall}
                value={draft.stroke.color}
                onChange={(color) => update({ stroke: { ...(draft.stroke ?? { width: 6 }), color } })}
              />
              <NumberInput
                label={t('outlineWidth')}
                styles={tall}
                min={TEXT_LIMITS.strokeWidth.min}
                max={TEXT_LIMITS.strokeWidth.max}
                value={draft.stroke.width}
                onChange={(v) => update({ stroke: { ...(draft.stroke ?? { color: '#000000' }), width: num(v, draft.stroke?.width ?? 6) } })}
              />
            </Group>
          )}
          {draft.shadow && (
            <Stack gap="xs">
              <ColorInput
                label={t('shadowColour')}
                styles={tall}
                value={draft.shadow.color}
                onChange={(color) => update({ shadow: { ...(draft.shadow ?? { blur: 18, offsetX: 0, offsetY: 8 }), color } })}
              />
              <Group grow gap="xs">
                <NumberInput
                  label={t('blur')}
                  styles={tall}
                  min={TEXT_LIMITS.shadowBlur.min}
                  max={TEXT_LIMITS.shadowBlur.max}
                  value={draft.shadow.blur}
                  onChange={(v) => update({ shadow: { ...(draft.shadow as NonNullable<TextContent['shadow']>), blur: num(v, draft.shadow?.blur ?? 18) } })}
                />
                <NumberInput
                  label={t('across')}
                  styles={tall}
                  min={TEXT_LIMITS.shadowOffset.min}
                  max={TEXT_LIMITS.shadowOffset.max}
                  value={draft.shadow.offsetX}
                  onChange={(v) => update({ shadow: { ...(draft.shadow as NonNullable<TextContent['shadow']>), offsetX: num(v, draft.shadow?.offsetX ?? 0) } })}
                />
                <NumberInput
                  label={t('down')}
                  styles={tall}
                  min={TEXT_LIMITS.shadowOffset.min}
                  max={TEXT_LIMITS.shadowOffset.max}
                  value={draft.shadow.offsetY}
                  onChange={(v) => update({ shadow: { ...(draft.shadow as NonNullable<TextContent['shadow']>), offsetY: num(v, draft.shadow?.offsetY ?? 8) } })}
                />
              </Group>
            </Stack>
          )}
        </Stack>
      )}

      {problems.length > 0 && (
        <Alert color="yellow" variant="light" p="xs">
          {problems.map((p) => (
            <Text key={p} size="xs">
              {p}
            </Text>
          ))}
        </Alert>
      )}
    </Stack>
  );
}
