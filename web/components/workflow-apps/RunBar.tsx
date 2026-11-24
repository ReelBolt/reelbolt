'use client';

import { useId } from 'react';
import Link from 'next/link';
import { Anchor, Button, Text } from '@mantine/core';
import { IconPlayerPlay, IconPlayerStop } from '@tabler/icons-react';
import { UpgradeCallout } from '@/components/billing/UpgradeCallout';
import { useT } from '@/i18n';
import styles from './AppRunPage.module.css';

/** Why Run cannot be pressed, in a sentence, with the one thing that fixes it when there is one. */
export interface RunBlock {
  /** Stable key for tests and focus. */
  key: string;
  reason: string;
  /** The label of the fix; on a phone the Run button reads it and does it. */
  fixLabel?: string | undefined;
  onFix?: (() => void) | undefined;
  /** Links that help (add credits, see plans). */
  links?: { label: string; href: string }[] | undefined;
  /** The refusal is the plan's doing: the shared upgrade callout (what plan unlocks it, and who can upgrade) follows the reason. */
  upgrade?: { feature: string | null; planKey?: string | null; planName?: string | null } | undefined;
  /** The field a blocked press moves focus to (the first one with a problem). */
  focusId?: string | undefined;
  /** Moves focus to that field when it may be closed away (under "More options"); used instead of `focusId`. */
  onFocus?: (() => void) | undefined;
}

/** The credits the run needs and the workspace lacks: a sentence and the ways to fix it. */
export interface RunShortfall {
  reason: string;
  links: { label: string; href: string }[];
}

interface RunBarProps {
  /** "Run · about 40 credits" (or just "Run"). */
  label: string;
  /** "usually 30 to 55" / "No credits used". */
  range: string | null;
  /** "You have 100 credits": shown next to the estimate whenever billing is on. */
  balance?: string | null;
  /** Not enough credits: Run is disabled with this sentence and a link, alongside any other blocker. */
  shortfall?: RunShortfall | null;
  priceUnavailable: boolean;
  onRetryPrice: () => void;
  block: RunBlock | null;
  phone: boolean;
  busy: boolean;
  /** A run is going: Run is replaced by Stop. */
  running: boolean;
  onStop: () => void;
  showBreakdown: boolean;
  /** The breakdown opens outside the bar (it would fill a phone screen inside it). */
  why: boolean;
  onToggleWhy: () => void;
  /** A line of status shown in the bar while a run is going. */
  runningText?: string | null;
  /**
   * Whether the block's reason is printed in the bar. False when the readiness strip above already
   * shows the same sentence (the first fix item), so the two never repeat each other.
   */
  showReason?: boolean;
  /** Called when Run is pressed while a block is standing (measurement only). */
  onBlockedPress?: (key: string) => void;
  onRun: () => void;
}

/**
 * Run, with the price on the button and the range on the line below it. While anything blocks the
 * run the button is `aria-disabled` (not `disabled`, so it stays in the tab order) and the reason is
 * linked with `aria-describedby`. A warning never lands here; only a standing error does. On a phone
 * the button reads the fix ("Add a clip") and performs it.
 */
export function RunBar({
  label, range, balance = null, shortfall = null, priceUnavailable, onRetryPrice, block, phone, busy, running, onStop, showBreakdown, why, onToggleWhy, runningText, showReason = true, onRun, onBlockedPress,
}: RunBarProps) {
  const t = useT('apps.run.bar');
  const reasonId = useId();
  const rangeId = useId();
  const shortfallId = useId();
  const blocked = Boolean(block) || Boolean(shortfall);
  const fixOnButton = phone && block?.fixLabel && block.onFix;
  const describedBy = [block ? reasonId : null, shortfall ? shortfallId : null, range || balance ? rangeId : null].filter(Boolean).join(' ') || undefined;

  const press = () => {
    if (busy) return;
    if (block) {
      onBlockedPress?.(block.key);
      // A phone's button reads the fix and does it. On a desktop a blocked press moves focus; only the
      // empty-brief block also runs its onFix (it marks the brief touched), never another block's fix.
      if (phone && block.onFix) block.onFix();
      else if (block.key === 'notes:required' && block.onFix) block.onFix();
      else if (block.onFocus) block.onFocus();
      else if (block.focusId) document.getElementById(block.focusId)?.focus();
      return;
    }
    if (shortfall) {
      onBlockedPress?.('credits');
      return;
    }
    onRun();
  };

  return (
    <div className={styles.actions}>
      {!running && block && (
        <div className={`${styles.reason} ${showReason ? '' : styles.visuallyHidden}`} id={reasonId} data-testid="run-block-reason">
          {block.reason}{' '}
          {showReason && block.links?.map((l) => (
            <Anchor key={l.href} component={Link} href={l.href} size="sm" mr="xs">
              {l.label}
            </Anchor>
          ))}
          {showReason && block.upgrade && (
            <UpgradeCallout
              compact
              feature={block.upgrade.feature}
              plan={{ key: block.upgrade.planKey ?? null, name: block.upgrade.planName ?? null }}
              testId="run-block-upgrade"
            />
          )}
          {showReason && !phone && block.onFix && block.fixLabel && (
            <Button size="compact-sm" variant="default" onClick={block.onFix} style={{ minHeight: 32 }}>
              {block.fixLabel}
            </Button>
          )}
        </div>
      )}
      {!running && shortfall && (
        <div className={styles.reason} id={shortfallId} data-testid="run-shortfall">
          {shortfall.reason}{' '}
          {shortfall.links.map((l) => (
            <Anchor key={l.href} component={Link} href={l.href} size="sm" mr="xs" style={phone ? { display: 'inline-flex', alignItems: 'center', minHeight: 44 } : undefined}>
              {l.label}
            </Anchor>
          ))}
        </div>
      )}
      {running && runningText && (
        <span className={styles.reason} role="status">
          {runningText}
        </span>
      )}
      {running ? (
        <Button size="md" variant="default" className={styles.runButton} leftSection={<IconPlayerStop size={18} />} loading={busy} onClick={onStop} data-testid="stop-button">
          {t('stop')}
        </Button>
      ) : (
      <Button
        size="md"
        className={`${styles.runButton} ${blocked ? styles.blocked : ''}`}
        leftSection={<IconPlayerPlay size={18} />}
        aria-disabled={blocked}
        aria-describedby={describedBy}
        loading={busy}
        onClick={press}
        variant="gradient"
        gradient={{ from: 'violet', to: 'purple' }}
        data-testid="run-button"
      >
        {fixOnButton ? block.fixLabel : label}
      </Button>
      )}
      {!running && (range || (balance && !shortfall)) && (
        <span className={styles.priceRange} id={rangeId}>
          {balance && !shortfall && <span className={styles.balance} data-testid="run-balance">{balance}</span>}
          {balance && !shortfall && range ? ' · ' : null}
          {range}
          {showBreakdown && (
            <>
              {' · '}
              <Anchor component="button" type="button" size="sm" onClick={onToggleWhy}
                aria-expanded={why}
                style={phone ? { display: 'inline-flex', alignItems: 'center', minHeight: 44, padding: '0 8px' } : undefined}
              >
                {t('whyPrice')}
              </Anchor>
            </>
          )}
        </span>
      )}
      {!running && priceUnavailable && (
        <Text size="sm" className={styles.reason} role="status">
          {t('priceUnavailable')}{' '}
          <Anchor component="button" type="button" size="sm" onClick={onRetryPrice}>
            {t('tryAgain')}
          </Anchor>
        </Text>
      )}
    </div>
  );
}
