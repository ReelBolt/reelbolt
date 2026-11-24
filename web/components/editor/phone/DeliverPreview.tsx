'use client';

import React from 'react';
import { IconMovie } from '@tabler/icons-react';
import { useT } from '@/i18n';

/**
 * What the viewer slot shows while the Export sheet is open: a light stand-in, so the WebGL compositor is not
 * running behind a sheet that hides it. Opening the editor again brings the real picture back.
 */
export function DeliverPreview({ name }: { name: string }) {
  const t = useT('editor.phone.deliverPreview');
  return (
    <div
      data-testid="deliver-light-preview"
      style={{ width: '100%', height: '100%', background: 'var(--rb-viewer-bg, #0b0b0d)', color: '#e8e8ec', display: 'flex', alignItems: 'center', justifyContent: 'center', flexDirection: 'column', gap: 6, padding: 12, textAlign: 'center' }}
    >
      <IconMovie size={28} aria-hidden />
      <span style={{ fontSize: 14, fontWeight: 600 }}>{name}</span>
      <span style={{ fontSize: 12 }}>{t('previewIsPausedWhile')}</span>
    </div>
  );
}
