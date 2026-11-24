'use client';

import { useState } from 'react';
import { Button, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { IconPhotoCog } from '@tabler/icons-react';
import { createImageToolModels, type ProviderScope } from '@/lib/api/inference-providers';
import type { InferenceProvider } from '@/lib/types/inference-provider';

/**
 * One click to give every picture-editor tool its own fal.ai model under THIS provider: each AI tool
 * (remove objects, fill, expand, background, relight, upscale, edit, create) resolves a capability of
 * its own. The models share the provider's key, so nothing is copied. Models that already exist and
 * defaults already chosen are left alone (the API decides; this only reports what it created).
 * Renders nothing for a provider that is not fal.ai.
 */
export function FalImageToolsButton({
  provider,
  scope = 'platform',
  onDone,
}: {
  provider: InferenceProvider;
  scope?: ProviderScope;
  onDone?: () => void;
}) {
  const tAll = useT();
  const [busy, setBusy] = useState(false);
  if (provider.kind !== 'FalAi') return null;

  const run = async () => {
    setBusy(true);
    try {
      const created = await createImageToolModels(provider.id, scope);
      notifications.show(
        created.length === 0
          ? {
              title: 'Nothing to add',
              message: 'Every picture tool already has a fal.ai model.',
              color: 'blue',
            }
          : {
              title: 'Picture tools set up',
              message: `Added ${created.length} model${created.length === 1 ? '' : 's'} under this provider: ${created
                .map((m) => m.displayName)
                .join(', ')}. Test each one from its capability row below.`,
              color: 'green',
            },
      );
      onDone?.();
    } catch (err: unknown) {
      notifications.show({
        title: 'The picture tools could not be set up',
        message: describeError(tAll, err, 'Nothing was added. Try again in a moment.'),
        color: 'red',
      });
    } finally {
      setBusy(false);
    }
  };

  return (
    <Tooltip
      label="Adds a model under this provider for each picture-editor tool that has none. They all use this provider's key."
      multiline
      w={260}
    >
      <Button variant="default" mih={{ base: 40, sm: 0 }} leftSection={<IconPhotoCog size={16} />} onClick={run} loading={busy}>
        Set up all picture tools
      </Button>
    </Tooltip>
  );
}
