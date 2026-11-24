'use client';

import { useState } from 'react';
import { Modal, NativeSelect, Tabs, Textarea } from '@mantine/core';
import { mutate as globalMutate } from 'swr';
import { PageHeader } from '@/components/shared/PageHeader';
import { useT } from '@/i18n';
import {
  adminDismissReport,
  adminListUrl,
  adminRecipeAction,
  adminPreviewUrl,
  adminPosterUrl,
  type AdminAction,
} from '@/lib/api/marketplace';
import { useAuth } from '@/lib/hooks/use-auth';
import { useAdminRecipes, useAdminReports } from '@/lib/hooks/use-marketplace-owner';
import { REJECT_REASONS, REPORT_NOTE_MAX, type AdminRecipe, type AdminReport, type AdminTab, type MarketplaceRejectReason } from '@/lib/types/marketplace';
import { PREVIEW_STATUS_LABELS, REJECT_REASON_LABELS, marketplaceErrorMessage, reasonCategoryLabel } from './marketplace-model';
import { StatusChip } from './StatusChip';
import styles from './marketplace.module.css';

type Pending = { kind: 'reject' | 'remove'; recipeId: string; name: string; row?: AdminRecipe } | null;

function Checks({ recipe }: { recipe: AdminRecipe }) {
  const t = useT('marketplace.admin');
  const c = recipe.checks;
  if (!c) return <p className={styles.hint}>{t('noChecks')}</p>;
  return (
    <ul className={styles.list} aria-label={t('checks')}>
      <li>{t('textVerdict', { verdict: c.text ?? t('none') })}</li>
      <li>{t('previewVerdict', { verdict: c.preview ?? t('none') })}</li>
      <li>{t('lintCodes', { codes: c.lintCodes?.length ? c.lintCodes.join(', ') : t('none') })}</li>
    </ul>
  );
}

/** The preview of the version under review, from the admin route, with its status and hash and an error state. */
function ReviewPreview({ recipe }: { recipe: AdminRecipe }) {
  const t = useT('marketplace.admin');
  const tr = useT();
  const [failed, setFailed] = useState(false);
  return (
    <section className={styles.section} aria-label={t('previewUnderReview')}>
      <h4 className={styles.promptHeading}>{t('previewUnderReview')}</h4>
      {failed ? (
        <p className={styles.error} role="alert">{t('previewUnavailable')}</p>
      ) : (
        <video
          className={styles.player}
          controls
          preload="none"
          poster={adminPosterUrl(recipe.versionId)}
          src={adminPreviewUrl(recipe.versionId)}
          aria-label={tr('marketplace.detail.previewLabel', { name: recipe.name })}
          onError={() => setFailed(true)}
        />
      )}
      <p className={styles.hint}>
        {t('previewHash', {
          status: recipe.previewStatus ? tr.msg(PREVIEW_STATUS_LABELS[recipe.previewStatus] ?? PREVIEW_STATUS_LABELS.preparing) : t('none'),
          hash: recipe.previewSha256 ?? t('none'),
        })}
      </p>
    </section>
  );
}

