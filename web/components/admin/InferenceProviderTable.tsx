'use client';

import { Table, Badge, ActionIcon, Anchor, Group, Text, Tooltip } from '@mantine/core';
import { IconEdit, IconTrash } from '@tabler/icons-react';
import Link from 'next/link';
import type { ReactNode } from 'react';
import type { ProviderScope } from '@/lib/api/inference-providers';
import type { InferenceProvider, InferenceProviderKind, InferenceProviderCapability } from '@/lib/types/inference-provider';

/**
 * Badge styling per provider kind. A lookup rather than a ternary so a kind added to the backend
 * enum but missed here degrades to its raw name, instead of being silently mislabelled as whatever
 * sat on the ternary's false branch.
 */
export const KIND_BADGE: Record<InferenceProviderKind, { label: string; color: string }> = {
  AzureOpenAI: { label: 'Azure OpenAI', color: 'blue' },
  OpenAICompatible: { label: 'OpenAI-compatible', color: 'grape' },
  Anthropic: { label: 'Anthropic', color: 'orange' },
  Gemini: { label: 'Google Gemini', color: 'teal' },
  DeepSeek: { label: 'DeepSeek', color: 'cyan' },
  TypeSafe: { label: 'TypeSafe AI', color: 'red' },
  MiniMax: { label: 'MiniMax', color: 'indigo' },
  FishAudio: { label: 'Fish Audio', color: 'violet' },
  BlackForestLabs: { label: 'Black Forest Labs', color: 'pink' },
  FalAi: { label: 'fal.ai', color: 'lime' },
  Higgsfield: { label: 'Higgsfield', color: 'violet' },
  ComfyUI: { label: 'ComfyUI', color: 'lime' },
  ElevenLabs: { label: 'ElevenLabs', color: 'dark' },
  LocalRunner: { label: 'On my computer', color: 'teal' },
};

/**
 * Badge styling per capability. An exhaustive Record so SpeechSynthesis gets its own color
 * rather than falling into a default branch.
 */
export const CAPABILITY_BADGE: Record<InferenceProviderCapability, string> = {
  Chat: 'indigo',
  Transcription: 'teal',
  Vision: 'orange',
  Decision: 'indigo',
  SpeechSynthesis: 'lime',
  VideoGeneration: 'pink',
  Embedding: 'cyan',
  ImageGeneration: 'grape',
  ImageEdit: 'blue',
  Segmentation: 'yellow',
  Depth: 'green',
  // The picture editor's per-job capabilities were split out of ImageEdit, so they share its colour and
  // read as one group in the table.
  ImageErase: 'blue',
  ImageFill: 'blue',
  ImageExpand: 'blue',
  ImageBackgroundReplace: 'blue',
  ImageRelight: 'blue',
  ImageUpscale: 'blue',
  // The video twin of Segmentation, kept visually distinct from it: one cuts a still frame out, the
  // other follows an object through a whole clip.
  VideoSegmentation: 'yellow',
  Compute: 'gray',
  MusicGeneration: 'grape',
  SoundEffectGeneration: 'grape',
  ContentModeration: 'red',
  Rerank: 'cyan',
  WebSearch: 'teal',
  AudioIsolation: 'grape',
  // Hosted video enhancement: one resolution job, one frame-rate job, both served by fal.ai only.
  VideoUpscale: 'pink',
  FrameInterpolation: 'violet',
  VideoOutpaint: 'indigo',
  LipSync: 'orange',
};

interface InferenceProviderTableProps {
  providers: InferenceProvider[];
  onDelete: (provider: InferenceProvider) => void;
  /** Which set of providers this is, so a row links to the page that can open it. */
  scope?: ProviderScope;
  /** Hides the row actions, keeping the table usable as a read-only listing. */
  readOnly?: boolean;
}

/** The host of an endpoint URL ("api.openai.com"), or the raw value when it is not a URL. */
export function endpointHost(endpoint: string): string {
  try {
    return new URL(endpoint).host || endpoint;
  } catch {
    return endpoint;
  }
}

/** The distinct capabilities served under a provider, in first-seen order, and which are defaults. */
function capabilitySummary(provider: InferenceProvider): { capability: InferenceProviderCapability; isDefault: boolean }[] {
  const seen = new Map<InferenceProviderCapability, boolean>();
  for (const model of provider.models) {
    for (const cap of model.capabilities) {
      seen.set(cap.capability, (seen.get(cap.capability) ?? false) || cap.isDefault);
    }
  }
  return [...seen].map(([capability, isDefault]) => ({ capability, isDefault }));
}

/** Stacked-card cell on a phone, ordinary table cell from `sm` up. */
const CELL = {
  display: { base: 'block', sm: 'table-cell' },
  py: { base: 4, sm: 'xs' },
} as const;

/** 40px tap targets for the row actions on a phone, the default size on desktop. */
const ACTION_PROPS = { miw: { base: 40, sm: 0 }, mih: { base: 40, sm: 0 } } as const;

/** Field name shown above a value in the stacked card layout; hidden once the real header row shows. */
function CellLabel({ children }: { children: ReactNode }) {
  return (
    <Text size="xs" c="dimmed" hiddenFrom="sm">
      {children}
    </Text>
  );
}

