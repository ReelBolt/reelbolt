'use client';

import React, { useState } from 'react';
import Link from 'next/link';
import { ActionIcon, Badge, Button, Menu, Modal, ScrollArea, Stack, Text } from '@mantine/core';
import { IconArrowBackUp, IconCheck, IconArrowForwardUp, IconChevronLeft, IconCloudOff, IconDots, IconHandMove, IconHistory, IconKeyboard, IconLink, IconMessageCircle, IconWand } from '@tabler/icons-react';
import type { SaveState } from '@/lib/editing/sync';
import { keyboardOnlyActions } from '@/lib/editing/keymap-touch';
import { keyActionLabelMsg } from '@/lib/editing/labels-msg';
import { saveStateText } from '../shell/save-state-text';
import { RETRYABLE } from '../shell/StatusBar';
import classes from './phone.module.css';
import { useT } from '@/i18n';
import { ActivityIndicator } from '@/components/activity';

/** The sheets the overflow menu can open (the dock opens the other five). */
export type OverflowSheet = 'assist' | 'review' | 'history';

export interface PhoneHeaderProps {
  name: string;
  save: SaveState;
  backHref: string;
  canUndo: boolean;
  canRedo: boolean;
  onUndo: () => void;
  onRedo: () => void;
  onExport: () => void;
  /** A render finished while the Export sheet was closed: a dot with the word on the Export button. */
  exportBadge?: boolean;
  onOpenSheet: (sheet: OverflowSheet) => void;
  /** The timeline's phone review page (comments on the latest render). */
  reviewHref: string;
  touchDrag: boolean;
  onToggleTouchDrag: () => void;
  /** Opens the platform assistant (a full-height sheet on a phone). Absent: the entry is not offered. */
  onAskAssistant?: () => void;
  /** Opens the AI tools drawer. Absent: the entry is not offered. */
  onOpenAiTools?: () => void;
  onRetry?: () => void;
  onSignIn?: () => void;
  /** Landscape phone: a 36 px bar (the controls keep their 44 px hit areas). */
  landscape?: boolean;
}

interface ControlProps {
  label: string;
  children: React.ReactNode;
  onClick?: () => void;
  disabled?: boolean;
  'aria-haspopup'?: 'menu';
  'data-testid'?: string;
}

const CONTROL_PROPS = { variant: 'subtle', color: 'gray', w: 44, h: 44, miw: 44, mih: 44 } as const;

function Control({ label, children, ...rest }: ControlProps) {
  return (
    <ActionIcon {...CONTROL_PROPS} aria-label={label} {...rest}>
      {children}
    </ActionIcon>
  );
}

/**
 * The phone header (44 px): back, the timeline's name with its save state, undo, redo, Export and an
 * overflow menu (Assist, Review, History, the touch-drag experiment). The offline chip lives here so it is on
 * screen in every state; the "Early preview" label is on the phone's status line (PhoneEditor), so the name and
 * save state keep their room.
 */
