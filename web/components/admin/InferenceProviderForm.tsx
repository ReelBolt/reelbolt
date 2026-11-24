'use client';

import { useEffect, useState } from 'react';
import { useMediaQuery } from '@mantine/hooks';
import {
  Alert, Anchor, Autocomplete, Button, Checkbox, Group, Modal, NumberInput, Select, Stack, Switch, Text, TextInput,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { IconAlertTriangle, IconInfoCircle, IconPlugConnected } from '@tabler/icons-react';
import Link from 'next/link';
import {
  createProvider,
  isByoNotInPlan,
  testProviderConfig,
  updateProvider,
  type ProviderScope,
} from '@/lib/api/inference-providers';
import type {
  InferenceProvider,
  InferenceProviderKind,
  SelectableCapability,
} from '@/lib/types/inference-provider';
import {
  CAPABILITY_HINTS,
  DEFAULT_NOUN,
  KIND_DEFAULT_ENDPOINT,
  KIND_FIELD_HINTS,
  kindOptionsForScope,
  capabilityOptionsForKind,
  fallbackCapability,
  modelPlaceholderFor,
  modelSuggestions,
  unsupportedCapabilities,
} from './provider-catalog';
import { LocalRunnerFields } from './LocalRunnerFields';

/** Where the workspace's plans are listed and changed (A17-UI): Plan & billing, not Usage. */
const PLANS_AND_CREDITS_HREF = '/settings/billing';

interface InferenceProviderFormProps {
  opened: boolean;
  onClose: () => void;
  onSuccess: () => void;
  /**
   * The provider ACCOUNT being edited, or nothing to create one. Creating also adds the provider's
   * first model and capability (the contract allows a new provider to carry its first models), so a
   * new provider is usable the moment it is saved. Editing changes only the account: models and
   * capabilities are managed on the provider's page.
   */
  provider?: InferenceProvider;
  /**
   * Which set of providers this form writes. `org` targets the active workspace's own providers,
   * which adds two rules the platform scope does not have: no `env:` ambient-credential sentinel and
   * no PersonalOAuthMode — a workspace could otherwise aim the endpoint at its own server and
   * capture the deployment's credential — and no private-address endpoints. The API enforces all
   * three; the form does not offer them.
   */
  scope?: ProviderScope;
}

interface FormValues {
  name: string;
  kind: InferenceProviderKind;
  capability: SelectableCapability;
  endpoint: string;
  modelName: string;
  displayName: string;
  apiKey: string;
  clearApiKey: boolean;
  isDefault: boolean;
  isEnabled: boolean;
  timeoutSeconds: number | string | undefined;
  personalOAuthMode: boolean;
}

function valuesFor(provider: InferenceProvider | undefined): FormValues {
  return {
    name: provider?.name || '',
    kind: provider?.kind || 'AzureOpenAI',
    capability: 'Chat',
    endpoint: provider?.endpoint || '',
    modelName: '',
    displayName: '',
    apiKey: '',
    clearApiKey: false,
    isDefault: false,
    isEnabled: provider?.isEnabled ?? true,
    timeoutSeconds: provider?.timeoutSeconds ?? undefined,
    personalOAuthMode: provider?.personalOAuthMode || false,
  };
}

/** A NumberInput value as the API's optional whole seconds. */
function timeoutOf(value: number | string | undefined): number | undefined {
  const n = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(n) && n > 0 ? n : undefined;
}

export function InferenceProviderForm({ opened, onClose, onSuccess, provider, scope = 'platform' }: InferenceProviderFormProps) {
  const tAll = useT();
  const [loading, setLoading] = useState(false);
  const [testing, setTesting] = useState(false);
  // A 402 `byo_not_in_plan` from this scope's API: the workspace's plan does not include bringing
  // its own provider. Presented as a plan limit with the upgrade path, never as a red error.
  const [planLimited, setPlanLimited] = useState(false);
  const isEdit = !!provider;
  const isOrg = scope === 'org';

  const form = useForm<FormValues>({
    initialValues: valuesFor(provider),
    validate: {
      name: (v) => (!v.trim() ? 'Name is required' : null),
      endpoint: (v) => (!v.trim() ? 'This field is required' : null),
      // Only a new provider carries a model; editing an account never touches its models.
      modelName: (v) => (!isEdit && !v.trim() ? 'This field is required' : null),
    },
  });

  // `initialValues` are captured once at mount, so re-seed from the current `provider` every time
  // the modal opens: a reopen always shows the latest saved data.
  useEffect(() => {
    if (opened) {
      form.setValues(valuesFor(provider));
      form.resetDirty();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, provider]);

  // Full screen on a phone so the form is not a cramped card with the keyboard over it.
  const isMobile = useMediaQuery('(max-width: 47.99375em)');

  const { kind, capability } = form.values;
  const { endpointLabel, modelLabel } = KIND_FIELD_HINTS[kind] ?? KIND_FIELD_HINTS.OpenAICompatible;
  const modelPlaceholder = modelPlaceholderFor(kind, capability);
  const isLocalRunner = kind === 'LocalRunner';
  const modelCount = provider?.models.length ?? 0;

  // Anthropic, Gemini and DeepSeek reject a blank key rather than falling back to the container's
  // environment, so the `env:` sentinel is the only way to ask for that. A WORKSPACE provider never
  // gets that option: the sentinel resolves to the DEPLOYMENT's credential while the endpoint
  // belongs to the workspace.
  const apiKeyDescription = [
    isEdit ? 'Leave blank to keep the current key.' : null,
    isOrg ? 'Stored encrypted. ReelBolt sends it only to this endpoint, never to a shared deployment credential.' : null,
    !isOrg && kind === 'Anthropic'
      ? 'Enter env: to use the ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN environment variables instead of storing a key.'
      : null,
    !isOrg && kind === 'Gemini'
      ? 'Enter env: to use the GEMINI_API_KEY / GOOGLE_API_KEY environment variables instead of storing a key.'
      : null,
    !isOrg && kind === 'DeepSeek'
      ? 'Enter env: to use the DEEPSEEK_API_KEY environment variable instead of storing a key.'
      : null,
    kind === 'Higgsfield'
      ? 'Higgsfield credentials are two-part: enter them as ID:SECRET, joining the Key ID and Key Secret from your Higgsfield account with a colon. The whole ID:SECRET string is stored as this provider\'s key — it is not a single opaque token.'
      : null,
    kind === 'ComfyUI'
      ? 'Optional. A stock ComfyUI server has no authentication; enter a token only if a reverse proxy in front of it expects an Authorization: Bearer header. Blank sends none.'
      : null,
  ]
    .filter(Boolean)
    .join(' ') || undefined;

  // Switching kind can invalidate the selected capability; `fallbackCapability` answers with one
  // the new kind serves, so the Select never shows a value missing from its own options. The
  // endpoint is prefilled for kinds with a fixed vendor URL, and only a URL we prefilled ourselves
  // is cleared on the way out — never one the user typed.
  const handleKindChange = (value: string | null) => {
    if (!value) return;
    const next = value as InferenceProviderKind;
    form.setFieldValue('kind', next);

    if (unsupportedCapabilities(next).includes(form.values.capability)) {
      form.setFieldValue('capability', fallbackCapability(next));
    }
    // The backend rejects PersonalOAuthMode on any non-Anthropic kind.
    if (next !== 'Anthropic' && form.values.personalOAuthMode) {
      form.setFieldValue('personalOAuthMode', false);
    }

    // A device id is not a URL (and a URL is not a device id): never carry one into the other.
    if (next === 'LocalRunner' || form.values.kind === 'LocalRunner') {
      form.setFieldValue('endpoint', KIND_DEFAULT_ENDPOINT[next] ?? '');
      form.setFieldValue('modelName', '');
      form.setFieldValue('apiKey', '');
      return;
    }

    const current = form.values.endpoint.trim();
    const ours = !current || Object.values(KIND_DEFAULT_ENDPOINT).includes(current);
    if (ours) form.setFieldValue('endpoint', KIND_DEFAULT_ENDPOINT[next] ?? '');
  };

  const handleClose = () => {
    setPlanLimited(false);
    onClose();
  };

  // Editing an account has no model on the form, so its test runs against the provider's first
  // capability — enough to prove a new key or endpoint answers before it is saved.
  const firstModel = provider?.models.find((m) => m.capabilities.some((c) => c.capability !== 'Compute'));
  const firstCapability = firstModel?.capabilities.find((c) => c.capability !== 'Compute');
  const testModelCode = isEdit ? firstModel?.modelCode ?? '' : form.values.modelName;
  const testCapability = isEdit ? firstCapability?.capability : capability;

  const handleTest = async () => {
    if (!testCapability) return;
    setTesting(true);
    setPlanLimited(false);
    try {
      const result = await testProviderConfig(
        {
          kind,
          endpoint: form.values.endpoint,
          apiKey: !isLocalRunner && form.values.apiKey ? form.values.apiKey : undefined,
          modelCode: testModelCode,
          capability: testCapability,
          personalOAuthMode: !isOrg && kind === 'Anthropic' ? form.values.personalOAuthMode : undefined,
          providerId: provider?.id,
        },
        scope,
      );
      notifications.show(
        result.ok
          ? { title: 'Connection OK', message: `Responded in ${result.latencyMs}ms`, color: 'green' }
          : { title: 'Connection failed', message: result.error || 'The provider did not respond successfully.', color: 'red' },
      );
    } catch (err: unknown) {
      if (isByoNotInPlan(err)) {
        setPlanLimited(true);
        return;
      }
      notifications.show({ title: 'Error', message: describeError(tAll, err, 'Test failed'), color: 'red' });
    } finally {
      setTesting(false);
    }
  };

  const handleSubmit = form.onSubmit(async (values) => {
    setLoading(true);
    setPlanLimited(false);
    try {
      // A workspace provider never carries PersonalOAuthMode, and the API would reject a true.
      const personalOAuthMode = !isOrg && values.kind === 'Anthropic' ? values.personalOAuthMode : false;
      if (isEdit) {
        await updateProvider(provider.id, {
          name: values.name,
          endpoint: values.endpoint,
          // Omitted keeps the stored key, a value replaces it, and "" clears it.
          apiKey: values.clearApiKey ? '' : values.apiKey ? values.apiKey : undefined,
          timeoutSeconds: timeoutOf(values.timeoutSeconds),
          isEnabled: values.isEnabled,
          personalOAuthMode,
        }, scope);
        notifications.show({ title: 'Updated', message: 'Provider updated', color: 'green' });
      } else {
        const modelCode = values.modelName.trim();
        await createProvider({
          name: values.name,
          kind: values.kind,
          endpoint: values.endpoint,
          apiKey: values.kind !== 'LocalRunner' && values.apiKey ? values.apiKey : undefined,
          timeoutSeconds: timeoutOf(values.timeoutSeconds),
          isEnabled: values.isEnabled,
          personalOAuthMode,
          models: [
            {
              modelCode,
              displayName: values.displayName.trim() || modelCode,
              isEnabled: true,
              capabilities: [{ capability: values.capability, isDefault: values.isDefault, isEnabled: true }],
            },
          ],
        }, scope);
        notifications.show({ title: 'Created', message: 'Provider created', color: 'green' });
      }
      onSuccess();
      onClose();
    } catch (err: unknown) {
      // A 402 is the plan, not the request: keep the modal open and say what to do about it.
      if (isByoNotInPlan(err)) {
        setPlanLimited(true);
        return;
      }
      notifications.show({
        title: 'Error',
        message: describeError(tAll, err, 'Operation failed'),
        color: 'red',
      });
    } finally {
      setLoading(false);
    }
  });

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title={
        isEdit
          ? isOrg ? 'Edit workspace provider' : 'Edit Provider'
          : isOrg ? 'New workspace provider' : 'New Provider'
      }
      centered
      fullScreen={isMobile}
    >
      <form onSubmit={handleSubmit}>
        <Stack>
          {planLimited && (
            <Alert
              icon={<IconInfoCircle size={16} />}
              color="yellow"
              variant="light"
              title="Your plan does not include your own inference provider"
            >
              <Text size="sm">
                Bringing your own model provider is part of a paid plan. Your workspace can add one as
                soon as the plan includes it. Nothing else about your workspace changes.
              </Text>
              <Anchor component={Link} href={PLANS_AND_CREDITS_HREF} size="sm" mt={4} display="inline-block">
                See plans and credits
              </Anchor>
            </Alert>
          )}
          {isOrg && !isLocalRunner && (
            <Text size="xs" c="dimmed">
              This provider belongs to your workspace and is charged to it, not to ReelBolt. It must be
              a public endpoint: internal and private addresses are not allowed for a workspace
              provider.
            </Text>
          )}
          {isEdit && (
            <Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />}>
              <Text size="sm">
                A change to the key or the endpoint applies to every model under this provider
                {modelCount > 0 ? ` (${modelCount} model${modelCount === 1 ? '' : 's'})` : ''}.
              </Text>
            </Alert>
          )}
          <TextInput label="Name" placeholder="Production Azure" {...form.getInputProps('name')} />
          <Select
            label="Kind"
            data={kindOptionsForScope(scope)}
            allowDeselect={false}
            maxDropdownHeight={240}
            disabled={isEdit}
            description={isEdit ? 'Fixed when the provider was created.' : undefined}
            {...form.getInputProps('kind')}
            onChange={handleKindChange}
          />
          {!isEdit && (
            <Select
              label="Capability"
              description={`${CAPABILITY_HINTS[capability] ?? ''} Each capability has its own independent default.`}
              data={capabilityOptionsForKind(kind)}
              allowDeselect={false}
              maxDropdownHeight={240}
              {...form.getInputProps('capability')}
            />
          )}
          {isLocalRunner && (
            <LocalRunnerFields
              deviceId={form.values.endpoint}
              modelName={form.values.modelName}
              onDeviceChange={(id) => form.setFieldValue('endpoint', id)}
              onModelChange={(m) => form.setFieldValue('modelName', m)}
              showModel={!isEdit}
              deviceError={form.errors.endpoint}
              modelError={form.errors.modelName}
            />
          )}
          {!isLocalRunner && (
          <TextInput
            label={endpointLabel}
            placeholder={(KIND_FIELD_HINTS[kind] ?? KIND_FIELD_HINTS.OpenAICompatible).endpointPlaceholder}
            description={
              // A workspace provider is never allowed a private or loopback endpoint, so the
              // local-server hints below are platform-scope only.
              isOrg
                ? 'A public https:// URL. Internal, private and loopback addresses are not allowed for a workspace provider.'
                : kind === 'FishAudio'
                  ? 'Cloud: https://api.fish.audio. Local fish-speech server: http://host.docker.internal:8180 (containers reach the host through that name).'
                  : kind === 'OpenAICompatible'
                    ? 'Include the /v1 suffix, e.g. http://host.docker.internal:8000/v1.'
                    : kind === 'ComfyUI'
                      ? 'The server root, no path. The bundled compose service (gpu profile) is http://comfyui:8188; a server on the host is http://host.docker.internal:8188.'
                      : undefined
            }
            {...form.getInputProps('endpoint')}
          />
          )}
          {!isEdit && (
            <>
              {!isLocalRunner && (
              <Autocomplete
                label={modelLabel}
                placeholder={modelPlaceholder}
                data={modelSuggestions(kind, capability)}
                maxDropdownHeight={240}
                description={kind === 'FishAudio' ? 'Sent as the `model` header. A local server ignores it, but a value is still required.' : undefined}
                {...form.getInputProps('modelName')}
              />
              )}
              <TextInput
                label="Display name"
                placeholder={form.values.modelName.trim() || 'GPT-4o mini'}
                description="What people see when they choose this model. Blank uses the model code."
                {...form.getInputProps('displayName')}
              />
            </>
          )}
          {!isLocalRunner && (
          <TextInput
            label="API Key"
            type="password"
            placeholder={provider?.apiKeyLastFour ? `•••• ${provider.apiKeyLastFour}` : 'sk-...'}
            description={apiKeyDescription}
            disabled={form.values.clearApiKey}
            {...form.getInputProps('apiKey')}
          />
          )}
          {isEdit && !isLocalRunner && provider.hasApiKey && (
            <Checkbox
              label="Remove the stored key"
              description="Saves this provider with no key at all."
              {...form.getInputProps('clearApiKey', { type: 'checkbox' })}
            />
          )}
          {/* The licence claim that put Higgsfield on this list could not be verified against the
              vendor's own terms, so this sends the admin to that authority instead of repeating the
              claim as fact. */}
          {kind === 'Higgsfield' && (
            <Alert color="yellow" icon={<IconAlertTriangle size={16} />} title="Check Higgsfield's data terms before sending footage">
              {"Higgsfield's terms have been reported to grant the vendor a broad, perpetual licence over the inputs and outputs you send it, including for training and marketing. That specific clause could not be confirmed against Higgsfield's own current terms when this provider kind was added, so treat it as a caution rather than a statement of fact — read Higgsfield's terms of service yourself before sending footage that is not yours or that shows other people. A VideoGenerate step also requires source-media egress to be explicitly enabled before any frame of your footage leaves this host."}
            </Alert>
          )}
          <NumberInput
            label="Timeout (seconds)"
            placeholder="Default"
            min={1}
            {...form.getInputProps('timeoutSeconds')}
          />
          {/* Never for a workspace provider: a personal subscription credential is for its owner's
              own use, and the API rejects the pairing outright. */}
          {!isOrg && kind === 'Anthropic' && (
            <Switch
              label="Dev/OAuth (Personal use only)"
              description="This provider uses a personal Claude subscription credential (sk-ant-oat…, or env: resolving to ANTHROPIC_AUTH_TOKEN), not a Console API key. Adds the system-prompt prefix and headers Anthropic's OAuth validation requires. Do not enable this for a provider serving anyone other than the credential's owner."
              {...form.getInputProps('personalOAuthMode', { type: 'checkbox' })}
            />
          )}
          {kind === 'ComfyUI' && (
            <Text size="sm" c="dimmed">
              Self-hosted, so the server URL may be a private address (the guard that rejects those for
              cloud kinds is lifted for ComfyUI). Only the two vetted Wan 2.2 TI2V-5B workflow templates
              can ever be submitted — free-form graphs are refused — and the model weights are Apache-2.0
              licensed. LTX-2 is deliberately NOT offered: its Open Weights License limits free commercial
              use to entities under $10M annual revenue. A VideoGenerate step budgets this provider in GPU
              minutes, not dollars.
            </Text>
          )}
          {kind === 'FishAudio' && (
            <Text size="sm" c="dimmed">
              Fish Audio&apos;s hosted API, including the free `s2.1-pro-free` tier, has no documented
              non-commercial restriction, with rate and SLA limits as the only constraint. Self-hosting Fish&apos;s open-weight models is
              licensed under the Fish Audio Community License Agreement: free for research and non-commercial use and
              for limited commercial use below its revenue threshold; above it, and for a hosted service, a
              separate license from Fish Audio is required.
              Voice cloning (a project&apos;s &quot;Voices&quot; tab) sends a person&apos;s recordings to this endpoint
              and creates a private voice model there: it is biometric data, so every clone needs a recorded
              consent attestation, keeps a retention deadline, and is deleted from the vendor when the voice is
              deleted or expires. The same licence applies to Fish Audio&apos;s speech-to-text service.
              <br />
              <br />
              <b>Speech-to-text.</b> A Fish Audio model set to <b>Transcription (ASR)</b> calls the vendor&apos;s
              hosted <code>/v1/asr</code> with the model <code>transcribe-1</code> (or{' '}
              <code>transcribe-1-pro</code>). Unlike a self-hosted speech server, which may run without a key,
              this service always needs the API key filled in — an empty key is refused rather than sent
              unauthenticated. It returns each phrase with its start and end time, but <b>no per-word timings</b>,
              so voiceover word alignment is unavailable on it; keep a Whisper-compatible model if you need
              word-level caption timing.
            </Text>
          )}
          {!isEdit && kind === 'OpenAICompatible' && capability === 'SpeechSynthesis' && (
            <Text size="sm" c="dimmed">
              Speaks OpenAI&apos;s `/v1/audio/speech`. The bundled `whisper` (speaches) container serves it locally at
              `http://whisper:8000/v1` with the Apache-2.0 kokoro voices — commercially clean and free. A blank API key
              sends no Authorization header and never reads the host&apos;s environment; a hosted endpoint will then
              answer 401. Voice cloning is not available on this kind: only Fish Audio exposes a cloning API.
            </Text>
          )}
          {!isEdit && (
            <Switch
              label={`Set as default ${DEFAULT_NOUN[capability] ?? 'chat'} provider`}
              {...form.getInputProps('isDefault', { type: 'checkbox' })}
            />
          )}
          <Switch label="Enabled" {...form.getInputProps('isEnabled', { type: 'checkbox' })} />

          <Group justify="space-between" mt="sm" wrap="wrap">
            <Button
              variant="default"
              w={{ base: '100%', sm: 'auto' }} h={{ base: 40, sm: 36 }}
              leftSection={<IconPlugConnected size={16} />}
              onClick={handleTest}
              loading={testing}
              disabled={!form.values.endpoint || !testModelCode || !testCapability}
            >
              Test connection
            </Button>
            <Button type="submit" w={{ base: '100%', sm: 'auto' }} h={{ base: 40, sm: 36 }} loading={loading}>
              {isEdit ? 'Update' : 'Create'}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