/**
 * One row per provider ACCOUNT: its kind, where it points, which key it holds, how many models sit
 * under it and which capabilities those models serve (a filled chip is a default). The system
 * compute provider is listed with a "System" badge and no edit or delete: the API refuses both.
 */
export function InferenceProviderTable({
  providers, onDelete, scope = 'platform', readOnly = false,
}: InferenceProviderTableProps) {
  const detailBase = scope === 'org' ? '/org/inference-providers' : '/admin/inference-providers';
  return (
    <Table.ScrollContainer minWidth={0}>
      <Table striped highlightOnHover miw={{ base: 0, sm: 820 }}>
        {/* Below `sm` each provider is a stacked card: the header row is dropped, every cell becomes a
            labelled block, and nothing needs a sideways scroll. From `sm` up it is the normal table. */}
        <Table.Thead visibleFrom="sm">
          <Table.Tr>
            <Table.Th>Name</Table.Th>
            <Table.Th>Kind</Table.Th>
            <Table.Th>Endpoint</Table.Th>
            <Table.Th>Key</Table.Th>
            <Table.Th>Status</Table.Th>
            <Table.Th>Models</Table.Th>
            <Table.Th>Capabilities</Table.Th>
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {providers.map((provider) => {
            const capabilities = capabilitySummary(provider);
            return (
              <Table.Tr
                key={provider.id}
                display={{ base: 'block', sm: 'table-row' }}
                pb={{ base: 'sm', sm: 0 }}
              >
                <Table.Td {...CELL}>
                  <CellLabel>Name</CellLabel>
                  <Group gap={6} wrap="wrap">
                    <Anchor component={Link} href={`${detailBase}/${provider.id}`} size="sm">
                      {provider.name}
                    </Anchor>
                    {provider.isSystem && (
                      <Tooltip label="The platform's own compute provider. It cannot be edited or deleted.">
                        <Badge color="dark" variant="light" size="sm">System</Badge>
                      </Tooltip>
                    )}
                    {provider.personalOAuthMode && (
                      <Tooltip label="Dev/OAuth (Personal use only): uses a personal Claude subscription credential">
                        <Badge color="yellow" variant="light" size="sm">Personal OAuth</Badge>
                      </Tooltip>
                    )}
                  </Group>
                </Table.Td>
                <Table.Td {...CELL}>
                  <CellLabel>Kind</CellLabel>
                  <Badge color={KIND_BADGE[provider.kind]?.color ?? 'gray'} variant="light" size="sm">
                    {KIND_BADGE[provider.kind]?.label ?? provider.kind}
                  </Badge>
                </Table.Td>
                <Table.Td {...CELL} style={{ wordBreak: 'break-all' }}>
                  <CellLabel>Endpoint</CellLabel>
                  <Tooltip label={provider.endpoint} disabled={!provider.endpoint}>
                    <Text size="sm" span>{endpointHost(provider.endpoint)}</Text>
                  </Tooltip>
                </Table.Td>
                <Table.Td {...CELL}>
                  <CellLabel>Key</CellLabel>
                  <Text size="sm" c={provider.hasApiKey ? undefined : 'dimmed'}>
                    {provider.hasApiKey ? `•••• ${provider.apiKeyLastFour ?? ''}` : 'No key'}
                  </Text>
                </Table.Td>
                <Table.Td {...CELL}>
                  <CellLabel>Status</CellLabel>
                  <Badge color={provider.isEnabled ? 'green' : 'gray'} variant="light" size="sm">
                    {provider.isEnabled ? 'Enabled' : 'Disabled'}
                  </Badge>
                </Table.Td>
                <Table.Td {...CELL}>
                  <CellLabel>Models</CellLabel>
                  {provider.models.length}
                </Table.Td>
                <Table.Td {...CELL}>
                  <CellLabel>Capabilities</CellLabel>
                  <Group gap={4}>
                    {capabilities.map(({ capability, isDefault }) => (
                      <Tooltip key={capability} label={isDefault ? `Default ${capability} model is here` : capability}>
                        <Badge
                          color={CAPABILITY_BADGE[capability] ?? 'gray'}
                          variant={isDefault ? 'filled' : 'light'}
                          size="sm"
                        >
                          {capability}
                        </Badge>
                      </Tooltip>
                    ))}
                  </Group>
                </Table.Td>
                <Table.Td {...CELL}>
                  {!readOnly && !provider.isSystem && (
                    <Group justify="flex-end" gap="xs" wrap="nowrap">
                      <ActionIcon
                        {...ACTION_PROPS}
                        component={Link}
                        href={`${detailBase}/${provider.id}`}
                        variant="subtle"
                        aria-label={`Edit ${provider.name}`}
                      >
                        <IconEdit size={16} />
                      </ActionIcon>
                      <ActionIcon
                        {...ACTION_PROPS}
                        color="red"
                        variant="subtle"
                        onClick={() => onDelete(provider)}
                        aria-label={`Delete ${provider.name}`}
                      >
                        <IconTrash size={16} />
                      </ActionIcon>
                    </Group>
                  )}
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