export function PhoneHeader({ name, save, backHref, canUndo, canRedo, onUndo, onRedo, onExport, exportBadge = false, onOpenSheet, reviewHref, touchDrag, onToggleTouchDrag, onAskAssistant, onOpenAiTools, onRetry, onSignIn, landscape = false }: PhoneHeaderProps) {
  const t = useT('editor.phone.phoneHeader');
  const tA = useT('editor-ai-tools');
  const [linkCopied, setLinkCopied] = useState<'idle' | 'done' | 'failed'>('idle');
  // T-007: the read-only keyboard-only list (no phone behaviour changes).
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const copyLink = async () => {
    try {
      await navigator.clipboard.writeText(window.location.href);
      setLinkCopied('done');
    } catch {
      setLinkCopied('failed');
    }
  };
  const saveText = saveStateText(t, save);
  const offline = save.status === 'offline' || save.status === 'reconnecting';
  const canRetry = onRetry && RETRYABLE.has(save.status) && save.problem !== 'deleted' && save.problem !== 'noPermission';
  const canSignIn = onSignIn && save.status === 'authExpired';
  return (
    <header className={classes.header} data-landscape={landscape ? 'true' : undefined} data-testid="phone-header">
      <ActionIcon {...CONTROL_PROPS} component={Link} href={backHref} aria-label={t('backToAllTimelines')} data-testid="phone-back">
        <IconChevronLeft size={22} aria-hidden />
      </ActionIcon>
      <div className={classes.headerName}>
        <span className={classes.headerTitle} title={name}>
          {name}
        </span>
        <span className={classes.headerSub}>
          <span className={classes.saveText} data-testid="save-state-chip" data-status={save.status} title={saveText}>
            {save.status === 'saved' && !save.viewOnly ? t('saved') : saveText}
          </span>
        </span>
      </div>
      {offline && (
        <Badge component="span" color="orange" variant="light" size="md" fz="xs" tt="none" leftSection={<IconCloudOff size={12} aria-hidden />} data-testid="phone-offline-chip">
          {t('offline')}
        </Badge>
      )}
      {canRetry && (
        <Button variant="default" h={44} px={10} onClick={onRetry} data-testid="phone-retry">
          {t('retry')}
        </Button>
      )}
      {canSignIn && (
        <Button variant="default" h={44} px={10} onClick={onSignIn} data-testid="phone-sign-in">
          {t('signIn')}
        </Button>
      )}
      <Control label={t('undo')} onClick={onUndo} disabled={!canUndo} data-testid="phone-undo">
        <IconArrowBackUp size={20} aria-hidden />
      </Control>
      <Control label={t('redo')} onClick={onRedo} disabled={!canRedo} data-testid="phone-redo">
        <IconArrowForwardUp size={20} aria-hidden />
      </Control>
      <ActivityIndicator />
      <Button h={44} miw={44} px={exportBadge ? 8 : 8} style={{ flexShrink: 0 }} onClick={onExport} data-testid="phone-export">
        {t('export')}
        {exportBadge && (
          <Badge component="span" color="green" variant="filled" size="sm" fz={12} ml={6} tt="none" leftSection={<IconCheck size={12} aria-hidden />} data-testid="phone-export-badge">
            {t('ready')}
          </Badge>
        )}
      </Button>
      <Menu position="bottom-end" withinPortal zIndex={300} width={280}>
        <Menu.Target>
          <Control label={t('more')} aria-haspopup="menu" data-testid="phone-more">
            <IconDots size={22} aria-hidden />
          </Control>
        </Menu.Target>
        <Menu.Dropdown>
          {onOpenAiTools ? (
            <Menu.Item mih={44} leftSection={<IconWand size={16} />} onClick={onOpenAiTools} data-testid="phone-more-ai-tools">
              {tA('launcher.button')}
            </Menu.Item>
          ) : null}
          <Menu.Item mih={44} leftSection={<IconWand size={16} />} onClick={() => onOpenSheet('assist')} data-testid="phone-more-assist">
            {t('assistant')}
          </Menu.Item>
          {onAskAssistant ? (
            <Menu.Item mih={44} leftSection={<IconWand size={16} />} onClick={onAskAssistant} data-testid="phone-more-ask-assistant">
              {t('askTheAssistant')}
            </Menu.Item>
          ) : null}
          <Menu.Item mih={44} leftSection={<IconMessageCircle size={16} />} onClick={() => onOpenSheet('review')} data-testid="phone-more-review">
            {t('review')}
          </Menu.Item>
          <Menu.Item mih={44} component={Link} href={reviewHref} leftSection={<IconMessageCircle size={16} />} data-testid="phone-more-review-page">
            {t('reviewPage')}
          </Menu.Item>
          <Menu.Item mih={44} leftSection={<IconHistory size={16} />} onClick={() => onOpenSheet('history')} data-testid="phone-more-history">
            {t('history')}
          </Menu.Item>
          <Menu.Item mih={44} leftSection={<IconKeyboard size={16} />} onClick={() => setShortcutsOpen(true)} data-testid="phone-more-shortcuts">
            {t('keyboardShortcuts')}
          </Menu.Item>
          <Menu.Divider />
          <Menu.Item mih={44} leftSection={<IconHandMove size={16} />} closeMenuOnClick={false} onClick={onToggleTouchDrag} role="menuitemcheckbox" aria-checked={touchDrag} data-testid="phone-more-touch-drag">
            {t('dragClipsWithYour')}{touchDrag ? <IconCheck size={16} aria-hidden style={{ marginLeft: 8, verticalAlign: 'middle' }} /> : null}
          </Menu.Item>
          <Menu.Item mih={44} leftSection={<IconLink size={16} />} closeMenuOnClick={false} onClick={() => void copyLink()} data-testid="phone-more-copy-link">
            {linkCopied === 'done' ? t('linkCopied') : linkCopied === 'failed' ? t('couldNotCopyThe') : t('copyLink')}
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
      <Modal opened={shortcutsOpen} onClose={() => setShortcutsOpen(false)} title={t('keyboardShortcuts')} size="md" transitionProps={{ duration: 0 }} data-testid="phone-shortcuts">
        <Stack gap="xs">
          <Text size="12px" c="dimmed">{t('keyboardOnlyNote')}</Text>
          <ScrollArea h={380}>
            <Stack gap={6}>
              {keyboardOnlyActions().map((action) => (
                <div key={action.id} data-testid={`phone-shortcut-${action.id}`}>
                  <Text size="xs" fw={600}>{t.msg(keyActionLabelMsg(action.id))}</Text>
                  <Text size="12px" c="dimmed">{action.reason}</Text>
                </div>
              ))}
            </Stack>
          </ScrollArea>
          <Button size="sm" fz="xs" onClick={() => setShortcutsOpen(false)} data-testid="phone-shortcuts-close">{t('close')}</Button>
        </Stack>
      </Modal>
    </header>
  );
}
