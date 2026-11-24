'use client';

/**
 * control-shape-restyle T-001: the dev-only preview for the three control shapes.
 *
 * Follows `web/app/spike/page.tsx`: a top-level route, not linked from the product, that calls
 * `notFound()` in a production build. The guard test is `./control-shapes-guard.test.tsx`; the
 * isolation test is `./control-shapes-isolation.test.ts`.
 *
 * The page is the evidence surface, not a mock: every control is a real Mantine `Slider` or
 * `Switch`, the theme toggle drives Mantine's own colour scheme, and the captions read the
 * `--rb-control-*` values from the computed style of the section that owns them.
 */

import { useEffect, useRef, useState } from 'react';
import {
  RangeSlider,
  Slider,
  Switch,
  useComputedColorScheme,
  useMantineColorScheme,
} from '@mantine/core';
import { notFound } from 'next/navigation';
import styles from './control-shapes.module.css';

type Scheme = 'light' | 'dark';
type MetricKind = 'slider' | 'switch';

const SLIDER_TOKENS = [
  ['Slant', '--rb-control-slant'],
  ['Track height', '--rb-control-track-h'],
  ['Track radius', '--rb-control-track-radius'],
  ['Thumb size', '--rb-control-thumb-size'],
  ['Thumb radius', '--rb-control-thumb-radius'],
  ['Hit box', '--rb-control-hit'],
] as const;

const SWITCH_TOKENS = [
  ['Slant', '--rb-control-slant'],
  ['Switch width', '--rb-control-switch-w'],
  ['Switch height', '--rb-control-switch-h'],
  ['Switch radius', '--rb-control-switch-radius'],
  ['Thumb size', '--rb-control-switch-thumb'],
  ['Hit box', '--rb-control-hit'],
] as const;

const VARIANTS = [
  {
    id: 'a',
    title: 'A. Soft slant',
    blurb:
      'Eight degrees and a mid radius. The ends lean, the body stays calm, and there is no glow. The conservative option.',
  },
  {
    id: 'b',
    title: 'B. Blade',
    blurb:
      'Fifteen degrees, tight corners, a one pixel rim, and a faint glow only while the thumb is active. The cyberpunk option, held back by geometry rather than saturation.',
  },
  {
    id: 'c',
    title: 'C. Capsule cut',
    blurb:
      'A large radius with a five degree lean, so only the ends read as cut. The quietest option.',
  },
] as const;

function tokensFor(kind: MetricKind) {
  return kind === 'slider' ? SLIDER_TOKENS : SWITCH_TOKENS;
}

/** Reads the shape tokens from the computed style, so the caption is the measurement. */
function ShapeMetrics({ kind, scheme }: { kind: MetricKind; scheme: Scheme }) {
  const ref = useRef<HTMLDListElement>(null);
  const [values, setValues] = useState<Record<string, string>>({});

  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const computed = getComputedStyle(element);
    const next: Record<string, string> = {};
    for (const [, token] of tokensFor(kind)) {
      next[token] = computed.getPropertyValue(token).trim();
    }
    setValues(next);
  }, [kind, scheme]);

  return (
    <dl ref={ref} className={styles.metrics} aria-label={`${kind} measured shape values`}>
      {tokensFor(kind).map(([label, token]) => (
        <div className={styles.metricRow} key={token}>
          <dt>{label}</dt>
          <dd>{values[token] || 'measuring'}</dd>
        </div>
      ))}
    </dl>
  );
}

function SliderRow({
  name,
  initial,
  disabled = false,
  dense = false,
}: {
  name: string;
  initial: number;
  disabled?: boolean;
  dense?: boolean;
}) {
  const [value, setValue] = useState(initial);
  return (
    <div className={styles.control}>
      <span className={styles.controlLabel}>
        {name} <span className={styles.controlValue}>{value} %</span>
      </span>
      <Slider
        value={value}
        onChange={setValue}
        disabled={disabled}
        size={dense ? 'xs' : undefined}
        thumbLabel={name}
        label={(current) => `${current} percent`}
      />
    </div>
  );
}

/** Two thumbs share the same track; both are children of the skewed carrier. */
function RangeRow() {
  const [value, setValue] = useState<[number, number]>([25, 75]);
  return (
    <div className={styles.control}>
      <span className={styles.controlLabel}>
        Range{' '}
        <span className={styles.controlValue}>
          {value[0]} % to {value[1]} %
        </span>
      </span>
      <RangeSlider
        value={value}
        onChange={setValue}
        thumbFromLabel="Range start"
        thumbToLabel="Range end"
      />
    </div>
  );
}

function SwitchRow({
  label,
  initial,
  disabled = false,
}: {
  label: string;
  initial: boolean;
  disabled?: boolean;
}) {
  const [checked, setChecked] = useState(initial);
  return (
    <div className={styles.control}>
      <Switch
        checked={checked}
        onChange={(event) => setChecked(event.currentTarget.checked)}
        label={label}
        disabled={disabled}
      />
    </div>
  );
}

