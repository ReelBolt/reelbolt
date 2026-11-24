'use client';

import { Group, Stack, Text, Title } from '@mantine/core';

/**
 * The heading each billing area opens with: its name, one sentence saying what it holds, and (when
 * the area can grow) the action that adds to it. Shared so the areas read as one page rather than
 * three unrelated tables.
 */
export function BillingSectionHeading({
  title,
  description,
  action,
}: {
  title: string;
  description: string;
  action?: React.ReactNode;
}) {
  return (
    <Group justify="space-between" align="flex-end" wrap="wrap">
      <Stack gap={2}>
        <Title order={4}>{title}</Title>
        <Text size="sm" c="dimmed" maw={760}>
          {description}
        </Text>
      </Stack>
      {action}
    </Group>
  );
}
