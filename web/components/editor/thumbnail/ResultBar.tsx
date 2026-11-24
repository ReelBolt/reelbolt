'use client';

import React from 'react';
import { Button, Group, Paper, Stack, Text } from '@mantine/core';
import { IconChevronLeft, IconChevronRight, IconX } from '@tabler/icons-react';
import type { ResultInfo } from '@/lib/editor/result-bar';
import { useT } from '@/i18n';
import { EditorIcon } from '../ui';

export interface ResultBarProps {
  info: ResultInfo;
  /** More than one layer came back as choices (the first is showing). */
  options: boolean;
  /** Which option is showing (0-based) when `options`. */
  activeOption: number;
  onOption(index: number): void;
  comparing: boolean;
  onCompare(on: boolean): void;
  /** Undo is available and the latest step is this run's. */
  canUndo: boolean;
  onUndo(): void;
  /** Absent when this run cannot be repeated exactly (it used a painted area that is gone). */
  onTryAgain?: () => void;
  /** "about 4.5 credits" for the Try again button, or null. */
  tryAgainCost?: string | null;
  tryAgainDisabled?: boolean;
  onKeepOne(): void;
  onKeepAll(): void;
  onDismiss(): void;
  coarse?: boolean;
}

/**
 * The one bar after a paid run: what changed, what it cost, then Compare with before, Undo and Try
 * again; and for a run that came back with several options, which one is showing with "Keep this
 * one" (the rest are removed, and undo brings them back) or "Keep all".
 */
export function ResultBar({ info, options, activeOption, onOption, comparing, onCompare, canUndo, onUndo, onTryAgain, tryAgainCost, tryAgainDisabled, onKeepOne, onKeepAll, onDismiss, coarse = false }: ResultBarProps) {
  const t = useT('editor-thumbnail-a.resultBar');
  const tc = useT('editor-thumbnail-a.common');
  const h = coarse ? 44 : undefined;
  const n = info.layerNames.length;
  const changed =
    n === 0 ? t('changed.none', { title: info.title })
    : options && n > 1 ? t('changed.options', { title: info.title, count: n })
    : n === 1 ? t('changed.one', { title: info.title, name: info.layerNames[0] })
    : t('changed.many', { title: info.title, count: n });
  const charged = info.charged !== null && info.charged > 0 ? t('charged.cost', { credits: tc('credits', { n: Math.round(info.charged * 100) / 100 }) }) : t('charged.none');
  const total = info.layerIds.length;
  return (
    <Paper withBorder p="xs" radius="md" role="status" aria-live="polite" data-testid="result-bar">
      <Stack gap={6}>
        <Group justify="space-between" wrap="nowrap" align="flex-start">
          <Stack gap={2}>
            <Text size="xs" fw={600} data-testid="result-bar-changed">
              {changed}
            </Text>
            <Text size="xs" c="dimmed" data-testid="result-bar-cost">
              {charged}
              {info.notice ? ` ${info.notice}` : ''}
            </Text>
          </Stack>
          <EditorIcon icon={IconX} size="sm" label={t('dismiss')} onClick={onDismiss} />
        </Group>
        {options && total > 1 && (
          <Group gap={6} wrap="wrap" data-testid="result-bar-options">
            <EditorIcon icon={IconChevronLeft} variant="light" label={t('previous')} disabled={activeOption <= 0} onClick={() => onOption(activeOption - 1)} />
            <Text size="xs" fw={600} data-testid="result-bar-option-label">
              {t('option', { n: activeOption + 1, total })}
            </Text>
            <EditorIcon icon={IconChevronRight} variant="light" label={t('next')} disabled={activeOption >= total - 1} onClick={() => onOption(activeOption + 1)} />
            <Button size="xs" fz="xs" h={h} onClick={onKeepOne} data-testid="result-keep-one">
              {t('keepOne')}
            </Button>
            <Button size="xs" fz="xs" h={h} variant="default" onClick={onKeepAll} data-testid="result-keep-all">
              {t('keepAll')}
            </Button>
          </Group>
        )}
        <Group gap={6} wrap="wrap">
          <Button size="xs" fz="xs" h={h} variant={comparing ? 'filled' : 'light'} aria-pressed={comparing} onClick={() => onCompare(!comparing)} data-testid="result-compare">
            {comparing ? t('showingBefore') : t('compare')}
          </Button>
          <Button size="xs" fz="xs" h={h} variant="light" disabled={!canUndo} onClick={onUndo} data-testid="result-undo" aria-label={t('undoLabel')}>
            {t('undo')}
          </Button>
          {onTryAgain && (
            <Button size="xs" fz="xs" h={h} variant="light" disabled={!canUndo || tryAgainDisabled} onClick={onTryAgain} data-testid="result-try-again">
              {tryAgainCost ? t('tryAgainCost', { cost: tryAgainCost }) : t('tryAgain')}
            </Button>
          )}
        </Group>
      </Stack>
    </Paper>
  );
}
