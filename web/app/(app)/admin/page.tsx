'use client';

import { Card, Text, Stack, Group, Badge, Loader } from '@mantine/core';
import {
  IconServer,
  IconPlugConnected,
  IconBook,
  IconAdjustments,
  IconReceipt,
  IconShieldCheck,
} from '@tabler/icons-react';
import Link from 'next/link';
import { PageHeader } from '@/components/shared/PageHeader';
import { useWorkflowEngineStatus } from '@/lib/hooks/use-workflow-engine';
import { useInferenceProviders } from '@/lib/hooks/use-inference-providers';
import { useSkills } from '@/lib/hooks/use-skills';
import { formatDate } from '@/lib/utils/format';

export default function AdminOverviewPage() {
  const { data: engineStatus, isLoading, error } = useWorkflowEngineStatus();
  const { data: providers, isLoading: providersLoading, error: providersError } = useInferenceProviders();
  const defaultProvider = providers?.find((p) => p.isDefault && p.capability === 'Chat');
  const { data: skills, isLoading: skillsLoading, error: skillsError } = useSkills();

  return (
    <>
      <PageHeader
        title="Admin Overview"
        breadcrumbs={[{ label: 'Admin' }, { label: 'Overview' }]}
      />
      <Stack gap="md" maw={600}>
        <Card
          withBorder
          padding="md"
          radius="md"
          component={Link}
          href="/admin/workflow-service"
          style={{ cursor: 'var(--rb-cursor-pointer)' }}
        >
          <Group justify="space-between" mb="sm">
            <Group gap="sm">
              <IconServer size={20} />
              <Text fw={600}>Workflow Engine</Text>
            </Group>
            {isLoading ? (
              <Loader size="xs" />
            ) : error ? (
              <Badge color="red" variant="filled">Unreachable</Badge>
            ) : (
              <Badge color="green" variant="filled">Healthy</Badge>
            )}
          </Group>
          {engineStatus && (
            <Stack gap={4}>
              <Text size="sm" c="dimmed">Service: {engineStatus.service}</Text>
              <Text size="sm" c="dimmed">Status: {engineStatus.status}</Text>
              <Text size="sm" c="dimmed">Last checked: {formatDate(engineStatus.timestamp)}</Text>
            </Stack>
          )}
          {error && !isLoading && (
            <Text size="sm" c="red">Could not connect to the Workflow Engine service.</Text>
          )}
          <Text size="sm" mt="sm" c="var(--mantine-color-anchor)">Open live command center</Text>
        </Card>

        <Card
          withBorder
          padding="md"
          radius="md"
          component={Link}
          href="/admin/inference-providers"
          style={{ cursor: 'var(--rb-cursor-pointer)' }}
        >
          <Group justify="space-between" mb="sm">
            <Group gap="sm">
              <IconPlugConnected size={20} />
              <Text fw={600}>Default Inference Provider</Text>
            </Group>
            {providersLoading ? (
              <Loader size="xs" />
            ) : providersError ? (
              <Badge color="red" variant="filled">Unreachable</Badge>
            ) : !defaultProvider ? (
              <Badge color="yellow" variant="filled">Not configured</Badge>
            ) : defaultProvider.lastTestOk === false ? (
              <Badge color="red" variant="filled">Last test failed</Badge>
            ) : (
              <Badge color="green" variant="filled">Configured</Badge>
            )}
          </Group>
          {defaultProvider && (
            <Stack gap={4}>
              <Text size="sm" c="dimmed">Name: {defaultProvider.name}</Text>
              <Text size="sm" c="dimmed">
                Kind: {defaultProvider.kind === 'AzureOpenAI' ? 'Azure OpenAI' : 'OpenAI-compatible'}
              </Text>
              <Text size="sm" c="dimmed">
                Last test: {defaultProvider.lastTestAt
                  ? `${defaultProvider.lastTestOk ? 'OK' : 'Failed'} (${formatDate(defaultProvider.lastTestAt)})`
                  : 'Never tested'}
              </Text>
            </Stack>
          )}
          {providersError && !providersLoading && (
            <Text size="sm" c="red">Could not connect to the Inference API.</Text>
          )}
          {!providersLoading && !providersError && !defaultProvider && (
            <Text size="sm" c="dimmed">
              No default provider set. Agents fall back to the AzureOpenAI:* config keys.
            </Text>
          )}
          <Text size="sm" mt="sm" c="var(--mantine-color-anchor)">Manage inference providers</Text>
        </Card>

        <Card
          withBorder
          padding="md"
          radius="md"
          component={Link}
          href="/admin/skills"
          style={{ cursor: 'var(--rb-cursor-pointer)' }}
        >
          <Group justify="space-between" mb="sm">
            <Group gap="sm">
              <IconBook size={20} />
              <Text fw={600}>Skills</Text>
            </Group>
            {skillsLoading ? (
              <Loader size="xs" />
            ) : skillsError ? (
              <Badge color="red" variant="filled">Unreachable</Badge>
            ) : (
              <Badge color="blue" variant="light">{skills?.length ?? 0} registered</Badge>
            )}
          </Group>
          {skillsError && !skillsLoading && (
            <Text size="sm" c="red">Could not connect to the Inference API.</Text>
          )}
          {!skillsLoading && !skillsError && (
            <Text size="sm" c="dimmed">
              Assign skills to custom agents. Built-in agents get their skills from code.
            </Text>
          )}
          <Text size="sm" mt="sm" c="var(--mantine-color-anchor)">Browse skills</Text>
        </Card>

        <Card
          withBorder
          padding="md"
          radius="md"
          component={Link}
          href="/admin/billing"
          style={{ cursor: 'var(--rb-cursor-pointer)' }}
        >
          <Group justify="space-between" mb="sm">
            <Group gap="sm">
              <IconReceipt size={20} />
              <Text fw={600}>Billing & Rate Cards</Text>
            </Group>
            <Badge color="gray" variant="light">Admin</Badge>
          </Group>
          <Text size="sm" c="dimmed">
            Revenue and cost the ledger supports, margin per plan, and the rate cards and multipliers that
            price every billable unit.
          </Text>
          <Text size="sm" mt="sm" c="var(--mantine-color-anchor)">Open billing &amp; rate cards</Text>
        </Card>

        <Card
          withBorder
          padding="md"
          radius="md"
          component={Link}
          href="/admin/calibration"
          style={{ cursor: 'var(--rb-cursor-pointer)' }}
        >
          <Group justify="space-between" mb="sm">
            <Group gap="sm">
              <IconAdjustments size={20} />
              <Text fw={600}>Decision Calibration</Text>
            </Group>
            <Badge color="gray" variant="light">Read-only</Badge>
          </Group>
          <Text size="sm" c="dimmed">
            Reliability, agreement and escalation rates for recorded decision observations, with the
            thresholds they are read at.
          </Text>
          <Text size="sm" mt="sm" c="var(--mantine-color-anchor)">Open calibration view</Text>
        </Card>

        <Card
          withBorder
          padding="md"
          radius="md"
          component={Link}
          href="/admin/moderation"
          style={{ cursor: 'var(--rb-cursor-pointer)' }}
        >
          <Group justify="space-between" mb="sm">
            <Group gap="sm">
              <IconShieldCheck size={20} />
              <Text fw={600}>Content Moderation</Text>
            </Group>
            <Badge color="gray" variant="light">Review only</Badge>
          </Group>
          <Text size="sm" c="dimmed">
            Pending mid-band moderation decisions and the thresholds that sent them here. Acknowledging
            records that an operator has seen an item; it changes no user content.
          </Text>
          <Text size="sm" mt="sm" c="var(--mantine-color-anchor)">Open moderation queue</Text>
        </Card>
      </Stack>
    </>
  );
}