/** Platform-admin review of community recipes: pending, reports, listed, removed. Every action is done by the API's admin routes. */
export function AdminMarketplace() {
  const t = useT('marketplace.admin');
  const tr = useT();
  const { isPlatformAdmin } = useAuth();
  const [tab, setTab] = useState<AdminTab>('pending');
  const pending = useAdminRecipes('pending', isPlatformAdmin && tab === 'pending');
  const listed = useAdminRecipes('listed', isPlatformAdmin && tab === 'listed');
  const removed = useAdminRecipes('removed', isPlatformAdmin && tab === 'removed');
  const reports = useAdminReports(isPlatformAdmin && tab === 'reports');
  const [dialog, setDialog] = useState<Pending>(null);
  const [reason, setReason] = useState<MarketplaceRejectReason>('content');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = () =>
    Promise.all((['pending', 'reports', 'listed', 'removed'] as AdminTab[]).map((k) => globalMutate(adminListUrl(k))));

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
      setDialog(null);
      setNote('');
      await refresh();
    } catch (err: unknown) {
      setError(marketplaceErrorMessage(tr, err));
      setDialog(null);
      if ((err as { code?: unknown }).code === 'STALE_VERSION') await refresh();
    } finally {
      setBusy(false);
    }
  };

  const act = (id: string, action: AdminAction, row?: AdminRecipe) =>
    run(() => (row ? adminRecipeAction(id, action, { versionId: row.versionId, digest: row.digest }) : adminRecipeAction(id, action)));

  if (!isPlatformAdmin) {
    return <p className={styles.hint}>{t('adminOnly')}</p>;
  }

  const recipeRow = (r: AdminRecipe, actions: React.ReactNode) => (
    <li key={r.id} className={styles.row}>
      <div className={styles.rowHead}>
        <h3 className={styles.sectionTitle}>{r.name}</h3>
        <StatusChip status={r.status} />
      </div>
      <div className={styles.meta}>
        <span>{tr('marketplace.card.by', { author: r.authorLabel })}</span>
        {r.orgName && <span>{t('org', { org: r.orgName })}</span>}
        <span>{tr('marketplace.detail.version', { version: r.version })}</span>
      </div>
      {r.description && <p className={styles.plain}>{r.description}</p>}
      <Checks recipe={r} />
      <ReviewPreview recipe={r} />
      <h4 className={styles.promptHeading}>{tr('marketplace.detail.promptsTitle')}</h4>
      {(r.prompts ?? []).length === 0 ? (
        <p className={styles.hint}>{tr('marketplace.detail.promptsNone')}</p>
      ) : (
        (r.prompts ?? []).map((p) => (
          <div key={`${p.stepOrder}-${p.field}`} className={styles.section}>
            <span className={styles.promptHeading}>{tr('marketplace.detail.promptHeading', { order: p.stepOrder, field: p.field })}</span>
            <pre className={styles.prompt} tabIndex={0} role="region" aria-label={tr('marketplace.detail.promptHeading', { order: p.stepOrder, field: p.field })}>{p.text}</pre>
          </div>
        ))
      )}
      <div className={styles.actions}>{actions}</div>
    </li>
  );

  const list = (state: { items: AdminRecipe[] | undefined; isLoading: boolean; error: unknown }, actions: (r: AdminRecipe) => React.ReactNode) => {
    if (state.isLoading) return <p className={styles.hint} role="status">{t('loading')}</p>;
    if (state.error && !state.items) return <p className={styles.error} role="alert">{t('loadFailed')}</p>;
    if (!state.items || state.items.length === 0) return <p className={styles.hint}>{t('empty')}</p>;
    return <ul className={styles.table}>{state.items.map((r) => recipeRow(r, actions(r)))}</ul>;
  };

  const removeButton = (r: AdminRecipe) => (
    <button type="button" className={styles.button} onClick={() => setDialog({ kind: 'remove', recipeId: r.id, name: r.name })}>
      {t('remove')}
    </button>
  );

  const reportRow = (r: AdminReport) => {
    const label = reasonCategoryLabel(r.reason);
    return (
      <li key={r.id} className={styles.row}>
        <div className={styles.rowHead}>
          <h3 className={styles.sectionTitle}>{r.recipeName}</h3>
          {r.count !== undefined && <span className={styles.badge}>{t('reportCount', { count: r.count })}</span>}
        </div>
        <p className={styles.plain}>{t('reportReason', { reason: label ? tr.msg(label) : r.reason })}</p>
        {r.note && <p className={styles.plain}>{r.note}</p>}
        <div className={styles.actions}>
          <button type="button" className={styles.button} disabled={busy} onClick={() => void run(() => adminDismissReport(r.id))}>
            {t('dismiss')}
          </button>
          <button type="button" className={styles.button} onClick={() => setDialog({ kind: 'remove', recipeId: r.recipeId, name: r.recipeName })}>
            {t('removeRecipe')}
          </button>
        </div>
      </li>
    );
  };

  return (
    <div className={styles.root}>
      <PageHeader title={t('title')} breadcrumbs={[{ label: t('breadcrumb'), href: '/admin' }, { label: t('title') }]} />
      {error && <p className={styles.error} role="alert">{error}</p>}
      <Tabs value={tab} onChange={(v) => v && setTab(v as AdminTab)} keepMounted={false}>
        <Tabs.List aria-label={t('tabs')}>
          <Tabs.Tab value="pending">{t('tabPending')}</Tabs.Tab>
          <Tabs.Tab value="reports">{t('tabReports')}</Tabs.Tab>
          <Tabs.Tab value="listed">{t('tabListed')}</Tabs.Tab>
          <Tabs.Tab value="removed">{t('tabRemoved')}</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="pending" pt="md">
          {list(pending, (r) => (
            <>
              <button type="button" className={styles.buttonPrimary} disabled={busy} onClick={() => void act(r.id, 'approve', r)}>
                {t('approve')}
              </button>
              <button type="button" className={styles.button} onClick={() => setDialog({ kind: 'reject', recipeId: r.id, name: r.name, row: r })}>
                {t('reject')}
              </button>
            </>
          ))}
        </Tabs.Panel>
        <Tabs.Panel value="reports" pt="md">
          {reports.isLoading ? (
            <p className={styles.hint} role="status">{t('loading')}</p>
          ) : reports.error && !reports.items ? (
            <p className={styles.error} role="alert">{t('loadFailed')}</p>
          ) : !reports.items || reports.items.length === 0 ? (
            <p className={styles.hint}>{t('emptyReports')}</p>
          ) : (
            <ul className={styles.table}>{reports.items.map(reportRow)}</ul>
          )}
        </Tabs.Panel>
        <Tabs.Panel value="listed" pt="md">{list(listed, removeButton)}</Tabs.Panel>
        <Tabs.Panel value="removed" pt="md">
          {list(removed, (r) => (
            <button type="button" className={styles.button} disabled={busy} onClick={() => void act(r.id, 'restore')}>
              {t('restore')}
            </button>
          ))}
        </Tabs.Panel>
      </Tabs>

      <Modal
        opened={dialog !== null}
        onClose={() => setDialog(null)}
        title={dialog?.kind === 'remove' ? t('removeTitle', { name: dialog.name }) : t('rejectTitle', { name: dialog?.name ?? '' })}
        centered
      >
        <form
          className={styles.section}
          onSubmit={(e) => {
            e.preventDefault();
            if (!dialog) return;
            const body = { reason, note: note.trim() || undefined };
            if (dialog.kind === 'remove') {
              void run(() => adminRecipeAction(dialog.recipeId, 'remove', body));
            } else {
              void run(() => adminRecipeAction(dialog.recipeId, 'reject', { versionId: dialog.row?.versionId, digest: dialog.row?.digest, ...body }));
            }
          }}
        >
          {dialog?.kind === 'remove' && <p className={styles.hint}>{t('removeMessage', { name: dialog.name })}</p>}
          <NativeSelect
            label={t('rejectReason')}
            value={reason}
            onChange={(e) => setReason(e.currentTarget.value as MarketplaceRejectReason)}
            data={REJECT_REASONS.map((r) => ({ value: r, label: tr.msg(REJECT_REASON_LABELS[r]) }))}
            styles={{ input: { minHeight: 44 } }}
          />
          <Textarea label={t('note')} value={note} onChange={(e) => setNote(e.currentTarget.value.slice(0, REPORT_NOTE_MAX))} maxLength={REPORT_NOTE_MAX} rows={3} />
          <div className={styles.actions}>
            <button type="submit" className={styles.buttonPrimary} disabled={busy}>{dialog?.kind === 'remove' ? t('remove') : t('reject')}</button>
            <button type="button" className={styles.button} onClick={() => setDialog(null)}>{tr('marketplace.copy.cancel')}</button>
          </div>
        </form>
      </Modal>
    </div>
  );
}
