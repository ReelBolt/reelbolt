'use client';

import React from 'react';
import Link from 'next/link';
import { Anchor, Loader, Stack, Text } from '@mantine/core';
import { BILLING_SETTINGS_PATH } from '@/components/styles/style-options';
import { useFormat, useT } from '@/i18n';
import { formatEstimateMsg, formatSizeMsg, type RenderEstimate } from './deliver-model';
import classes from './Deliver.module.css';

/**
 * Where the cost of the render on screen stands. Every figure comes from `POST …/render/estimate`; nothing here
 * works out or remembers a price of its own.
 */
export type CostState =
  /** Nothing has been asked (a read-only timeline, or the form has not reported its settings yet). */
  | { kind: 'idle' }
  /** The settings changed and the answer for them is on its way. */
  | { kind: 'loading' }
  /** Billing is off on this installation: there is no cost to state. */
  | { kind: 'billingOff'; estimate?: RenderEstimate | null }
  | { kind: 'ready'; estimate: RenderEstimate }
  /** The estimate itself was refused (not enough credits, or the plan does not include these settings). */
  | { kind: 'refused'; message: string; link: 'credits' | 'plans' | null }
  /** The estimate could not be fetched; the render still checks credits before it starts. */
  | { kind: 'error'; onRetry: () => void };

/** What the Render button will make, already in words. */
export interface RenderSummary {
  /** "Full HD landscape". */
  format: string;
  /** "1920 × 1080", with the plan's limit when it applies; null when there is no picture to size. */
  size: string | null;
  /** "Whole timeline, 00:00:00:00 to 00:01:23:10" or the marked range in timecode. */
  range: string;
  /** Where the captions go; null when the timeline has none. */
  captions: string | null;
}

export interface CostStatementProps {
  summary: RenderSummary;
  cost: CostState;
}

const tenth = (n: number): number => Math.round(n * 10) / 10;

/**
 * The one statement a person reads before a render: what will be made (format, size, range, captions), about how
 * long it takes and what it costs. It is always on screen above the Render button and has a sentence for every
 * state: working it out, billing off, a figure, a refusal and a failure.
 */
export function CostStatement({ summary, cost }: CostStatementProps) {
  const t = useT('editor.deliver.costStatement');
  const fmt = useFormat();
  const estimate = cost.kind === 'ready' || cost.kind === 'billingOff' ? (cost.estimate ?? null) : null;
  const estimateMsg = cost.kind === 'ready' ? formatEstimateMsg(estimate) : null;
  const sizeMsg = formatSizeMsg(estimate);
  const low = estimate?.creditsLow;
  const high = estimate?.creditsHigh;
  const hasRange = cost.kind === 'ready' && typeof low === 'number' && typeof high === 'number' && Number.isFinite(low) && Number.isFinite(high) && high > low;
  const balance = cost.kind === 'ready' && typeof estimate?.balanceCredits === 'number' && Number.isFinite(estimate.balanceCredits) ? estimate.balanceCredits : null;

  return (
    <Stack gap={6} data-testid="render-cost" data-cost={cost.kind}>
      <dl className={classes.costList} data-testid="render-summary">
        <Text component="dt" size="xs">
          {t('format')}
        </Text>
        <Text component="dd" size="sm" data-testid="render-summary-format">
          {summary.size ? t('formatWithSize', { format: summary.format, size: summary.size }) : summary.format}
        </Text>
        <Text component="dt" size="xs">
          {t('range')}
        </Text>
        <Text component="dd" size="sm" data-testid="render-summary-range">
          {summary.range}
        </Text>
        {summary.captions ? (
          <>
            <Text component="dt" size="xs">
              {t('captions')}
            </Text>
            <Text component="dd" size="sm" data-testid="render-summary-captions">
              {summary.captions}
            </Text>
          </>
        ) : null}
        {sizeMsg ? (
          <>
            <Text component="dt" size="xs">
              {t('fileSize')}
            </Text>
            <Text component="dd" size="sm" data-testid="render-summary-size">
              {t.msg(sizeMsg)}
            </Text>
          </>
        ) : null}
        {cost.kind === 'billingOff' || cost.kind === 'idle' ? null : (
          <>
            <Text component="dt" size="xs">
              {t('cost')}
            </Text>
            <Text component="dd" size="sm" role="status" aria-live="polite">
              {cost.kind === 'loading' ? (
                <span data-testid="render-cost-loading">
                  <Loader size={12} mr={6} aria-hidden />
                  {t('workingItOut')}
                </span>
              ) : cost.kind === 'error' ? (
                <span data-testid="render-cost-error">
                  {t('couldNotWorkOut')}{' '}
                  <Anchor component="button" type="button" size="sm" className={classes.touchTarget} onClick={cost.onRetry} data-testid="render-cost-retry">
                    {t('tryAgain')}
                  </Anchor>
                </span>
              ) : cost.kind === 'refused' ? (
                <span data-testid="render-cost-refused">
                  {cost.message}{' '}
                  {cost.link ? (
                    <Anchor component={Link} href={cost.link === 'plans' ? `${BILLING_SETTINGS_PATH}#plans` : BILLING_SETTINGS_PATH} size="sm" data-testid="render-cost-link">
                      {cost.link === 'plans' ? t('seePlans') : t('addCredits')}
                    </Anchor>
                  ) : null}
                </span>
              ) : estimateMsg ? (
                <>
                  <span data-testid="render-estimate">{t.msg(estimateMsg)}</span>
                  {hasRange ? (
                    <Text component="span" size="xs" c="dimmed" display="block" data-testid="render-estimate-range">
                      {t('usuallyBetween', { low: fmt.number(tenth(low as number)), high: fmt.number(tenth(high as number)) })}
                    </Text>
                  ) : null}
                </>
              ) : (
                <span data-testid="render-cost-unknown">{estimate?.unavailableReason?.trim() || t('noFigure')}</span>
              )}
            </Text>
          </>
        )}
        {balance !== null ? (
          <>
            <Text component="dt" size="xs">
              {t('yourCredits')}
            </Text>
            <Text component="dd" size="sm" data-testid="render-balance">
              {t('balance', { balance: tenth(balance) })}
            </Text>
          </>
        ) : null}
      </dl>
      {cost.kind === 'billingOff' ? (
        <Text size="sm" role="status" data-testid="render-ready">
          {t('readyToRender')}
        </Text>
      ) : null}
    </Stack>
  );
}
