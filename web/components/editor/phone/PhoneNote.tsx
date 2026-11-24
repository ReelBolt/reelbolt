'use client';

import React, { useState } from 'react';
import { ActionIcon, Button } from '@mantine/core';
import { IconX } from '@tabler/icons-react';
import classes from './phone.module.css';
import { useT } from '@/i18n';

export const PHONE_NOTE_DISMISS_KEY = 'reelbolt:editor:phone-note-dismissed';

function dismissedBefore(): boolean {
  try {
    return window.sessionStorage.getItem(PHONE_NOTE_DISMISS_KEY) === '1';
  } catch {
    return false;
  }
}

export interface PhoneNoteProps {
  /** The address to copy (defaults to the current page). */
  href?: string;
}

/**
 * The escape hatch's note (AC 11): this phone opened the full editor, so it gets the phone surface and
 * is told so, with a way to copy the link for a bigger screen.
 */
export function PhoneNote({ href }: PhoneNoteProps) {
  const t = useT('editor.phone.phoneNote');
  const [dismissed, setDismissed] = useState(dismissedBefore);
  const [copied, setCopied] = useState<'idle' | 'done' | 'failed'>('idle');
  if (dismissed) return null;
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(href ?? window.location.href);
      setCopied('done');
    } catch {
      setCopied('failed');
    }
  };
  return (
    <div className={classes.note} role="note" data-testid="phone-note">
      <span className={classes.noteText}>{copied === 'failed' ? t('couldNotCopySelect') : t('phoneViewSomeTools')}</span>
      <Button variant="default" h={44} px={10} onClick={() => void copy()} data-testid="phone-note-copy">
        {copied === 'done' ? t('linkCopied') : t('copyLink')}
      </Button>
      <ActionIcon
        variant="subtle"
        color="gray"
        w={44}
        h={44}
        miw={44}
        mih={44}
        aria-label={t('dismissThisNote')}
        onClick={() => {
          try {
            window.sessionStorage.setItem(PHONE_NOTE_DISMISS_KEY, '1');
          } catch {
            // Dismissed for this page only.
          }
          setDismissed(true);
        }}
        data-testid="phone-note-dismiss"
      >
        <IconX size={18} aria-hidden />
      </ActionIcon>
    </div>
  );
}
