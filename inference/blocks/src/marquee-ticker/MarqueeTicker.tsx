// ReelBolt block "marquee-ticker" (MIT). A news-style band whose items scroll left inside the safe area.
import React from 'react';
import {AbsoluteFill, useCurrentFrame, useVideoConfig} from 'remotion';

export type MarqueeTickerProps = {
  /** Items scrolled in a loop. Keep each short enough to fit one slot (about 25 characters). */
  items?: string[];
  /** Band background. Default '#facc15'. */
  backgroundColor?: string;
  color?: string;
  fontSize?: number;
  /** CSS font-family; must be an installed family. Default 'Bebas Neue, sans-serif'. */
  fontFamily?: string;
  /** Scroll speed in px per frame. Default 8. */
  speed?: number;
  /** How many slots fit across the safe area. Default 2. */
  itemsPerView?: number;
  /** 'bottom' (inside the safe area) or 'middle'. Default 'bottom'. */
  position?: 'bottom' | 'middle';
};

const PAD = 24; // inner padding of a slot; items fade inside this zone at the window edges

export const MarqueeTicker: React.FC<MarqueeTickerProps> = ({
  items = ['• NEW RENDER ENGINE', '• FASTER EXPORTS', '• FRESH TEMPLATES WEEKLY'],
  backgroundColor = '#facc15',
  color = '#111827',
  fontSize = 56,
  fontFamily = 'Bebas Neue, sans-serif',
  speed = 8,
  itemsPerView = 2,
  position = 'bottom',
}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const safe = width * 0.05;
  const perView = Math.min(12, Math.max(1, Math.round(Number.isFinite(itemsPerView) ? itemsPerView : 2)));
  const slot = (width - 2 * safe) / perView;
  // The ring holds at least one more slot than the window shows, so every slot can slide in from the right.
  const count = Math.max(items.length, perView + 1);
  const ring = count * slot;
  const offset = (frame * speed) % ring;
  return (
    <AbsoluteFill style={{justifyContent: position === 'bottom' ? 'flex-end' : 'center', paddingBottom: position === 'bottom' ? height * 0.07 : 0}}>
      <div style={{position: 'relative', height: fontSize * 1.5, background: backgroundColor, color, fontFamily, fontSize, letterSpacing: 2}}>
        {Array.from({length: count}, (_, i) => {
          let x = (((i * slot - offset) % ring) + ring) % ring;
          if (x > ring - slot) x -= ring;
          // Fully visible inside the window; fades out over the slot padding at either edge.
          const edge = Math.min(x, (width - 2 * safe) - (x + slot));
          const opacity = Math.max(0, Math.min(1, (edge + PAD) / PAD));
          return (
            <div
              key={i}
              style={{
                position: 'absolute',
                left: safe + x,
                width: slot,
                top: 0,
                bottom: 0,
                display: 'flex',
                alignItems: 'center',
                padding: `0 ${PAD}px`,
                boxSizing: 'border-box',
                whiteSpace: 'nowrap',
                opacity,
              }}
            >
              {items[i % items.length]}
            </div>
          );
        })}
      </div>
    </AbsoluteFill>
  );
};