function ShortTrackRow() {
  const [narrow, setNarrow] = useState(100);
  const [wide, setWide] = useState(100);
  return (
    <div className={styles.shortRow}>
      <div className={styles.shortCol}>
        <span className={styles.controlLabel}>Track in a 120 px container</span>
        <div className={styles.narrow}>
          <Slider
            value={narrow}
            onChange={setNarrow}
            thumbLabel="Track in a 120 px container"
          />
        </div>
      </div>
      <div className={styles.shortCol}>
        <span className={styles.controlLabel}>Track in a wide container</span>
        <div className={styles.wide}>
          <Slider value={wide} onChange={setWide} thumbLabel="Track in a wide container" />
        </div>
      </div>
    </div>
  );
}

/** One measured block per variant: the values are read from the section, not retyped. */
function VariantMetrics({ scheme }: { scheme: Scheme }) {
  return (
    <div className={styles.variantMetrics}>
      <h3 className={styles.metricsHeading}>Measured shape values</h3>
      <div className={styles.metricColumns}>
        <div className={styles.metricGroup}>
          <span className={styles.metricGroupLabel}>Slider</span>
          <ShapeMetrics kind="slider" scheme={scheme} />
        </div>
        <div className={styles.metricGroup}>
          <span className={styles.metricGroupLabel}>Switch</span>
          <ShapeMetrics kind="switch" scheme={scheme} />
        </div>
      </div>
    </div>
  );
}

/** The shape the app actually ships: no variant attribute, so it inherits global.css + theme.ts. */
function AppliedSection({ scheme }: { scheme: Scheme }) {
  return (
    <section
      className={`${styles.section} touch-targets`}
      aria-labelledby="applied"
      data-testid="applied-section"
    >
      <h2 id="applied">Applied in the app (no variant override)</h2>
      <p className={styles.blurb}>
        The default every product slider and switch inherits. Same tokens, theme overrides and global
        rules; the only difference from B is that nothing in this section overrides the variant.
      </p>
      <div className={styles.grid}>
        <SliderRow name="Middle position" initial={50} />
        <RangeRow />
        <SwitchRow label="Switch off" initial={false} />
        <SwitchRow label="Switch on" initial />
        <SliderRow name="Disabled position" initial={60} disabled />
        <SliderRow name="Dense position (size xs)" initial={40} dense />
        <SwitchRow label="Disabled switch" initial={false} disabled />
      </div>
      <VariantMetrics scheme={scheme} />
    </section>
  );
}

function ControlShapesPreview() {
  const { setColorScheme } = useMantineColorScheme();
  const scheme = useComputedColorScheme('light', { getInitialValueInEffect: true });

  // Mantine owns the attribute; this keeps it in step with the resolved scheme even while the
  // scheme is still 'auto', and makes the toggle's effect explicit for the preview.
  useEffect(() => {
    document.documentElement.dataset.mantineColorScheme = scheme;
  }, [scheme]);

  const choose = (next: Scheme) => {
    setColorScheme(next);
  };

  return (
    <main className={styles.page}>
      <header className={styles.pageHeader}>
        <h1>Control shapes preview</h1>
        <p className={styles.intro}>
          A design preview, not linked from the product. Every control below is a real Mantine
          slider or switch. Switch the theme, then compare the three shapes.
        </p>
        <div className={styles.toggle} role="group" aria-label="Colour scheme">
          {(['light', 'dark'] as const).map((option) => (
            <button
              key={option}
              type="button"
              aria-pressed={scheme === option}
              className={styles.toggleButton}
              onClick={() => choose(option)}
            >
              {option === 'light' ? 'Light' : 'Dark'}
            </button>
          ))}
        </div>
      </header>

      {VARIANTS.map((variant) => (
        <section
          key={variant.id}
          className={styles.section}
          data-control-variant={variant.id}
          aria-labelledby={`variant-${variant.id}`}
        >
          <h2 id={`variant-${variant.id}`}>{variant.title}</h2>
          <p className={styles.blurb}>{variant.blurb}</p>

          <div className={styles.grid}>
            <SliderRow name="Start position" initial={25} />
            <SliderRow name="Middle position" initial={50} />
            <SliderRow name="End position" initial={100} />
            <SliderRow name="Zero position" initial={0} />
            <SwitchRow label="Switch off" initial={false} />
            <SwitchRow label="Switch on" initial />
            <SliderRow name="Disabled position" initial={60} disabled />
            <SwitchRow label="Disabled switch" initial={false} disabled />
            <RangeRow />
          </div>

          <ShortTrackRow />
          <VariantMetrics scheme={scheme} />
        </section>
      ))}

      <AppliedSection scheme={scheme} />
    </main>
  );
}

export default function ControlShapesPage() {
  if (process.env.NODE_ENV === 'production') {
    notFound();
  }

  return <ControlShapesPreview />;
}
