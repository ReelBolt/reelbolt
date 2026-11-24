'use client';

import { useState } from 'react';
import styles from './contact-sheet.module.css';

export interface CursorSpec {
  name: string;
  group: 'ui' | 'editor';
  file: string;
  at2x: string;
  hotspot: { x: number; y: number };
  fallback: string;
  intent: string;
}

export interface CursorCatalog {
  version: number;
  sizePx: number;
  palette: { paper: string; rim: string; violet: string; lavender: string };
  cursors: CursorSpec[];
}

/** The dashboard runs under the '/app' basePath (web/next.config.ts). */
const CURSOR_URL = '/app/cursors';

const BACKGROUNDS = [
  { id: 'white', name: 'White', color: '#FFFFFF', ink: '#3A3450' },
  { id: 'deep', name: 'Deep #2E1065', color: '#2E1065', ink: '#D7CBF0' },
  // A dark ink on mid-grey: white at 12 px/600 is 3.95:1, below WCAG AA. #0F0C17 is 4.92:1.
  { id: 'grey', name: 'Grey #808080', color: '#808080', ink: '#0F0C17' },
] as const;

const DETAIL = 64;

function Hotspot({ x, y, box }: { x: number; y: number; box: number }) {
  const left = (x / 32) * box;
  const top = (y / 32) * box;
  return (
    <span
      aria-hidden="true"
      className={styles.hotspot}
      style={{ left, top }}
      data-testid="hotspot"
    />
  );
}

function CursorCard({ cursor }: { cursor: CursorSpec }) {
  return (
    <article className={styles.card}>
      <div className={styles.cardHead}>
        <h3 className={styles.name}>{cursor.name}</h3>
        <code
          className={styles.hotspotCode}
          title="Hotspot in 32 px space"
        >{`${cursor.hotspot.x},${cursor.hotspot.y}`}</code>
      </div>
      <p className={styles.intent}>{cursor.intent}</p>

      <div className={styles.swatches}>
        {BACKGROUNDS.map((bg) => (
          <div
            key={bg.id}
            className={styles.swatch}
            style={{ background: bg.color }}
          >
            <span className={styles.swatchName} style={{ color: bg.ink }}>
              {bg.name}
            </span>
            <span className={styles.cursorBox}>
              <span className={styles.stage} style={{ width: 32, height: 32 }}>
                {/* eslint-disable-next-line @next/next/no-img-element -- real-size raster test swatch */}
                <img
                  src={`${CURSOR_URL}/${cursor.file}`}
                  srcSet={`${CURSOR_URL}/${cursor.file} 1x, ${CURSOR_URL}/${cursor.at2x} 2x`}
                  width={32}
                  height={32}
                  alt=""
                />
                <Hotspot x={cursor.hotspot.x} y={cursor.hotspot.y} box={32} />
              </span>
            </span>
          </div>
        ))}
      </div>

      <div className={styles.detailRow}>
        <span className={styles.detailLabel}>2× detail</span>
        <span className={styles.detailStage} style={{ width: DETAIL, height: DETAIL }}>
          {/* eslint-disable-next-line @next/next/no-img-element -- 2x raster for inspection */}
          <img
            src={`${CURSOR_URL}/${cursor.at2x}`}
            width={DETAIL}
            height={DETAIL}
            alt=""
          />
          <Hotspot x={cursor.hotspot.x} y={cursor.hotspot.y} box={DETAIL} />
        </span>
        <span className={styles.fallback}>
          fallback <code>{cursor.fallback}</code>
        </span>
      </div>
    </article>
  );
}

export function CursorContactSheet({ catalog }: { catalog: CursorCatalog }) {
  const [theme, setTheme] = useState<'light' | 'dark'>('light');

  const ui = catalog.cursors.filter((c) => c.group === 'ui');
  const editor = catalog.cursors.filter((c) => c.group === 'editor');

  return (
    <main className={styles.sheet} data-theme={theme}>
      <header className={styles.header}>
        <p className={styles.kicker}>ReelBolt · design proposal</p>
        <h1 className={styles.title}>Custom cursors</h1>
        <p className={styles.lede}>
          Every cursor in the set, at real size (32 px), on the three backgrounds it has to survive.
          This page is a review artefact: the dashboard and the marketing site use these, and
          nothing links here.
        </p>

        <div className={styles.toolbar}>
          <div className={styles.themeSwitch} role="group" aria-label="Page theme">
            <button
              type="button"
              className={styles.themeButton}
              aria-pressed={theme === 'light'}
              onClick={() => setTheme('light')}
            >
              Light
            </button>
            <button
              type="button"
              className={styles.themeButton}
              aria-pressed={theme === 'dark'}
              onClick={() => setTheme('dark')}
            >
              Dark
            </button>
          </div>

          <ul className={styles.palette} aria-label="Cursor palette">
            {Object.entries(catalog.palette).map(([name, hex]) => (
              <li key={name} className={styles.paletteItem}>
                <span className={styles.chip} style={{ background: hex }} aria-hidden="true" />
                <span className={styles.paletteName}>{name}</span>
                <code className={styles.paletteHex}>{hex}</code>
              </li>
            ))}
          </ul>
        </div>

        <p className={styles.legend}>
          <span className={styles.legendDot} aria-hidden="true" />
          The ring marks the hotspot: the point of action, never the image centre. Every shipped
          rule degrades to its standard keyword if the image fails to load.
        </p>
      </header>

      <section className={styles.section}>
        <h2 className={styles.sectionTitle}>
          UI cursors <span className={styles.count}>{ui.length}</span>
        </h2>
        <div className={styles.grid}>
          {ui.map((c) => (
            <CursorCard key={c.name} cursor={c} />
          ))}
        </div>
      </section>

      <section className={styles.section}>
        <h2 className={styles.sectionTitle}>
          Editor cursors <span className={styles.count}>{editor.length}</span>
        </h2>
        <div className={styles.grid}>
          {editor.map((c) => (
            <CursorCard key={c.name} cursor={c} />
          ))}
        </div>
      </section>

      <footer className={styles.footer}>
        {catalog.cursors.length} cursors · brand masters in <code>/brand/cursors</code> · catalog
        synced from <code>brand/cursors/cursors.json</code>.
      </footer>
    </main>
  );
}
