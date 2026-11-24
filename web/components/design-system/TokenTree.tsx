'use client';

import { useState } from 'react';
import {
  Accordion,
  Badge,
  Group,
  Popover,
  Stack,
  Switch,
  Text,
  UnstyledButton,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconInfoCircle } from '@tabler/icons-react';
import { lockVersion } from '@/lib/api/design-system';
import {
  confidenceBand,
  isGroup,
  isToken,
  reelboltExtension,
  tokenConf,
  tokenProv,
  type DesignDocument,
  type DesignToken,
  type DesignVersionState,
} from '@/lib/types/design-system';
import { EditReasonText } from './PanelNotes';
import {
  BAND_LABELS,
  LOCKED_LABEL,
  LOCK_ERROR,
  TOKEN_GROUP_LABELS,
  TOKEN_GROUP_ORDER,
  TOKEN_PROVENANCE_CONFIDENCE,
  TOKEN_PROVENANCE_IMPORTER,
  TOKEN_PROVENANCE_LOCATION,
  TOKEN_PROVENANCE_METHOD,
  TOKEN_PROVENANCE_NONE,
  TOKENS_EMPTY,
  TOKENS_TITLE,
} from './copy';
import { TypeSpecimen } from './TypeSpecimen';
import mobile from './mobile.module.css';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

/** The version a token tree is being viewed at — enough to gate the lock control. */
export interface TokenTreeVersion {
  id: string;
  state: DesignVersionState;
}

/** Everything the recursive tree needs to render and act on lock toggles. */
interface LockContext {
  projectId: string;
  version: TokenTreeVersion | null;
  canEdit: boolean;
  busyPath: string | null;
  onToggle: (path: string, nextLocked: boolean) => void;
}

/** A one-line preview of a token's value: a colour's hex, a list joined, or a scalar verbatim. */
function previewToken(token: DesignToken): string | null {
  const value = token.$value;
  if (token.$type === 'color' && typeof value === 'object' && value !== null) {
    const hex = (value as Record<string, unknown>).hex;
    if (typeof hex === 'string') return hex;
  }
  if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
    return String(value);
  }
  if (Array.isArray(value)) return value.map((entry) => String(entry)).join(', ');
  return null;
}

function ProvenanceRow({ label, value }: { label: string; value: string }) {
  return (
    <Group gap="xs" wrap="nowrap" justify="space-between" className={mobile.wrapRow}>
      <Text size="xs" c="dimmed">{label}</Text>
      <Text size="xs">{value}</Text>
    </Group>
  );
}

function TokenLeaf({
  doc,
  path,
  token,
  lock,
}: {
  doc: DesignDocument;
  path: string;
  token: DesignToken;
  lock: LockContext;
}) {
  const tr = useT();
  const leaf = path.split('.').pop() ?? path;
  const preview = previewToken(token);
  const prov = tokenProv(doc, path);
  const band = confidenceBand(tokenConf(doc, path));
  const locked = reelboltExtension(doc, path)?.locked === true;
  const isDraft = lock.version?.state === 'Draft';

  return (
    <Group justify="space-between" wrap="nowrap" gap="xs" className={mobile.wrapRow}>
      <Group gap="xs" wrap="nowrap" className={mobile.wrapRow}>
        <Text size="sm" fw={600} className={mobile.breakText}>{leaf}</Text>
        {token.$type && <Badge size="xs" variant="light" color="gray">{token.$type}</Badge>}
        {preview && <Text size="xs" ff="monospace" c="dimmed" className={mobile.breakText}>{preview}</Text>}
        {token.$type === 'fontFamily' && preview && <TypeSpecimen compact family={preview} />}
      </Group>
      <Group gap="xs" wrap="nowrap" className={mobile.wrapRow}>
        {locked && !isDraft && (
          <Badge size="xs" variant="light" color="gray" data-testid={`token-locked-${path}`}>
            {tr.msg(LOCKED_LABEL)}
          </Badge>
        )}
        {isDraft && (
          <Switch
            size="xs"
            checked={locked}
            disabled={!lock.canEdit || lock.busyPath !== null}
            aria-label={tr('brandLibraries.profile.tokens.lockAria', { action: locked ? 'unlock' : 'lock', path })}
            aria-describedby={lock.canEdit ? undefined : LOCK_REASON_ID}
            data-testid={`lock-toggle-${path}`}
            onChange={(event) => lock.onToggle(path, event.currentTarget.checked)}
          />
        )}
        <Popover position="top-end" withArrow withinPortal>
          <Popover.Target>
            <UnstyledButton
              aria-label={tr('brandLibraries.profile.tokens.provenanceAria', { path })}
              style={{ display: 'inline-flex', padding: 4, color: 'var(--mantine-color-dimmed)' }}
            >
              <IconInfoCircle size={14} />
            </UnstyledButton>
          </Popover.Target>
          <Popover.Dropdown data-testid={`token-provenance-${path}`}>
            <Stack gap={2}>
              <ProvenanceRow label={tr.msg(TOKEN_PROVENANCE_METHOD)} value={prov?.method ?? tr.msg(TOKEN_PROVENANCE_NONE)} />
              <ProvenanceRow label={tr.msg(TOKEN_PROVENANCE_IMPORTER)} value={prov?.importer ?? tr.msg(TOKEN_PROVENANCE_NONE)} />
              <ProvenanceRow label={tr.msg(TOKEN_PROVENANCE_LOCATION)} value={prov?.loc ?? tr.msg(TOKEN_PROVENANCE_NONE)} />
              <ProvenanceRow
                label={tr.msg(TOKEN_PROVENANCE_CONFIDENCE)}
                value={band ? tr.msg(BAND_LABELS[band]) : tr.msg(TOKEN_PROVENANCE_NONE)}
              />
            </Stack>
          </Popover.Dropdown>
        </Popover>
      </Group>
    </Group>
  );
}

