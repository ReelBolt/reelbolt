'use client';

import React, { useState } from 'react';
import { Button, Select, Slider, Text } from '@mantine/core';
import { IconCamera, IconColorFilter, IconLayoutColumns } from '@tabler/icons-react';
import { useCoarsePointer } from '../thumbnail/useCoarsePointer';
import { useBeforeAfterSplit } from './useBeforeAfterSplit';
import { useT } from '@/i18n';
import classes from './colour-depth.module.css';

export interface CompareBarProps {
  /** The bypass as the user set it (a panel's own brief bypass is not shown). */
  shownBypass: boolean;
  /** The user's choice; it wins over a brief bypass in progress. */
  onChooseBypass: (bypass: boolean) => void;
  /** The raw bypass, for the split's own capture (bypass, grab the picture, put it back). */
  gradeBypass: boolean;
  onGradeBypass: (bypass: boolean) => void;
  gallery: readonly { id: string; label: string }[];
  referenceId: string | null;
  onReference: (id: string | null) => void;
  wipe: number;
  onWipe: (fraction: number) => void;
  onCaptureStill: (label: string) => Promise<void>;
  /** The label a grabbed still gets (the clip's name). */
  stillLabel: string;
}

/**
 * Before / after as first-class controls (pro UI wave 4), above the grade tools on every tab: bypass every
 * grade, split the picture before / after, and pick the reference still the viewer wipes against. All of it
 * is view state — nothing here writes the document, so it works on a read-only timeline too.
 */
export function CompareBar({ shownBypass, onChooseBypass, gradeBypass, onGradeBypass, gallery, referenceId, onReference, wipe, onWipe, onCaptureStill, stillLabel }: CompareBarProps) {
  const t = useT('editor-color.compare');
  const coarse = useCoarsePointer();
  const split = useBeforeAfterSplit({ gradeBypass, onGradeBypass, gallery, referenceId, onReference, onCaptureStill });
  // The split's capture bypasses the grade for a moment: the button keeps showing what the user had set.
  const [held, setHeld] = useState<boolean | null>(null);
  const pressed = split.busy && held !== null ? held : shownBypass;
  const toggleSplit = () => {
    if (split.active) {
      split.stop();
      return;
    }
    setHeld(shownBypass);
    void split.start().finally(() => setHeld(null));
  };
  const size = coarse ? 'md' : 'compact-xs';
  const touch = coarse ? { minHeight: 44 } : undefined;

  return (
    <div className={classes.compareBar} role="group" aria-label={t('label')} data-testid="color-compare">
      <Button size={size} fz="xs" style={touch} variant={pressed ? 'filled' : 'light'} color={pressed ? 'orange' : undefined} leftSection={<IconColorFilter size={12} />} aria-pressed={pressed} title={t('bypassTip')} onClick={() => onChooseBypass(!pressed)} data-testid="color-compare-bypass">
        {pressed ? t('bypassOn') : t('bypass')}
      </Button>
      <Button size={size} fz="xs" style={touch} variant={split.active ? 'filled' : 'light'} leftSection={<IconLayoutColumns size={12} />} aria-pressed={split.active} loading={split.busy} title={t('splitTip')} onClick={toggleSplit} data-testid="color-compare-split">
        {split.active ? t('closeSplit') : t('split')}
      </Button>
      <Select
        size={coarse ? 'md' : 'xs'}
        w={150}
        aria-label={t('reference')}
        placeholder={gallery.length === 0 ? t('noStills') : t('referencePlaceholder')}
        data={gallery.map((s) => ({ value: s.id, label: s.label }))}
        value={referenceId}
        onChange={onReference}
        clearable
        disabled={gallery.length === 0}
        comboboxProps={{ withinPortal: true }}
        styles={coarse ? { input: { minHeight: 44 } } : undefined}
        data-testid="color-compare-reference"
      />
      <Button size={size} fz="xs" style={touch} variant="subtle" leftSection={<IconCamera size={12} />} title={t('grabTip')} onClick={() => void onCaptureStill(stillLabel)} data-testid="color-compare-grab">
        {t('grab')}
      </Button>
      {referenceId && (
        <>
          <Text size="xs" c="dimmed">
            {t('wipe')}
          </Text>
          <Slider size={coarse ? 'lg' : 'xs'} style={{ flex: '1 1 80px', minWidth: 80 }} min={0} max={1} step={0.01} value={wipe} onChange={onWipe} label={null} thumbLabel={t('wipePosition')} data-testid="color-compare-wipe" />
        </>
      )}
    </div>
  );
}

/** "Solo: <name>" with the way out. Shown above the grade tools and above the scopes while a node is soloed. */
export function SoloLine({ name, onLeave, testId }: { name: string; onLeave: () => void; testId: string }) {
  const t = useT('editor-color.compare');
  const coarse = useCoarsePointer();
  return (
    <div className={classes.soloLine} role="status" data-testid={testId}>
      <Text size="xs" fw={600} truncate style={{ minWidth: 0 }}>
        {t('solo', { name })}
      </Text>
      <Text size="xs" c="dimmed" truncate style={{ flex: 1, minWidth: 0 }}>
        {t('soloNote')}
      </Text>
      <Button size={coarse ? 'md' : 'compact-xs'} fz="xs" variant="light" color="yellow" style={coarse ? { minHeight: 44, flexShrink: 0 } : { flexShrink: 0 }} onClick={onLeave} data-testid={`${testId}-leave`}>
        {t('leaveSolo')}
      </Button>
    </div>
  );
}
