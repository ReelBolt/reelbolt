'use client';

import React from 'react';
import { Button, Tooltip } from '@mantine/core';
import { IconMusic } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { EditorIcon } from '../ui';

export interface CutToBeatButtonProps {
  /** Read-only documents, or a timeline with no media clip to read. */
  disabled: boolean;
  onOpen: () => void;
  /** The music clip has a ready beat analysis, so the overlay can be shown. */
  beatsReady: boolean;
  showBeats: boolean;
  onToggleBeats: () => void;
  /** The toolbar is out of room: the icon alone, named. */
  iconOnly?: boolean;
}

/**
 * The timeline toolbar's entry to "Cut to the music", plus the "Show beats" toggle once a music
 * clip's beat analysis is ready. The phone reaches the same dialog from the Captions sheet.
 */
export function CutToBeatButton({ disabled, onOpen, beatsReady, showBeats, onToggleBeats, iconOnly = false }: CutToBeatButtonProps) {
  const tx = useT('editor.panels.cutToBeatDialog');
  return (
    <>
      {iconOnly ? (
        <EditorIcon icon={IconMusic} label={tx('cutToTheMusic')} tooltip={tx('openHint')} onClick={onOpen} disabled={disabled} data-testid="cut-to-beat-open" />
      ) : (
      <Tooltip label={tx('openHint')} openDelay={300}>
        <Button
          size="compact-sm"
          fz="xs"
          variant="subtle"
          color="gray"
          onClick={onOpen}
          leftSection={<IconMusic size={15} stroke={1.7} />}
          data-testid="cut-to-beat-open"
          disabled={disabled}
          fw={500}
        >
          {tx('cutToTheMusic')}
        </Button>
      </Tooltip>
      )}
      {beatsReady && (
        <Button
          size="compact-sm"
          fz="xs"
          variant={showBeats ? 'light' : 'subtle'}
          color={showBeats ? 'violet' : 'gray'}
          onClick={onToggleBeats}
          aria-pressed={showBeats}
          data-testid="show-beats-toggle"
        >
          {showBeats ? tx('hideBeats') : tx('showBeats')}
        </Button>
      )}
    </>
  );
}
