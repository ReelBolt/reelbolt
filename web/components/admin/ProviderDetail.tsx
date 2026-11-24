'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { Alert, Badge, Button, Card, Center, Group, Loader, Stack, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useT, type T } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { IconEdit, IconInfoCircle, IconPlus, IconTrash } from '@tabler/icons-react';
import {
  deleteCapability,
  deleteModel,
  deleteProvider,
  retirePrice,
  testCapability,
  updateCapability,
  type ProviderScope,
} from '@/lib/api/inference-providers';
import { useProvider } from '@/lib/hooks/use-inference-providers';
import type { InferenceModel, ModelCapability, ModelCapabilityPrice } from '@/lib/types/inference-provider';
import { formatDate } from '@/lib/utils/format';
import { PageHeader } from '@/components/shared/PageHeader';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { AddCapabilityForm } from './AddCapabilityForm';
import { FalImageToolsButton } from './FalImageToolsButton';
import { InferenceProviderForm } from './InferenceProviderForm';
import { KIND_BADGE } from './InferenceProviderTable';
import { ModelForm } from './ModelForm';
import { PriceForm } from './PriceForm';
import { ProviderModels, type ProviderModelActions } from './ProviderModels';

type Crumb = { label: string; href?: string };

type Pending =
  | { kind: 'deleteProvider' }
  | { kind: 'deleteModel'; model: InferenceModel }
  | { kind: 'deleteCapability'; model: InferenceModel; cap: ModelCapability }
  | { kind: 'retirePrice'; model: InferenceModel; cap: ModelCapability; price: ModelCapabilityPrice };

const errorText = (t: T, err: unknown, fallback: string) => describeError(t, err, fallback);

/** >=40px tap height on a phone, Mantine's default button height from `sm` up. */
const TAP_H = { base: 40, sm: 36 } as const;

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Group align="flex-start">
      <Text size="sm" fw={500} w={{ base: '100%', sm: 140 }}>{label}</Text>
      {children}
    </Group>
  );
}

/**
 * One provider ACCOUNT and everything under it: the account (kind, endpoint, key, timeout, enabled),
 * its models, each model's capabilities (default, enabled, test) and — on the platform scope only —
 * each capability's prices.
 *
 * Shared by the platform admin's page and the workspace's: the two differ only in `scope`, which
 * picks the API, and in that a workspace page never renders prices or calls a price route.
 */