/** Id of the visible sentence that says why the lock switches are disabled. */
const LOCK_REASON_ID = 'token-lock-reason';

/** One group's children: nested groups become accordions, token leaves become rows with locks. */
function TokenChildren({
  doc,
  path,
  node,
  lock,
}: {
  doc: DesignDocument;
  path: string;
  node: Record<string, unknown>;
  lock: LockContext;
}) {
  const tr = useT();
  const entries = Object.entries(node).filter(([key]) => !key.startsWith('$'));
  const nestedGroups = entries.filter(([, value]) => isGroup(value));
  const leaves = entries.filter(([, value]) => isToken(value));

  return (
    <Stack gap="xs">
      {nestedGroups.length > 0 && (
        <Accordion multiple variant="contained" radius="sm" defaultValue={nestedGroups.map(([key]) => key)}>
          {nestedGroups.map(([key, value]) => (
            <Accordion.Item key={key} value={key}>
              <Accordion.Control>
                <Group gap="xs">
                  <Text size="sm" fw={600}>{(TOKEN_GROUP_LABELS[key] ? tr.msg(TOKEN_GROUP_LABELS[key]) : key)}</Text>
                  <Badge size="xs" variant="light" color="gray">{key}</Badge>
                </Group>
              </Accordion.Control>
              <Accordion.Panel>
                <TokenChildren
                  doc={doc}
                  path={`${path}.${key}`}
                  node={value as Record<string, unknown>}
                  lock={lock}
                />
              </Accordion.Panel>
            </Accordion.Item>
          ))}
        </Accordion>
      )}
      {leaves.map(([key, value]) => (
        <TokenLeaf
          key={key}
          doc={doc}
          path={`${path}.${key}`}
          token={value as DesignToken}
          lock={lock}
        />
      ))}
    </Stack>
  );
}

interface TokenTreeProps {
  projectId: string;
  doc: DesignDocument;
  /** The viewed version; lock toggles render ONLY when its state is `Draft`. */
  version?: TokenTreeVersion | null;
  canEdit?: boolean;
  /** Called after a lock edit succeeds, so the parent can refresh the version. */
  onLocked?: () => void;
}

/**
 * The design document's token tree: the known top-level groups as collapsible sections, each token
 * showing its type, a value preview and a provenance popover. A Draft version carries a lock toggle
 * per leaf (SPEC §9 `PATCH /versions/{id}/locks`); any other state shows a read-only "Locked"
 * marker and no control.
 */
export function TokenTree({ projectId, doc, version = null, canEdit = false, onLocked }: TokenTreeProps) {
  const tr = useT();
  const [busyPath, setBusyPath] = useState<string | null>(null);

  const toggle = async (path: string, nextLocked: boolean) => {
    if (!version || !canEdit) return;
    setBusyPath(path);
    try {
      await lockVersion(projectId, version.id, nextLocked ? [path] : [], nextLocked ? [] : [path]);
      onLocked?.();
    } catch (e) {
      notifications.show({
        color: 'red',
        message: describeError(tr, e, tr.msg(LOCK_ERROR)),
      });
    } finally {
      setBusyPath(null);
    }
  };

  const lock: LockContext = {
    projectId,
    version,
    canEdit,
    busyPath,
    onToggle: (path, nextLocked) => void toggle(path, nextLocked),
  };

  const source = doc as Record<string, unknown>;
  const groups = TOKEN_GROUP_ORDER.filter((key) => isGroup(source[key]));

  if (groups.length === 0) {
    return <Text size="sm" c="dimmed">{tr.msg(TOKENS_EMPTY)}</Text>;
  }

  return (
    <Stack gap="sm">
      <Text fw={650}>{tr.msg(TOKENS_TITLE)}</Text>
      {!canEdit && version?.state === 'Draft' && <EditReasonText id={LOCK_REASON_ID} />}
      <Accordion multiple variant="contained" radius="sm" defaultValue={[...groups]}>
        {groups.map((key) => (
          <Accordion.Item key={key} value={key}>
            <Accordion.Control>
              <Group gap="xs">
                <Text size="sm" fw={600}>{(TOKEN_GROUP_LABELS[key] ? tr.msg(TOKEN_GROUP_LABELS[key]) : key)}</Text>
                <Badge size="xs" variant="light" color="gray">{key}</Badge>
              </Group>
            </Accordion.Control>
            <Accordion.Panel>
              <TokenChildren doc={doc} path={key} node={source[key] as Record<string, unknown>} lock={lock} />
            </Accordion.Panel>
          </Accordion.Item>
        ))}
      </Accordion>
    </Stack>
  );
}
