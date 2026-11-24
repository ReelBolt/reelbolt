'use client';

import { describeError } from '@/lib/api/describe-error';
import { useState } from 'react';
import { Alert, Badge, Button, Center, Group, Loader, Stack, Tabs, Text } from '@mantine/core';
import { Panel, QuietText } from '@/components/dashboard/Panel';
import { ApiError } from '@/lib/api/client';
import { useAuth } from '@/lib/hooks/use-auth';
import { useDesignSystem, useDesignVersion } from '@/lib/hooks/use-design-system';
import { BuildProfileModal } from './BuildProfileModal';
import { ImportProfileModal } from './ImportProfileModal';
import { LibraryLinkSelect } from './LibraryLinkSelect';
import { PanelError } from './PanelNotes';
import { ProfileOverview } from './ProfileOverview';
import { ProposalReview } from './ProposalReview';
import { SourcesPanel } from './SourcesPanel';
import { TokenTree } from './TokenTree';
import { VersionHistory } from './VersionHistory';
import {
  approvedTokensTitle,
  BUILD_BUTTON_LABEL,
  draftTokensTitle,
  DRAFT_TOKENS_HINT,
  EMPTY_BUILD_LABEL,
  EMPTY_DESCRIPTION,
  EMPTY_EDIT_TOOLTIP,
  EMPTY_IMPORT_LABEL,
  EMPTY_TITLE,
  ERROR_403,
  ERROR_404,
  ERROR_FALLBACK,
  ERROR_TITLE,
  IMPORT_BUTTON_LABEL,
  REBUILD_WITH_AI_LABEL,
  SUB_TAB_HISTORY,
  SUB_TAB_OVERVIEW,
  SUB_TAB_PROPOSALS,
  SUB_TAB_SOURCES,
  SUB_TAB_TOKENS,
} from './copy';
import mobile from './mobile.module.css';
import { useT, type T } from '@/i18n';

/** 404 and 403 get their own sentence; anything else keeps the API's message. */
function errorMessage(error: unknown, tr: T): string {
  if (error instanceof ApiError) {
    if (error.status === 404) return tr.msg(ERROR_404);
    if (error.status === 403) return tr.msg(ERROR_403);
    return describeError(tr, error, tr.msg(ERROR_FALLBACK));
  }
  return describeError(tr, error, tr.msg(ERROR_FALLBACK));
}

/** Id of the visible sentence that explains why the edit buttons are disabled. */
const EDIT_REASON_ID = 'profile-edit-reason';

function EditReason() {
  const tr = useT();
  return <Text size="xs" c="dimmed" id={EDIT_REASON_ID}>{tr.msg(EMPTY_EDIT_TOOLTIP)}</Text>;
}

function EmptyAction({
  label,
  canEdit,
  onClick,
}: {
  label: string;
  canEdit: boolean;
  onClick: () => void;
}) {
  return (
    <Button
      variant="light"
      style={{ minHeight: 44 }}
      disabled={!canEdit}
      aria-describedby={canEdit ? undefined : EDIT_REASON_ID}
      onClick={onClick}
    >
      {label}
    </Button>
  );
}

function EditAction({
  label,
  canEdit,
  onClick,
  primary,
}: {
  label: string;
  canEdit: boolean;
  onClick: () => void;
  primary?: boolean;
}) {
  return (
    <Button
      size="xs"
      variant={primary ? 'filled' : 'light'}
      disabled={!canEdit}
      aria-describedby={canEdit ? undefined : EDIT_REASON_ID}
      onClick={onClick}
    >
      {label}
    </Button>
  );
}

/**
 * The "Brand & product" tab (SPEC §9). It renders the two profiles' current versions and their
 * documents in four sub-tabs, opens the Build and Import flows (P2-22) and renders the draft-review
 * banner per kind. Edit authority is the API's `403`; this gate on the caller's org role only avoids
 * a button that could not work.
 */
