'use client';

import { readableServerMessage } from '@/lib/api/describe-error';
import { Alert, Button, Center, Group, Loader, Paper, SimpleGrid, Stack, Text } from '@mantine/core';
import { IconDownload, IconInfoCircle } from '@tabler/icons-react';
import { MARKETING_ADMIN_EXPORT_URL, type MarketingListSummary } from '@/lib/api/marketing';
import { useMarketingListSummary } from '@/lib/hooks/use-marketing';

/**
 * The per-send process rule (compliance.md, "Export": the admin UI states it). Shown verbatim above
 * the download, because the export is only lawful to use the way this sentence says.
 */
export const MARKETING_SEND_RULE =
  "Download a fresh list right before every send. Use each row's unsubscribe link as the email's List-Unsubscribe header, and remove anyone who unsubscribed in your sending tool before the next send.";

/** How long after a click the summary is re-read, so "Last export" shows the download just made. */
const REFRESH_AFTER_DOWNLOAD_MS = 3_000;

function formatDateTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

function Count({ label, value, testId }: { label: string; value: number; testId: string }) {
  return (
    <Paper withBorder radius="md" p="md">
      <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
        {label}
      </Text>
      <Text size="xl" fw={700} data-testid={testId}>
        {value.toLocaleString()}
      </Text>
    </Paper>
  );
}

function Summary({ summary }: { summary: MarketingListSummary }) {
  const { counts, lastExport } = summary;
  return (
    <>
      <SimpleGrid cols={{ base: 2, md: 4 }} spacing="md">
        <Count label="In the export" value={counts.exportable} testId="marketing-count-exportable" />
        <Count label="Opted in" value={counts.optedIn} testId="marketing-count-opted-in" />
        <Count label="Awaiting confirmation" value={counts.pending} testId="marketing-count-pending" />
        <Count label="Unsubscribed" value={counts.withdrawn} testId="marketing-count-withdrawn" />
      </SimpleGrid>
      <Text size="sm" c="dimmed" data-testid="marketing-last-export">
        {lastExport
          ? `Last export: ${formatDateTime(lastExport.exportedAt)}, ${lastExport.rowCount.toLocaleString()} ${lastExport.rowCount === 1 ? 'row' : 'rows'}.`
          : 'This list has never been exported.'}
      </Text>
    </>
  );
}

/**
 * Platform admin: the marketing email list, for a send made from an external sending tool.
 *
 * Shows counts only — never an address — and a plain download link for the CSV. The server
 * restricts both to platform admins, logs each export and answers `Cache-Control: no-store`, so no
 * copy is kept here either: the browser downloads the file straight from the API.
 */
export function MarketingListPanel({ enabled = true }: { enabled?: boolean }) {
  const summary = useMarketingListSummary(enabled);

  return (
    <Stack gap="md" data-testid="marketing-list-panel">
      <Text size="sm" c="dimmed" data-testid="marketing-list-scope">
        The list includes only confirmed, verified addresses: people who confirmed from their inbox,
        or who opted in while signed in to an account whose email is verified. Addresses awaiting
        confirmation, unsubscribed addresses and unverified accounts are never in it.
      </Text>

      {summary.error ? (
        <Alert color="red" variant="light" title="Could not load the marketing list">
          {readableServerMessage(summary.error)
            ?? 'Please try again in a moment.'}
        </Alert>
      ) : !summary.data ? (
        <Center h={120}>
          <Loader />
        </Center>
      ) : (
        <Summary summary={summary.data} />
      )}

      <Alert
        color="blue"
        variant="light"
        icon={<IconInfoCircle size={16} />}
        title="Before every send"
        data-testid="marketing-send-rule"
      >
        {MARKETING_SEND_RULE}
      </Alert>

      <Group>
        <Button
          component="a"
          href={MARKETING_ADMIN_EXPORT_URL}
          download
          leftSection={<IconDownload size={16} />}
          data-testid="marketing-export-download"
          onClick={() => {
            window.setTimeout(() => void summary.mutate(), REFRESH_AFTER_DOWNLOAD_MS);
          }}
        >
          Download CSV
        </Button>
      </Group>
    </Stack>
  );
}
