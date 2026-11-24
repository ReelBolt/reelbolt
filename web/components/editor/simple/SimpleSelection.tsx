'use client';

import React from 'react';
import { Button, Group, Text } from '@mantine/core';
import { IconCopy, IconCut, IconTrash } from '@tabler/icons-react';
import type { WorkspaceId } from '@/lib/editing/store';
import type { Item } from '@/lib/editing/types';
import { ClipSheetBody } from '../phone/ClipSheet';
import { TitlesSection } from '../phone/LookSheet';
import type { PhoneActions } from '../phone/phone-actions';
import phone from '../phone/phone.module.css';
import classes from './simple.module.css';
import { fullEditorWork, fullWorkPage, SIMPLE_ITEM_TYPES } from './full-editor-work';
import { useT } from '@/i18n';

export interface SimpleSelectionProps {
  /** The selected items, in timeline order. */
  items: readonly Item[];
  actions: PhoneActions;
  /** Opens the full editor on a page, with the selection kept. */
  onOpenInFull: (workspace: WorkspaceId) => void;
  /** Opens the captions panel (a caption is edited there, with its neighbours). */
  onCaptions: () => void;
}

/**
 * What the Simple shell's panel shows for the selection. A video, picture or sound clip gets the same body the
 * phone's clip sheet has (one undo step per change); a title gets its words and style; a caption goes to the
 * captions panel. Work made in the full editor is said in one line with the way there: it is never hidden.
 */
export function SimpleSelection({ items, actions, onOpenInFull, onCaptions }: SimpleSelectionProps) {
  const t = useT('editor-simple.selection');
  const ro = actions.readOnly;
  const buttons = (
    <Group gap="xs" px={14} pb={8} pt={10} wrap="wrap" data-testid="simple-clip-actions">
      <Button size="xs" fz="xs" variant="default" leftSection={<IconCut size={14} aria-hidden />} disabled={ro || !actions.canSplit} onClick={actions.split} data-testid="simple-split">
        {t('split')}
      </Button>
      <Button size="xs" fz="xs" variant="default" leftSection={<IconCopy size={14} aria-hidden />} disabled={ro} onClick={actions.duplicate} data-testid="simple-duplicate">
        {t('duplicate')}
      </Button>
      <Button size="xs" fz="xs" variant="default" color="red" leftSection={<IconTrash size={14} aria-hidden />} disabled={ro} onClick={actions.remove} data-testid="simple-remove">
        {t('remove')}
      </Button>
    </Group>
  );

  // One clip, or one linked picture and its sound: the clip body.
  if (actions.clip) {
    const item = actions.clip.item;
    if (item.type === 'titleClip') {
      return (
        <div data-testid="simple-selection" data-kind="title">
          {buttons}
          <div className={phone.sheetStack}>
            <TitlesSection actions={actions} />
          </div>
        </div>
      );
    }
    if (item.type === 'caption') {
      return (
        <div data-testid="simple-selection" data-kind="caption">
          {buttons}
          <div className={phone.sheetStack}>
            <Text size="sm">{t('captionLead')}</Text>
            <Button variant="light" onClick={onCaptions} data-testid="simple-open-captions">
              {t('openCaptions')}
            </Button>
          </div>
        </div>
      );
    }
    if (!SIMPLE_ITEM_TYPES.has(item.type)) {
      return (
        <div data-testid="simple-selection" data-kind="other">
          {buttons}
          <div className={classes.fullWork} data-testid="simple-full-item">
            <Text size="sm">{t('otherItem', { name: item.name || t('thisItem') })}</Text>
            <Button size="xs" fz="xs" variant="light" onClick={() => onOpenInFull('edit')} data-testid="simple-open-full-item">
              {t('openInFull')}
            </Button>
          </div>
        </div>
      );
    }
    const work = fullEditorWork(item);
    return (
      <div data-testid="simple-selection" data-kind="clip">
        {buttons}
        {work.length > 0 && (
          <div className={classes.fullWork} data-testid="simple-full-work">
            <Text size="sm">{t('fullWorkLead', { list: work.map((w) => t(`work.${w.kind}`, { count: w.count })).join(', ') })}</Text>
            <Text size="xs" c="dimmed">{t('fullWorkNote')}</Text>
            <Button size="xs" fz="xs" variant="light" onClick={() => onOpenInFull(fullWorkPage(work[0].kind))} data-testid="simple-open-full-work">
              {t('openInFull')}
            </Button>
          </div>
        )}
        <ClipSheetBody actions={actions} animatedNote={t('animatedNote')} />
      </div>
    );
  }

  // Several separate clips: the things that make sense for all of them.
  return (
    <div data-testid="simple-selection" data-kind="many">
      {buttons}
      <div className={phone.sheetStack}>
        <Text size="sm" data-testid="simple-many">{t('many', { count: items.length })}</Text>
        <p className={classes.note}>{t('manyHint')}</p>
      </div>
    </div>
  );
}
