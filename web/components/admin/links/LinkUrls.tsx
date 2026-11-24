'use client';

import { Button, Code, CopyButton, Group, Stack, Text, VisuallyHidden } from '@mantine/core';
import { IconCheck, IconCopy } from '@tabler/icons-react';
import { useT } from '@/i18n';
import type { MarketingLink } from '@/lib/types/marketing-links';

/**
 * The public origin the links are posted on. The dashboard is served under `/app` on the site's own
 * host, so its origin is the site's. Empty during server rendering, where no link is ever shown.
 */
export function linkOrigin(): string {
  return typeof window === 'undefined' ? '' : window.location.origin;
}

/** The short address an operator posts (`https://<site>/l/<code>`). */
export const shortUrlOf = (link: Pick<MarketingLink, 'shortPath'>): string => linkOrigin() + link.shortPath;

/** The landing page plus the link's campaign parameters; works without the redirect. */
export const longUrlOf = (link: Pick<MarketingLink, 'longPath'>): string => linkOrigin() + link.longPath;

/** Copies `text`, answering whether the browser allowed it (it refuses outside a secure context). */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

function UrlBlock({
  label,
  hint,
  url,
  copyLabel,
  testId,
}: {
  label: string;
  hint: string;
  url: string;
  copyLabel: string;
  testId: string;
}) {
  const t = useT('adminLinks.urls');
  return (
    <Stack gap={6}>
      <Text size="sm" fw={600}>
        {label}
      </Text>
      {/* tabIndex: the address stays selectable by keyboard when the clipboard is not available. */}
      <Code block p="sm" tabIndex={0} data-testid={testId} style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
        {url}
      </Code>
      <Text size="sm" c="dimmed">
        {hint}
      </Text>
      <CopyButton value={url}>
        {({ copied, copy }) => (
          <Group>
            <Button
              variant="default"
              leftSection={copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
              color={copied ? 'green' : undefined}
              onClick={copy}
              data-testid={`${testId}-copy`}
            >
              {copied ? t('copied') : copyLabel}
            </Button>
            <VisuallyHidden role="status" aria-live="polite">
              {copied ? t('copied') : ''}
            </VisuallyHidden>
          </Group>
        )}
      </CopyButton>
    </Stack>
  );
}

/** A link's two addresses, each with a copy button and a sentence on where to use it. */
export function LinkUrls({ link }: { link: Pick<MarketingLink, 'shortPath' | 'longPath'> }) {
  const t = useT('adminLinks.urls');
  return (
    <Stack gap="lg" data-testid="link-urls">
      <UrlBlock
        label={t('shortLabel')}
        hint={t('shortHint')}
        url={shortUrlOf(link)}
        copyLabel={t('copyShort')}
        testId="link-short-url"
      />
      <UrlBlock
        label={t('longLabel')}
        hint={t('longHint')}
        url={longUrlOf(link)}
        copyLabel={t('copyLong')}
        testId="link-long-url"
      />
    </Stack>
  );
}
