'use client';

import { Stack, Select, NumberInput, Switch, SimpleGrid, Text, Box, UnstyledButton } from '@mantine/core';
import type {
  VideoCompileStepConfig as VideoCompileStepConfigValue,
  CaptionStyle,
  CaptionFont,
  CaptionPosition,
} from '@/lib/types/workflow';
import { useT } from '@/i18n';
import { useFd } from './config-kit';
import classes from './CaptionStyleFields.module.css';

/** Mirrors `CaptionStyle` in VideoCompileStepConfig.cs. Fixed list — the engine maps each to a first-party layout. */
// Labels: wfSteps.captionStyle.style.<value>
export const CAPTION_STYLE_OPTIONS: { value: CaptionStyle }[] = [
  { value: 'Subtitle' },
  { value: 'Karaoke' },
  { value: 'Punch' },
  { value: 'Bold' },
];

/** The font each style uses when the font is left on Automatic — mirrors `CaptionFontCatalog.ForStyle`. */
export const AUTO_FONT_BY_STYLE: Record<CaptionStyle, Exclude<CaptionFont, 'Auto'>> = {
  Subtitle: 'Inter',
  Karaoke: 'Montserrat',
  Punch: 'Anton',
  Bold: 'Poppins',
};

/**
 * Mirrors `CaptionFont` — only fonts the workflow-engine image really ships. `family` is the CSS
 * family the sample renders in (the same files, served from public/fonts/captions).
 */
// Name and hint: wfSteps.captionStyle.font.<value>.name / .hint
export const CAPTION_FONT_OPTIONS: { value: CaptionFont; family: string | null }[] = [
  { value: 'Auto', family: null },
  { value: 'Inter', family: "'ReelBolt Caption Inter', sans-serif" },
  { value: 'Montserrat', family: "'ReelBolt Caption Montserrat', sans-serif" },
  { value: 'Poppins', family: "'ReelBolt Caption Poppins', sans-serif" },
  { value: 'BebasNeue', family: "'ReelBolt Caption Bebas Neue', sans-serif" },
  { value: 'Anton', family: "'ReelBolt Caption Anton', sans-serif" },
  { value: 'DejaVuSans', family: "'DejaVu Sans', Verdana, sans-serif" },
];

/** Mirrors `CaptionPosition`. */
// Labels: wfSteps.captionStyle.position.<value>
export const CAPTION_POSITION_OPTIONS: { value: CaptionPosition }[] = [
  { value: 'Auto' },
  { value: 'Bottom' },
  { value: 'Center' },
  { value: 'Top' },
];

// Labels: wfSteps.captionStyle.textColor.<value> / boxColor.<key> (see boxKey)
const TEXT_COLOR_VALUES = ['white', 'yellow', 'black'];
const BOX_COLOR_VALUES = ['black@0.6', 'black@0.45', 'black', 'none'];
const boxKey = (value: string) => value.replace(/[@.]/g, '_');

/** 0 and 4 (the default every config saved before the automatic size carries) mean "automatic" — mirrors `CaptionLayout.IsAutoSize`. */
export function isAutoCaptionSize(pct: number | undefined | null): boolean {
  return pct === undefined || pct === null || pct === 0 || pct === 4;
}

/** The CSS family a font option renders its sample in; Automatic follows the style. */
export function captionFontFamily(font: CaptionFont, style: CaptionStyle): string {
  const effective = font === 'Auto' ? AUTO_FONT_BY_STYLE[style] : font;
  return CAPTION_FONT_OPTIONS.find((o) => o.value === effective)?.family ?? 'sans-serif';
}

