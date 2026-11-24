'use client';

import React from 'react';
import { SegmentedControl, Tooltip } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import type { EditorShell } from '@/lib/editing/shell-choice';
import { useT } from '@/i18n';

/**
 * The Simple | Full switch, in the header of both shells. It changes how the editor is laid out on this device and
 * nothing else: the timeline, the selection, the playhead and undo history are the same on both sides.
 */
export function ShellSwitch({ shell, onChange }: { shell: EditorShell; onChange: (shell: EditorShell) => void }) {
  const t = useT('editor-simple.shell');
  const coarse = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  return (
    <Tooltip label={t('switchHint')} openDelay={400} multiline w={260}>
      <SegmentedControl
        size={coarse ? 'xl' : 'xs'}
        value={shell}
        onChange={(value) => onChange(value === 'simple' ? 'simple' : 'full')}
        data={[
          { value: 'simple', label: t('simple') },
          { value: 'full', label: t('full') },
        ]}
        aria-label={t('switchLabel')}
        data-testid="shell-switch"
        data-shell={shell}
        styles={coarse ? { root: { minHeight: 44 }, label: { minHeight: 40, display: 'flex', alignItems: 'center' } } : undefined}
        style={{ flexShrink: 0 }}
      />
    </Tooltip>
  );
}
