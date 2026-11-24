import { ImageResponse } from 'next/og';
import type { Locale } from '@/i18n/config';
import { seoStrings } from '@/lib/seo-strings';

export const OG_SIZE = { width: 1200, height: 630 };

export function renderOgCard(locale: Locale) {
  const text = seoStrings(locale);
  return new ImageResponse(
    (
      <div
        style={{
          width: '100%',
          height: '100%',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'flex-start',
          justifyContent: 'center',
          background: 'linear-gradient(135deg, #2E1065 0%, #6D28D9 55%, #A855F7 100%)',
          padding: '80px',
          fontFamily: 'sans-serif',
        }}
      >
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '20px',
          }}
        >
          <svg width="84" height="84" viewBox="0 0 120 120" fill="none">
            <rect width="120" height="120" rx="28" fill="#2E1065" />
            <path d="M36 96V24H62a21 21 0 0 1 0 42H36" stroke="#C4B5FD" strokeWidth="14" strokeLinejoin="round" />
            <path d="M68 12 L28 68 H54 L46 108 L92 48 H64 Z" fill="#A855F7" stroke="#2E1065" strokeWidth="5" strokeLinejoin="round" transform="translate(66 80) scale(-.68 .68) translate(-60 -60)" />
          </svg>
          <div style={{ display: 'flex', fontSize: 56, fontWeight: 700, color: 'white' }}>
            ReelBolt
          </div>
        </div>
        <div
          style={{
            display: 'flex',
            marginTop: '48px',
            fontSize: 40,
            fontWeight: 600,
            color: 'white',
            maxWidth: '920px',
            lineHeight: 1.25,
          }}
        >
          {text.ogTitle}
        </div>
        <div
          style={{
            display: 'flex',
            marginTop: '28px',
            fontSize: 26,
            color: 'rgba(255,255,255,0.85)',
          }}
        >
          {text.ogSubtitle}
        </div>
      </div>
    ),
    { ...OG_SIZE }
  );
}