/** An allowlisted caption colour (`white`, `#RRGGBB`, `black@0.6`...) as CSS; anything else is null. */
export function captionColorCss(value: string | undefined | null): string | null {
  if (!value) return null;
  const m = /^(white|black|yellow|#[0-9a-f]{6})(?:@(0(?:\.\d{1,3})?|1(?:\.0{1,3})?))?$/i.exec(value);
  if (!m) return null;
  const base = m[1].toLowerCase();
  const rgb = base === 'white' ? [255, 255, 255]
    : base === 'black' ? [0, 0, 0]
      : base === 'yellow' ? [255, 255, 0]
        : [1, 3, 5].map((i) => parseInt(base.slice(i, i + 2), 16));
  const alpha = m[2] === undefined ? 1 : Number(m[2]);
  return `rgba(${rgb[0]}, ${rgb[1]}, ${rgb[2]}, ${alpha})`;
}

/** Keeps a stored value the fixed list does not name (e.g. a #hex an assistant proposed) selectable instead of blanking it. */
function withCurrent(options: { value: string; label: string }[], current: string) {
  return options.some((o) => o.value === current) ? options : [...options, { value: current, label: current }];
}

interface CaptionStyleFieldsProps {
  config: VideoCompileStepConfigValue;
  patch: (partial: Partial<VideoCompileStepConfigValue>) => void;
}

/**
 * The burned-caption look: style, font (with a live sample of each), position, size and colours,
 * plus an approximate preview. Rendered inside the compile step's "Narration & captions" section
 * once captions are switched on. See docs/voiceover.md "Captions".
 */
export function CaptionStyleFields({ config, patch }: CaptionStyleFieldsProps) {
  const t = useT('wfSteps.captionStyle');
  const fd = useFd();
  const textColorOptions = TEXT_COLOR_VALUES.map((v) => ({ value: v, label: t(`textColor.${v}`) }));
  const boxColorOptions = BOX_COLOR_VALUES.map((v) => ({ value: v, label: t(`boxColor.${boxKey(v)}`) }));
  const style: CaptionStyle = config.captionStyle ?? 'Subtitle';
  const font: CaptionFont = config.captionFont ?? 'Auto';
  const autoSize = isAutoCaptionSize(config.captionFontSizePct);
  const fontColor = config.captionFontColor ?? 'white';
  const boxColor = config.captionBoxColor ?? 'black@0.6';
  const highlightColor = config.captionHighlightColor ?? 'yellow';

  return (
    <Stack gap="xs">
      <Select
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.captionStyle')}
        label={t('style.label')}
        size="xs"
        data={CAPTION_STYLE_OPTIONS.map((o) => ({ value: o.value, label: t(`style.${o.value}`) }))}
        value={style}
        onChange={(v) => patch({ captionStyle: (v as CaptionStyle) ?? 'Subtitle' })}
        allowDeselect={false}
      />

      <Box
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.captionFont').wrapperProps}>
        <Text size="xs" fw={500} id="caption-font-label">{t('font.label')}</Text>
        <Text size="xs" c="dimmed" mb={4}>{t('font.licence')}</Text>
        <SimpleGrid cols={{ base: 1, xs: 2 }} spacing={6} role="radiogroup" aria-labelledby="caption-font-label">
          {CAPTION_FONT_OPTIONS.map((option) => {
            const selected = option.value === font;
            // eslint-disable-next-line i18next/no-literal-string -- enum value / fallback, not copy
            const family = option.family ?? captionFontFamily('Auto', style);
            return (
              <UnstyledButton
                key={option.value}
                role="radio"
                aria-checked={selected}
                aria-label={`${t(`font.${option.value}.name`)}: ${t(`font.${option.value}.hint`)}`}
                className={classes.fontCard}
                onClick={() => patch({ captionFont: option.value })}
              >
                <span className={classes.sample} style={{ fontFamily: family }} data-testid={`caption-font-sample-${option.value}`}>
                  {t('preview.sample')}
                </span>
                <Text size="xs" c="dimmed" component="span" display="block">
                  {option.value === 'Auto' ? t('font.autoNote', { name: t('font.Auto.name'), font: AUTO_FONT_BY_STYLE[style], style: t(`styleName.${style}`) }) : t(`font.${option.value}.name`)} · {t(`font.${option.value}.hint`)}
                </Text>
              </UnstyledButton>
            );
          })}
        </SimpleGrid>
      </Box>

      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.captionPosition')}
          label={t('position.label')}
          description={t('position.description')}
          size="xs"
          data={CAPTION_POSITION_OPTIONS.map((o) => ({ value: o.value, label: t(`position.${o.value}`) }))}
          value={config.captionPosition ?? 'Auto'}
          onChange={(v) => patch({ captionPosition: (v as CaptionPosition) ?? 'Auto' })}
          allowDeselect={false}
        />
        <Stack gap={4}>
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.captionFontSizePct')}
            label={t('size.auto.label')}
            description={t('size.auto.description')}
            size="xs"
            checked={autoSize}
            onChange={(e) => patch({ captionFontSizePct: e.currentTarget.checked ? 0 : 6 })}
          />
          {!autoSize && (
            <NumberInput
              label={t('size.label')}
              size="xs"
              min={2}
              max={12}
              value={config.captionFontSizePct}
              onChange={(v) => patch({ captionFontSizePct: typeof v === 'number' ? v : 6 })}
            />
          )}
        </Stack>
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, sm: style === 'Karaoke' ? 3 : 2 }}>
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.captionFontColor')}
          label={t('colour.text')}
          size="xs"
          data={withCurrent(textColorOptions, fontColor)}
          value={fontColor}
          onChange={(v) => patch({ captionFontColor: v ?? 'white' })}
          allowDeselect={false}
        />
        {style === 'Karaoke' && (
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.captionHighlightColor')}
            label={t('colour.highlight')}
            size="xs"
            data={withCurrent(textColorOptions, highlightColor)}
            value={highlightColor}
            onChange={(v) => patch({ captionHighlightColor: v ?? 'yellow' })}
            allowDeselect={false}
          />
        )}
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.captionBoxColor')}
          label={t('colour.box')}
          description={style === 'Punch' || style === 'Bold' ? t('colour.noBox', { style: t(`styleName.${style}`) }) : undefined}
          size="xs"
          data={withCurrent(boxColorOptions, boxColor)}
          value={boxColor}
          onChange={(v) => patch({ captionBoxColor: v ?? 'black@0.6' })}
          allowDeselect={false}
          disabled={style === 'Punch' || style === 'Bold'}
        />
      </SimpleGrid>

      {style !== 'Punch' && (
        <NumberInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.captionMaxCharsPerCue')}
          label={t('maxChars.label')}
          description={t('maxChars.description')}
          size="xs"
          min={12}
          max={120}
          value={config.captionMaxCharsPerCue ?? 42}
          onChange={(v) => patch({ captionMaxCharsPerCue: typeof v === 'number' ? v : 42 })}
        />
      )}

      <CaptionPreview
        style={style}
        font={font}
        position={config.captionPosition ?? 'Auto'}
        vertical={config.outputFormat === 'Vertical' || config.outputFormat === 'Portrait'}
        fontColor={fontColor}
        boxColor={boxColor}
        highlightColor={highlightColor}
      />
    </Stack>
  );
}

