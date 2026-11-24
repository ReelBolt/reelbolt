'use client';

import { Card, Stack, Text, Title } from '@mantine/core';

/**
 * One card on a settings page: a heading, an optional one-line explanation, and the control.
 *
 * The heading is order 3 so it sits directly under the page's own order-2 title (PageHeader). That
 * is the same relationship every other settings page has; the difference is only that this states
 * it once instead of each page re-deriving it.
 */
export function SettingsCard({
  title,
  description,
  children,
  testId,
}: {
  title: string;
  description?: string;
  children: React.ReactNode;
  testId?: string;
}) {
  return (
    <Card withBorder radius="lg" padding="lg" data-testid={testId}>
      <Stack gap="md">
        <div>
          <Title order={3}>{title}</Title>
          {description && (
            <Text size="sm" c="dimmed" mt={4}>
              {description}
            </Text>
          )}
        </div>
        {children}
      </Stack>
    </Card>
  );
}
