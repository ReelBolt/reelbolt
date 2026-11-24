'use client';

import { useState } from 'react';
import { Modal, Radio, Textarea } from '@mantine/core';
import { useT } from '@/i18n';
import { useMarketplaceActions } from '@/lib/hooks/use-marketplace';
import { REPORT_NOTE_MAX, REPORT_REASONS, type MarketplaceReportReason } from '@/lib/types/marketplace';
import { REPORT_REASON_LABELS, marketplaceErrorMessage } from './marketplace-model';
import styles from './marketplace.module.css';

/** Report a recipe: one reason from the closed list plus an optional plain-text note of at most 500 characters. */
export function ReportDialog({ recipeId, opened, onClose }: { recipeId: string; opened: boolean; onClose: () => void }) {
  const t = useT('marketplace.report');
  const tr = useT();
  const { report } = useMarketplaceActions(recipeId);
  const [reason, setReason] = useState<MarketplaceReportReason | ''>('');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (!reason || busy) return;
    setBusy(true);
    setError(null);
    try {
      await report(reason, note);
      setSent(true);
    } catch (err: unknown) {
      setError(marketplaceErrorMessage(tr, err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t('title')} centered>
      {sent ? (
        <div className={styles.section}>
          <p className={styles.ok} role="status">{t('thanks')}</p>
          <div className={styles.actions}>
            <button type="button" className={styles.button} onClick={onClose}>
              {tr('marketplace.copy.close')}
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
          <p className={styles.hint}>{t('hint')}</p>
          <Radio.Group label={t('reason')} value={reason} onChange={(v) => setReason(v as MarketplaceReportReason)} required>
            <div className={styles.section}>
              {REPORT_REASONS.map((r) => (
                <Radio key={r} value={r} label={tr.msg(REPORT_REASON_LABELS[r])} />
              ))}
            </div>
          </Radio.Group>
          <Textarea
            label={t('note')}
            description={t('noteHint', { count: note.length, max: REPORT_NOTE_MAX })}
            value={note}
            onChange={(e) => setNote(e.currentTarget.value.slice(0, REPORT_NOTE_MAX))}
            maxLength={REPORT_NOTE_MAX}
            rows={4}
          />
          {error && <p className={styles.error} role="alert">{error}</p>}
          <div className={styles.actions}>
            <button type="submit" className={styles.buttonPrimary} disabled={!reason || busy}>
              {busy ? t('sending') : t('submit')}
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