export function ProviderDetail({
  id,
  scope,
  breadcrumbs,
  listHref,
}: {
  id: string;
  scope: ProviderScope;
  /** The trail above the provider's own name. */
  breadcrumbs: Crumb[];
  listHref: string;
}) {
  const router = useRouter();
  const tAll = useT();
  const { data: provider, isLoading, mutate } = useProvider(id, scope);
  const [editOpened, setEditOpened] = useState(false);
  const [modelForm, setModelForm] = useState<{ model?: InferenceModel } | null>(null);
  const [capabilityTarget, setCapabilityTarget] = useState<InferenceModel | null>(null);
  const [priceTarget, setPriceTarget] = useState<{ model: InferenceModel; cap: ModelCapability } | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [busyCapabilityId, setBusyCapabilityId] = useState<string | null>(null);

  const crumbs = (name: string) => [...breadcrumbs, { label: name }];

  if (isLoading) {
    return (
      <>
        <PageHeader title="Provider" breadcrumbs={crumbs('Provider')} />
        <Center h={300}><Loader /></Center>
      </>
    );
  }
  if (!provider) return <Text>Provider not found</Text>;

  const refresh = () => void mutate();
  const locked = provider.isSystem;

  const actions: ProviderModelActions = {
    editModel: (model) => setModelForm({ model }),
    deleteModel: (model) => setPending({ kind: 'deleteModel', model }),
    addCapability: (model) => setCapabilityTarget(model),
    deleteCapability: (model, cap) => setPending({ kind: 'deleteCapability', model, cap }),
    updateCapability: async (model, cap, change) => {
      setBusyCapabilityId(cap.id);
      try {
        await updateCapability(provider.id, model.id, cap.id, change, scope);
        refresh();
      } catch (err: unknown) {
        notifications.show({ title: 'Error', message: errorText(tAll, err, 'The capability could not be changed'), color: 'red' });
      } finally {
        setBusyCapabilityId(null);
      }
    },
    testCapability: async (model, cap) => {
      setBusyCapabilityId(cap.id);
      try {
        const result = await testCapability(provider.id, model.id, cap.id, scope);
        refresh();
        notifications.show(
          result.ok
            ? { title: 'Connection OK', message: `${model.displayName} responded in ${result.latencyMs}ms`, color: 'green' }
            : { title: 'Connection failed', message: result.error || 'The provider did not respond successfully.', color: 'red' },
        );
      } catch (err: unknown) {
        notifications.show({ title: 'Error', message: errorText(tAll, err, 'Test failed'), color: 'red' });
      } finally {
        setBusyCapabilityId(null);
      }
    },
    pricing:
      scope === 'platform'
        ? {
            now: new Date(),
            addPrice: (model, cap) => setPriceTarget({ model, cap }),
            retirePrice: (model, cap, price) => setPending({ kind: 'retirePrice', model, cap, price }),
          }
        : undefined,
  };

  const confirm = async () => {
    if (!pending) return;
    setConfirming(true);
    try {
      switch (pending.kind) {
        case 'deleteProvider':
          await deleteProvider(provider.id, scope);
          notifications.show({ title: 'Deleted', message: 'Provider deleted', color: 'green' });
          router.push(listHref);
          return;
        case 'deleteModel':
          await deleteModel(provider.id, pending.model.id, scope);
          notifications.show({ title: 'Model deleted', message: pending.model.displayName, color: 'green' });
          break;
        case 'deleteCapability':
          await deleteCapability(provider.id, pending.model.id, pending.cap.id, scope);
          notifications.show({ title: 'Capability removed', message: `${pending.model.displayName} · ${pending.cap.capability}`, color: 'green' });
          break;
        case 'retirePrice':
          await retirePrice(provider.id, pending.model.id, pending.cap.id, pending.price.id, new Date().toISOString());
          notifications.show({
            title: 'Price retired',
            message: 'It ends now, so that unit is unpriced from this instant unless another price covers it.',
            color: 'green',
          });
          break;
      }
      refresh();
    } catch (err: unknown) {
      notifications.show({ title: 'Error', message: errorText(tAll, err, 'The change was refused'), color: 'red' });
    } finally {
      setConfirming(false);
      setPending(null);
    }
  };

  const confirmText = (): { title: string; message: string; label: string } => {
    switch (pending?.kind) {
      case 'deleteProvider':
        return {
          title: 'Delete provider',
          label: 'Delete',
          message: `Delete ${provider.name} and every model under it? A provider that still holds a default cannot be deleted: choose another default first.`,
        };
      case 'deleteModel':
        return {
          title: 'Delete model',
          label: 'Delete',
          message: `Delete ${pending.model.displayName} and its capabilities? Steps and agents still pointing at it fall back to the default.`,
        };
      case 'deleteCapability':
        return {
          title: 'Remove capability',
          label: 'Remove',
          message: `Stop ${pending.model.displayName} serving ${pending.cap.capability}? Steps and agents still pointing at it fall back to the default.`,
        };
      case 'retirePrice':
        return {
          title: 'Retire price',
          label: 'Retire',
          message: `End the ${pending.price.unit}${pending.price.variant ? ` (${pending.price.variant})` : ''} price of ${pending.model.displayName} now? That unit is unpriced from this instant unless another price covers it.`,
        };
      default:
        return { title: '', message: '', label: 'Confirm' };
    }
  };
  const text = confirmText();

  return (
    <>
      <PageHeader title={provider.name} breadcrumbs={crumbs(provider.name)}>
        {!locked && (
          <>
            <FalImageToolsButton provider={provider} scope={scope} onDone={refresh} />
            <Button variant="default" h={TAP_H} leftSection={<IconEdit size={16} />} onClick={() => setEditOpened(true)}>
              Edit
            </Button>
            <Button color="red" variant="outline" h={TAP_H} leftSection={<IconTrash size={16} />} onClick={() => setPending({ kind: 'deleteProvider' })}>
              Delete
            </Button>
          </>
        )}
      </PageHeader>

      <Stack gap="lg">
        {locked && (
          <Alert color="gray" variant="light" icon={<IconInfoCircle size={16} />} title="System provider">
            The platform&apos;s own compute. It cannot be edited or deleted; only its prices can be changed.
          </Alert>
        )}

        <Card withBorder>
          <Stack gap="sm">
            <Field label="Kind">
              <Group gap="xs">
                <Badge color={KIND_BADGE[provider.kind]?.color ?? 'gray'} variant="light">
                  {KIND_BADGE[provider.kind]?.label ?? provider.kind}
                </Badge>
                {provider.isSystem && <Badge color="dark" variant="light">System</Badge>}
              </Group>
            </Field>
            <Field label="Endpoint">
              <Text size="sm" style={{ wordBreak: 'break-all' }}>{provider.endpoint}</Text>
            </Field>
            <Field label="API key">
              <Text size="sm">{provider.hasApiKey ? `•••• ${provider.apiKeyLastFour ?? ''}` : 'Not set'}</Text>
            </Field>
            <Field label="Status">
              <Group gap="xs">
                <Badge color={provider.isEnabled ? 'green' : 'gray'} variant="light">
                  {provider.isEnabled ? 'Enabled' : 'Disabled'}
                </Badge>
                {provider.personalOAuthMode && <Badge color="yellow" variant="light">Personal OAuth</Badge>}
              </Group>
            </Field>
            <Field label="Timeout">
              <Text size="sm">{provider.timeoutSeconds ? `${provider.timeoutSeconds}s` : 'Default'}</Text>
            </Field>
            <Field label="Updated">
              <Text size="sm">{formatDate(provider.updatedAt)}</Text>
            </Field>
            {!locked && (
              <Text size="xs" c="dimmed">
                The key and the endpoint belong to the provider: changing either applies to every model below.
              </Text>
            )}
          </Stack>
        </Card>

        <Stack gap="sm">
          <Group justify="space-between" wrap="wrap" gap="sm">
            <Title order={4}>Models</Title>
            {!locked && (
              <Button variant="light" h={TAP_H} leftSection={<IconPlus size={16} />} onClick={() => setModelForm({})}>
                Add model
              </Button>
            )}
          </Group>
          <ProviderModels models={provider.models} busyCapabilityId={busyCapabilityId} locked={locked} actions={actions} />
        </Stack>
      </Stack>

      {!locked && (
        <InferenceProviderForm
          opened={editOpened}
          onClose={() => setEditOpened(false)}
          onSuccess={refresh}
          provider={provider}
          scope={scope}
        />
      )}
      {modelForm && (
        <ModelForm
          opened
          onClose={() => setModelForm(null)}
          onSuccess={refresh}
          provider={provider}
          model={modelForm.model}
          scope={scope}
        />
      )}
      {capabilityTarget && (
        <AddCapabilityForm
          opened
          onClose={() => setCapabilityTarget(null)}
          onSuccess={refresh}
          provider={provider}
          model={capabilityTarget}
          scope={scope}
        />
      )}
      {scope === 'platform' && priceTarget && (
        <PriceForm
          onClose={() => setPriceTarget(null)}
          onSuccess={refresh}
          provider={provider}
          model={priceTarget.model}
          capability={priceTarget.cap}
        />
      )}
      <ConfirmModal
        opened={!!pending}
        onClose={() => setPending(null)}
        onConfirm={confirm}
        title={text.title}
        message={text.message}
        confirmLabel={text.label}
        loading={confirming}
      />
    </>
  );
}
