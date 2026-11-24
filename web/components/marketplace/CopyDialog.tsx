'use client';

import { useState } from 'react';
import Link from 'next/link';
import { Modal, NativeSelect, TextInput } from '@mantine/core';
import { IconCircleCheck } from '@tabler/icons-react';
import { needsHref } from '@/components/workflow-apps/gallery-model';
import { useT } from '@/i18n';
import { useMarketplaceActions } from '@/lib/hooks/use-marketplace';
import { useProjects } from '@/lib/hooks/use-projects';
import type { MarketplaceCopyResult } from '@/lib/types/marketplace';
import { marketplaceErrorMessage } from './marketplace-model';
import styles from './marketplace.module.css';

/**
 * Copy a recipe into one of the signed-in account's projects. On success it shows the open slots ("needs") exactly as the
 * template cards do and links to the copied app. A copy is idempotent per (recipe, project, name) on the server.
 */
export function CopyDialog({ recipeId, recipeName, opened, onClose }: { recipeId: string; recipeName: string; opened: boolean; onClose: () => void }) {
  const t = useT('marketplace.copy');
  const tr = useT();
  const { data: projects, isLoading } = useProjects();
  const { copy } = useMarketplaceActions(recipeId);
  const [projectId, setProjectId] = useState('');
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<{ projectId: string; projectName: string; result: MarketplaceCopyResult } | null>(null);

  const list = projects ?? [];
  const chosen = projectId || (list.length === 1 ? list[0].id : '');

  const submit = async () => {
    if (!chosen || busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await copy(chosen, name);
      setDone({ projectId: chosen, projectName: list.find((p) => p.id === chosen)?.name ?? '', result });
    } catch (err: unknown) {
      setError(marketplaceErrorMessage(tr, err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={done ? t('doneTitle', { project: done.projectName }) : t('dialogTitle')} centered>
      {done ? (
        <div className={styles.section} role="status">
          <p className={styles.plain}>{t('doneBody', { name: done.result.name })}</p>
          {done.result.needs.length === 0 ? (
            <div className={`${styles.needsRow} ${styles.ok}`}>
              <IconCircleCheck size={16} aria-hidden />
              <span>{t('ready')}</span>
            </div>
          ) : (
            done.result.needs.map((n) => {
              const href = needsHref(done.projectId, n.action);
              return (
                <div key={`${n.need}-${n.action}`} className={styles.needsRow}>
                  <span>{t('needs')}</span>
                  {href ? (
                    <Link href={href} className={styles.needsLink}>
                      {n.message}
                    </Link>
                  ) : (
                    <span>{n.message}</span>
                  )}
                </div>
              );
            })
          )}
          <div className={styles.actions}>
            <Link href={`/projects/${done.projectId}/workflows/${done.result.workflowId}`} className={styles.buttonPrimary}>
              {t('open')}
            </Link>
            <button type="button" className={styles.button} onClick={onClose}>
              {t('close')}
            </button>
          </div>
        </div>
      ) : (
        <form
          className={styles.section}
          onSubmit={(e) => {
            e.preventDefault();
            void submit();
          }}
        >
          <p className={styles.hint}>{t('dialogHint')}</p>
          <p className={styles.plain}>{recipeName}</p>
          {isLoading ? (
            <p className={styles.hint} role="status">{t('projectsLoading')}</p>
          ) : list.length === 0 ? (
            <p className={styles.hint}>{t('noProjects')}</p>
          ) : (
            <NativeSelect
              label={t('project')}
              value={chosen}
              onChange={(e) => setProjectId(e.currentTarget.value)}
              data={[{ value: '', label: t('projectPlaceholder'), disabled: true }, ...list.map((p) => ({ value: p.id, label: p.name }))]}
              required
              styles={{ input: { minHeight: 44 } }}
            />
          )}
          <TextInput
            label={t('name')}
            placeholder={t('namePlaceholder')}
            value={name}
            onChange={(e) => setName(e.currentTarget.value)}
            maxLength={120}
            styles={{ input: { minHeight: 44 } }}
          />
          {error && <p className={styles.error} role="alert">{error}</p>}
          <div className={styles.actions}>
            <button type="submit" className={styles.buttonPrimary} disabled={!chosen || busy}>
              {busy ? t('copying') : t('confirm')}
            </button>
            <button type="button" className={styles.button} onClick={onClose}>
              {t('cancel')}
            </button>
          </div>
        </form>
      )}
    </Modal>
  );
}
