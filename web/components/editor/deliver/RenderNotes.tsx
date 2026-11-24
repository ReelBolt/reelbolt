'use client';

import React, { useId, useState } from 'react';
import { Button, Collapse, List, Stack, Text } from '@mantine/core';
import { IconChevronDown, IconChevronRight } from '@tabler/icons-react';
import type { Msg } from '@/i18n/msg';
import type { RenderNotes as RenderNotesData, RenderNotesMsg } from './render-notes';
import classes from './RenderNotes.module.css';
import { useT } from '@/i18n';

/**
 * RD-5: under a finished render row, a one-line summary of what the render left out and warned
 * about, and a disclosure listing each sentence. Text is 12px (Mantine `xs`); the disclosure is a
 * 44px target on touch.
 */
export function RenderNotes({ notes }: { notes: RenderNotesData | RenderNotesMsg }) {
  const t = useT('editor.deliver.renderNotes');
  const show = (line: Msg | string): string => (typeof line === 'string' ? line : t.msg(line));
  const [open, setOpen] = useState(false);
  const listId = useId();
  return (
    <Stack gap={2} data-testid="render-notes">
      <Text size="xs" c="orange" className={classes.notesText} data-testid="render-notes-summary">
        {show(notes.summary)}
      </Text>
      <Button
        variant="subtle"
        size="xs" fz="xs"
        px={6}
        className={classes.notesToggle}
        leftSection={open ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
        aria-expanded={open}
        aria-controls={listId}
        onClick={() => setOpen((o) => !o)}
        data-testid="render-notes-toggle"
      >
        {open ? t('hideDetails') : t('showDetails')}
      </Button>
      <Collapse expanded={open} id={listId}>
        {notes.skipped.length > 0 && (
          <>
            <Text size="xs" fw={600}>
              {t('leftOut')}
            </Text>
            <List size="xs" spacing={2} mb={notes.warnings.length > 0 ? 4 : 0}>
              {notes.skipped.map((line, i) => (
                <List.Item key={`s${i}`} className={classes.notesText} data-testid="render-notes-line">
                  {show(line)}
                </List.Item>
              ))}
            </List>
          </>
        )}
        {notes.warnings.length > 0 && (
          <>
            <Text size="xs" fw={600}>
              {t('notes')}
            </Text>
            <List size="xs" spacing={2}>
              {notes.warnings.map((line, i) => (
                <List.Item key={`w${i}`} className={classes.notesText} data-testid="render-notes-line">
                  {line}
                </List.Item>
              ))}
            </List>
          </>
        )}
      </Collapse>
    </Stack>
  );
}