export function ProjectProfileTab({ projectId }: { projectId: string }) {
  const tr = useT();
  const { data, isLoading, error, mutate } = useDesignSystem(projectId);
  const { user, isPlatformAdmin, isOrgAdmin } = useAuth();
  // SPEC §17: the server's 403 is the real guard. The web derives the same "not a read-only
  // Billing role" signal P2-21/P2-22 used, since `useAuth()` exposes no project-level Edit flag.
  const canEdit = isPlatformAdmin || isOrgAdmin || user?.orgRole !== 'Billing';
  // SPEC §16.1/P4: libraries carry their own, narrower authority. Billing cannot read them at all,
  // and only Owner/Admin may write one, so a platform admin whose active role is Member is
  // read-only here — exactly like the server's `OrgLibraryGate`.
  const isBilling = user?.orgRole === 'Billing';
  const canWriteLibrary = isOrgAdmin;

  const [buildOpen, setBuildOpen] = useState(false);
  const [importOpen, setImportOpen] = useState(false);

  const design = data?.design ?? null;
  const productMap = data?.productMap ?? null;
  const pendingProposals =
    (design?.pendingProposals ?? 0) + (productMap?.pendingProposals ?? 0);

  const designVersion = useDesignVersion(projectId, design?.currentVersion?.id ?? null);
  // A draft is what the lock switches act on (the API only locks a Draft), so its document is loaded
  // beside the approved one.
  const draftVersion = useDesignVersion(projectId, design?.latestDraft?.id ?? null);
  const productVersion = useDesignVersion(projectId, productMap?.currentVersion?.id ?? null);

  const modals = (
    <>
      <BuildProfileModal
        projectId={projectId}
        opened={buildOpen}
        onClose={() => setBuildOpen(false)}
        onBuilt={() => void mutate()}
        onImport={() => {
          setBuildOpen(false);
          setImportOpen(true);
        }}
      />
      <ImportProfileModal
        projectId={projectId}
        opened={importOpen}
        onClose={() => setImportOpen(false)}
        onImported={() => void mutate()}
        canEdit={canEdit}
        current={data ?? null}
      />
    </>
  );

  if (isLoading && !data) {
    return <Center h={160}><Loader size="sm" /></Center>;
  }

  if (error) {
    return (
      <Alert color="red" title={tr.msg(ERROR_TITLE)} data-testid="profile-error">
        {errorMessage(error, tr)}
      </Alert>
    );
  }

  // A first build or import leaves a Draft and no current version: that still needs the review
  // banner, so it counts as "something to show".
  const hasAnyProfile = Boolean(
    design?.currentVersion || productMap?.currentVersion || design?.latestDraft || productMap?.latestDraft,
  );

  if (!hasAnyProfile) {
    return (
      <>
        <Panel id="profile-empty">
          <Stack align="flex-start" gap="xs">
            <Text fw={650}>{tr.msg(EMPTY_TITLE)}</Text>
            <QuietText>{tr.msg(EMPTY_DESCRIPTION)}</QuietText>
            <Group gap="xs">
              <EmptyAction
                label={tr.msg(EMPTY_IMPORT_LABEL)}
                canEdit={canEdit}
                onClick={() => setImportOpen(true)}
              />
              <EmptyAction
                label={tr.msg(EMPTY_BUILD_LABEL)}
                canEdit={canEdit}
                onClick={() => setBuildOpen(true)}
              />
            </Group>
            {!canEdit && <EditReason />}
          </Stack>
        </Panel>
        {modals}
      </>
    );
  }

  return (
    <>
      {!canEdit && <EditReason />}
      <Tabs defaultValue="overview">
        <Group justify="space-between" align="center" mb="md" wrap="nowrap" className={mobile.wrapRow}>
          <Tabs.List className={mobile.tabList}>
            <Tabs.Tab value="overview">{tr.msg(SUB_TAB_OVERVIEW)}</Tabs.Tab>
            <Tabs.Tab value="tokens">{tr.msg(SUB_TAB_TOKENS)}</Tabs.Tab>
            <Tabs.Tab value="sources">{tr.msg(SUB_TAB_SOURCES)}</Tabs.Tab>
            <Tabs.Tab value="history">{tr.msg(SUB_TAB_HISTORY)}</Tabs.Tab>
            <Tabs.Tab value="proposals">
              {tr.msg(SUB_TAB_PROPOSALS)}
              {pendingProposals > 0 && (
                <Badge size="xs" variant="filled" color="blue" ml={6} data-testid="proposals-badge">
                  {pendingProposals}
                </Badge>
              )}
            </Tabs.Tab>
          </Tabs.List>
          <Group gap="xs" wrap="nowrap" className={mobile.actionRow}>
            <EditAction
              label={tr.msg(IMPORT_BUTTON_LABEL)}
              canEdit={canEdit}
              onClick={() => setImportOpen(true)}
            />
            <EditAction
              label={tr.msg(REBUILD_WITH_AI_LABEL)}
              canEdit={canEdit}
              onClick={() => setBuildOpen(true)}
            />
            <EditAction
              label={tr.msg(BUILD_BUTTON_LABEL)}
              canEdit={canEdit}
              onClick={() => setBuildOpen(true)}
              primary
            />
          </Group>
        </Group>

        <Tabs.Panel value="overview">
          <Stack gap="lg">
            {isBilling ? (
              <Text size="sm" c="dimmed" data-testid="library-billing-unavailable">
                {tr('brandLibraries.page.billingUnavailable')}
              </Text>
            ) : (
              <LibraryLinkSelect
                projectId={projectId}
                current={design?.parentLibrary ?? null}
                canWrite={canWriteLibrary}
                onChanged={() => void mutate()}
              />
            )}
            <ProfileOverview
              projectId={projectId}
              design={design}
              productMap={productMap}
              designDocument={designVersion.data?.document ?? null}
              productDocument={productVersion.data?.document ?? null}
              canEdit={canEdit}
              onChanged={() => void mutate()}
              designDocumentFailed={Boolean(designVersion.error)}
              onRetryDesignDocument={() => void designVersion.mutate()}
            />
          </Stack>
        </Tabs.Panel>
        <Tabs.Panel value="tokens">
          <Stack gap="lg">
            {draftVersion.data && design?.latestDraft && (
              <Stack gap="xs">
                <Text fw={650}>{tr.msg(draftTokensTitle(design.latestDraft.versionNumber))}</Text>
                <QuietText>{tr.msg(DRAFT_TOKENS_HINT)}</QuietText>
                <TokenTree
                  projectId={projectId}
                  doc={draftVersion.data.document}
                  version={{ id: draftVersion.data.id, state: draftVersion.data.state }}
                  canEdit={canEdit}
                  onLocked={() => void draftVersion.mutate()}
                />
              </Stack>
            )}
            {design?.currentVersion && designVersion.error && !designVersion.data && (
              <PanelError onRetry={() => void designVersion.mutate()} />
            )}
            {design?.currentVersion && !(designVersion.error && !designVersion.data) && (
              <Stack gap="xs">
                {draftVersion.data && (
                  <Text fw={650}>{tr.msg(approvedTokensTitle(design.currentVersion.versionNumber))}</Text>
                )}
                <TokenTree
                  projectId={projectId}
                  doc={designVersion.data?.document ?? {}}
                  version={
                    designVersion.data
                      ? { id: designVersion.data.id, state: designVersion.data.state }
                      : null
                  }
                  canEdit={canEdit}
                  onLocked={() => void designVersion.mutate()}
                />
              </Stack>
            )}
            {!design?.currentVersion && !draftVersion.data && (
              <TokenTree projectId={projectId} doc={{}} />
            )}
          </Stack>
        </Tabs.Panel>
        <Tabs.Panel value="sources">
          <SourcesPanel projectId={projectId} canEdit={canEdit} />
        </Tabs.Panel>
        <Tabs.Panel value="history">
          <VersionHistory projectId={projectId} canEdit={canEdit} />
        </Tabs.Panel>
        <Tabs.Panel value="proposals">
          <Stack gap="lg">
            {design && (
              <ProposalReview
                projectId={projectId}
                kind="design"
                canEdit={canEdit}
                onChanged={() => void mutate()}
              />
            )}
            {productMap && (
              <ProposalReview
                projectId={projectId}
                kind="product_map"
                canEdit={canEdit}
                onChanged={() => void mutate()}
              />
            )}
          </Stack>
        </Tabs.Panel>
      </Tabs>
      {modals}
    </>
  );
}