interface CaptionPreviewProps {
  style: CaptionStyle;
  font: CaptionFont;
  position: CaptionPosition;
  vertical: boolean;
  fontColor: string;
  boxColor: string;
  highlightColor: string;
}

/** An approximate, static rendering of the chosen look on a stand-in frame — not the encoder's output. */
export function CaptionPreview({ style, font, position, vertical, fontColor, boxColor, highlightColor }: CaptionPreviewProps) {
  const t2 = useT('wfSteps.captionStyle.preview');
  const effectivePosition = position === 'Auto' ? (style === 'Punch' ? 'Center' : 'Bottom') : position;
  const text = captionColorCss(fontColor) ?? 'white';
  const highlight = captionColorCss(highlightColor) ?? 'yellow';
  const box = style === 'Punch' || style === 'Bold' ? null : captionColorCss(boxColor);
  const width = vertical ? 135 : 320;
  const height = vertical ? 240 : 180;
  // Mirrors CaptionLayout's automatic sizes (% of the short side) and title-safe margins.
  const shortSide = Math.min(width, height);
  const sizePct = style === 'Punch' ? (vertical ? 13 : 11) : style === 'Bold' ? (vertical ? 14 : 10.5)
    : style === 'Karaoke' ? (vertical ? 8 : 6.5) : (vertical ? 6.5 : 5.5);
  const fontSize = Math.max(8, Math.round((shortSide * sizePct) / 100));
  const outline = box ? undefined : `0 0 3px rgba(0,0,0,0.9), 0 0 1px rgba(0,0,0,1), 1px 1px 2px rgba(0,0,0,0.6)`;
  const placement = effectivePosition === 'Top'
    ? { top: `${vertical ? 12 : 8}%` }
    : effectivePosition === 'Center'
      ? { top: '50%', transform: 'translateY(-50%)' }
      : { bottom: `${style === 'Bold' ? (vertical ? 28 : 10) : vertical ? 20 : 8}%` };

  return (
    <Box>
      <Text size="xs" c="dimmed" mb={4}>{t2('title')}</Text>
      <Box className={classes.preview} style={{ width, height }} data-testid="caption-preview" data-position={effectivePosition}>
        <div className={classes.previewCaption} style={{ ...placement, fontFamily: captionFontFamily(font, style), fontSize, color: text }}>
          <span className={classes.previewText} style={{ background: box ?? undefined, textShadow: outline }}>
            {style === 'Punch' ? t2('ship') : style === 'Bold' ? t2('shipIt') : style === 'Karaoke'
              ? (<><span style={{ color: highlight }}>{t2('shipIt')}</span> {t2('today')}</>)
              : t2('shipIt')}
          </span>
        </div>
      </Box>
    </Box>
  );
}
