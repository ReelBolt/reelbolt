'use client';

import React, { useRef, useState } from 'react';
import { Badge, Button, Checkbox, Group, Modal, Stack, Text } from '@mantine/core';
import { useT } from '@/i18n';
import { progressPercent } from '@/lib/editing/enhance/types';

/** A small status chip for a clip ("Enhancing" / "Needs your OK"), self-contained so background activity can adopt it later. */
export function EnhanceStatusChip({ kind, progress }: { kind: 'running' | 'needs-ok'; progress?: number | null }) {
  const t = useT('editor-enhance');
  const shown = progressPercent(progress);
  const pct = shown === null ? '' : ` ${shown}%`;
  return (
    <Badge size="sm" fz="sm" variant="light" color={kind === 'running' ? 'blue' : 'orange'} data-testid="enhance-chip" data-kind={kind} style={{ textTransform: 'none', minHeight: 20 }}>
      {kind === 'running' ? `${t('statusChip')}${pct}` : t('readyChip')}
    </Badge>
  );
}

/** Sending a video's picture to an outside service needs an explicit yes (saved for the project on the server). */
export function EnhanceConsentDialog({ opened, busy, failed, onCancel, onConfirm }: { opened: boolean; busy: boolean; failed: boolean; onCancel: () => void; onConfirm: () => void }) {
  return opened ? <OpenConsent busy={busy} failed={failed} onCancel={onCancel} onConfirm={onConfirm} /> : null;
}

function OpenConsent({ busy, failed, onCancel, onConfirm }: { busy: boolean; failed: boolean; onCancel: () => void; onConfirm: () => void }) {
  const t = useT('editor-enhance.consent');
  // Mounted only while open, so the box always starts unticked.
  const [agreed, setAgreed] = useState(false);
  return (
    <Modal opened onClose={onCancel} title={t('title')} centered trapFocus returnFocus data-testid="enhance-consent-dialog">
      <Stack gap="sm">
        <Text size="sm">{t('body')}</Text>
        <Checkbox size="sm" label={t('check')} checked={agreed} onChange={(e) => setAgreed(e.currentTarget.checked)} data-autofocus data-testid="enhance-consent-check" />
        {failed && (
          <Text size="xs" c="red" role="alert" data-testid="enhance-consent-error">
            {t('failed')}
          </Text>
        )}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" mih={44} onClick={onCancel} data-testid="enhance-consent-cancel">
            {t('cancel')}
          </Button>
          <Button mih={44} disabled={!agreed} loading={busy} onClick={onConfirm} data-testid="enhance-consent-confirm">
            {t('confirm')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Two synced players over the clip's own part of the source: before (left) and after (right). */
export function EnhanceCompare({ beforeSrc, afterSrc, startSec, lengthSec }: { beforeSrc: string; afterSrc: string; startSec: number; lengthSec: number }) {
  const t = useT('editor-enhance');
  const a = useRef<HTMLVideoElement>(null);
  const b = useRef<HTMLVideoElement>(null);
  const [playing, setPlaying] = useState(false);
  const end = startSec + lengthSec;
  const frag = `#t=${startSec.toFixed(2)},${end.toFixed(2)}`;
  const toggle = () => {
    const va = a.current;
    const vb = b.current;
    if (!va || !vb) return;
    if (playing) {
      va.pause();
      vb.pause();
      setPlaying(false);
      return;
    }
    if (va.currentTime < startSec || va.currentTime >= end - 0.05) va.currentTime = startSec;
    vb.currentTime = va.currentTime;
    void Promise.allSettled([va.play(), vb.play()]);
    setPlaying(true);
  };
  const onTime = () => {
    const va = a.current;
    const vb = b.current;
    if (!va || !vb) return;
    if (va.currentTime >= end) {
      va.currentTime = startSec;
      vb.currentTime = startSec;
      return;
    }
    if (Math.abs(vb.currentTime - va.currentTime) > 0.15) vb.currentTime = va.currentTime;
  };
  return (
    <Stack gap={6} data-testid="enhance-compare">
      <Text size="xs" fw={600}>
        {t('compareTitle')}
      </Text>
      <Group gap={6} grow align="flex-start" wrap="nowrap">
        <Stack gap={2}>
          <video ref={a} src={`${beforeSrc}${frag}`} muted playsInline preload="metadata" onTimeUpdate={onTime} style={{ width: '100%', borderRadius: 4, background: '#000' }} data-testid="enhance-before" />
          <Text size="xs">{t('before')}</Text>
        </Stack>
        <Stack gap={2}>
          <video ref={b} src={`${afterSrc}${frag}`} muted playsInline preload="metadata" style={{ width: '100%', borderRadius: 4, background: '#000' }} data-testid="enhance-after" />
          <Text size="xs">{t('after')}</Text>
        </Stack>
      </Group>
      <Button variant="default" mih={44} onClick={toggle} data-testid="enhance-play">
        {playing ? t('pause') : t('play')}
      </Button>
    </Stack>
  );
}
