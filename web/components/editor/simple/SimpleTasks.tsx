'use client';

import React from 'react';
import { UnstyledButton } from '@mantine/core';
import { IconBadgeCc, IconCrop, IconMusic, IconScissors, IconWand, IconTextSize, IconVolume, IconWaveSine } from '@tabler/icons-react';
import classes from './simple.module.css';
import { useT } from '@/i18n';

export type SimpleTaskId = 'silence' | 'captions' | 'beats' | 'sound' | 'format' | 'title' | 'tighten';

/** The tasks, in the order a first video usually needs them. Each one opens something the editor already has. */
export const SIMPLE_TASKS: readonly { id: SimpleTaskId; icon: React.ComponentType<{ size?: number | string; 'aria-hidden'?: boolean }>; needsClips: boolean }[] = [
  { id: 'silence', icon: IconWaveSine, needsClips: true },
  { id: 'captions', icon: IconBadgeCc, needsClips: true },
  { id: 'title', icon: IconTextSize, needsClips: false },
  { id: 'beats', icon: IconMusic, needsClips: true },
  { id: 'sound', icon: IconVolume, needsClips: true },
  { id: 'format', icon: IconCrop, needsClips: false },
  { id: 'tighten', icon: IconWand, needsClips: true },
];

export interface SimpleTasksProps {
  /** Whether the timeline has anything on it: most tasks need a clip to work on. */
  hasClips: boolean;
  readOnly: boolean;
  onTask: (task: SimpleTaskId) => void;
}

/**
 * What the Simple shell's panel shows with nothing selected: a short list of things people want done, in words.
 * A task never does anything by itself: it opens the dialog or panel that does, with that one's own price and undo.
 */
export function SimpleTasks({ hasClips, readOnly, onTask }: SimpleTasksProps) {
  const t = useT('editor-simple.tasks');
  return (
    <div className={classes.tasks} data-testid="simple-tasks">
      <p className={classes.note}>{hasClips ? t('lead') : t('leadEmpty')}</p>
      {SIMPLE_TASKS.map(({ id, icon: Icon, needsClips }) => (
        <UnstyledButton key={id} className={classes.task} disabled={readOnly || (needsClips && !hasClips)} onClick={() => onTask(id)} data-testid={`simple-task-${id}`}>
          <span style={{ flex: '0 0 20px', display: 'inline-flex', marginTop: 1 }}>
            <Icon size={20} aria-hidden />
          </span>
          <span className={classes.taskText}>
            <span className={classes.taskTitle}>{t(`${id}.title`)}</span>
            <span className={classes.taskHint}>{t(`${id}.hint`)}</span>
          </span>
        </UnstyledButton>
      ))}
      <p className={classes.note}>
        <IconScissors size={12} aria-hidden style={{ verticalAlign: '-2px', marginRight: 4 }} />
        {t('selectHint')}
      </p>
    </div>
  );
}
