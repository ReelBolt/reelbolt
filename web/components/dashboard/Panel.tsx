'use client';

import Link from 'next/link';
import { ActionIcon, Anchor, Group, Text } from '@mantine/core';
import { IconX } from '@tabler/icons-react';
import classes from './dashboard.module.css';
import { useT } from '@/i18n';

export function Panel({
  title,
  action,
  children,
  tone,
  id,
  onDismiss,
  dismissLabel,
}: {
  title?: React.ReactNode;
  action?: { label: string; href: string };
  children: React.ReactNode;
  tone?: 'attention';
  id?: string;
  /** When set, the panel header gets an X that lets the viewer close this warning. */
  onDismiss?: () => void;
  /** Accessible name of that X: say what disappears, not just "close". */
  dismissLabel?: string;
}) {
  const t = useT('dashboard');
  return (
    <section className={`${classes.panel} ${tone === 'attention' ? classes.attention : ''}`} aria-labelledby={id}>
      {(title || action || onDismiss) && (
        <div className={classes.panelHead}>
          <h2 id={id} className={classes.panelTitle} style={{ margin: 0 }}>{title}</h2>
          {(action || onDismiss) && (
            <Group gap={4} wrap="nowrap" style={{ alignSelf: 'center', minHeight: 32 }}>
              {action && (
                <Anchor component={Link} href={action.href} size="sm" style={{ whiteSpace: 'nowrap', display: 'inline-flex', alignItems: 'center', minHeight: 44, marginBlock: -12 }}>
                  {action.label}
                </Anchor>
              )}
              {onDismiss && (
                <ActionIcon className={classes.dismiss} variant="subtle" color="gray" size="md" aria-label={dismissLabel ?? t('dismiss')} onClick={onDismiss}>
                  <IconX size={16} />
                </ActionIcon>
              )}
            </Group>
          )}
        </div>
      )}
      {children}
    </section>
  );
}

export function QuietText({ children }: { children: React.ReactNode }) {
  return <Text size="sm" c="dimmed">{children}</Text>;
}
