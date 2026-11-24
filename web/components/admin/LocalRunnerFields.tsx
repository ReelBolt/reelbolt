'use client';

import { Alert, Anchor, Autocomplete, Select, Stack, Text } from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import Link from 'next/link';
import { useOrgRunnerLlm } from '@/lib/hooks/use-runners';
import type { RunnerLlmDevice } from '@/lib/types/runner';

export const NO_SHARING_MESSAGE =
  'No computer is sharing a model yet. Open the ReelBolt desktop app, go to Local model, and switch sharing on.';

export const LOCAL_RUNNER_NOTICE =
  'Requests go through the ReelBolt desktop app to the model on that computer. The computer must be on and connected. No credits are used, and your members’ prompts are sent to that computer.';

/** The label a device shows in the picker, with its online and sharing state. */
export function deviceLabel(d: RunnerLlmDevice): string {
  const state = !d.online ? 'offline' : d.enabled ? 'online, sharing' : 'online, not sharing';
  return `${d.name} (${state})`;
}

interface LocalRunnerFieldsProps {
  deviceId: string;
  modelName: string;
  onDeviceChange: (deviceId: string) => void;
  onModelChange: (modelName: string) => void;
  /** Edit mode: the computer is fixed and models are managed elsewhere. */
  showModel: boolean;
  deviceError?: React.ReactNode;
  modelError?: React.ReactNode;
}

/** Replaces the endpoint / API-key / model fields when the LocalRunner kind is chosen. */
export function LocalRunnerFields({
  deviceId, modelName, onDeviceChange, onModelChange, showModel, deviceError, modelError,
}: LocalRunnerFieldsProps) {
  const { data, isLoading } = useOrgRunnerLlm();
  const devices = data?.devices ?? [];
  const selected = devices.find((d) => d.deviceId === deviceId);

  return (
    <Stack gap="sm">
      {!isLoading && devices.length === 0 && (
        <Alert color="yellow" variant="light" icon={<IconInfoCircle size={16} />}>
          <Text size="sm">{NO_SHARING_MESSAGE}</Text>
          <Anchor component={Link} href="/runners" size="sm" mt={4} display="inline-block">
            Runners
          </Anchor>
        </Alert>
      )}
      <Select
        label="Computer"
        placeholder={isLoading ? 'Loading...' : 'Choose a computer'}
        data={devices.map((d) => ({ value: d.deviceId, label: deviceLabel(d) }))}
        value={deviceId || null}
        onChange={(v) => onDeviceChange(v ?? '')}
        allowDeselect={false}
        error={deviceError}
        nothingFoundMessage="No computer is sharing a model"
      />
      {showModel && (
        <Autocomplete
          label="Model name"
          placeholder="llama3.2"
          description="Pick one the computer reports, or type the model name."
          data={selected?.models ?? []}
          value={modelName}
          onChange={onModelChange}
          error={modelError}
        />
      )}
      <Text size="sm" c="dimmed">{LOCAL_RUNNER_NOTICE}</Text>
    </Stack>
  );
}
