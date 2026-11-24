'use client';

import { Button, Group, Loader, Modal, Stack, Text, VisuallyHidden } from '@mantine/core';
import { IconPaperclip } from '@tabler/icons-react';
import { useAui } from '@assistant-ui/react';
import Link from 'next/link';
import { useCallback, useId, useRef, useState, type ChangeEvent, type ReactElement } from 'react';
import { uploadFile } from '@/lib/api/files';
import { isZipFile, zipsOverLimit } from '@/lib/files/zip-warning';
import { getStorageUsage } from '@/lib/api/storage';
import { withoutAttachId } from '@/lib/assistant/plain-text';
import { useFormat, useT } from '@/i18n';
import { describeError, readableServerMessage } from '@/lib/api/describe-error';
import { enT } from '@/lib/i18n-default';
import { isStorageQuotaError, storageQuotaInfo, storageQuotaMessage } from '@/lib/api/quota';
import { useAssistant } from './AssistantShellProvider';
import classes from './AssistantChatPanel.module.css';

/** The control's accessible name — distinct from the composer's, the send button's and Stop's. */
export const ATTACH_LABEL = enT('assistant.attach.label');

/** Why the control cannot be used, said out loud where a sighted user can read it too. */
export const ATTACH_DISABLED_REASON = enT('assistant.attach.disabledReason');

/**
 * What the assistant is told about a file that has just landed in the project's storage. The
 * project file id is in the text because it is the only handle the assistant's own tools accept —
 * a bare name would leave it guessing which of several same-named files was meant.
 */
export function buildAttachMessage(fileName: string, projectFileId: string): string {
  return `I attached the file "${fileName}" (project file id ${projectFileId}). Use it as context for this conversation.`;
}

export { withoutAttachId };

/**
 * The shell's project, or undefined when there is no shell at all.
 *
 * `useAssistant()` throws outside its provider, and the panel is legitimately rendered without one
 * — its own unit tests do exactly that, and a chat with no page behind it is a real state. No shell
 * means no project, which is the very state this control already renders as disabled. The hook is
 * called on every render either way, so the call order stays stable; only its result is dropped.
 */
function useProjectIdFromShell(): string | undefined {
  try {
    return useAssistant().context.projectId;
  } catch {
    return undefined;
  }
}

export interface AssistantAttachControlProps {
  /**
   * Blocks the control for a reason the COMPOSER owns — currently, that no server thread is bound
   * yet. An attachment is not a passive decoration: once the upload lands it composes a turn and
   * calls `composer.send()`, so this is a send path like the Send button and the suggestion chips,
   * and it has to be closed with them. Without this the pre-thread window would still be open
   * through the file picker — and after a failed `createThread` it would stay open all visit.
   */
  disabled?: boolean;
}

/**
 * Attach a file to the conversation from the composer.
 *
 * The upload goes through the project's EXISTING file endpoint (`uploadFile`), so an attachment is
 * an ordinary `ProjectFile` the rest of the platform already understands — never a second upload
 * transport, and never multipart through the assistant's SSE endpoint. Once the file has landed,
 * the turn that follows names its id and is handed to the ordinary thread runtime, so the assistant
 * sees a normal user message and the conversation is persisted like any other.
 *
 * The visible affordance is a label; the real control is a clipped `<input type="file">` inside it,
 * which is what makes the control keyboard-reachable and file-picker-native rather than a
 * click-only div.
 */
export function AssistantAttachControl({
  disabled = false,
}: AssistantAttachControlProps = {}): ReactElement {
  const t = useT();
  const f = useFormat();
  const projectId = useProjectIdFromShell();
  const aui = useAui();
  const inputId = useId();
  const reasonId = `${inputId}-reason`;

  // A ref, not the state below: two change events dispatched in the same tick both read state that
  // has not re-rendered yet, and the guard has to hold for the SECOND one.
  const busyRef = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  /** True when `error` is the workspace being full: the alert then also offers the Billing page. */
  const [quotaError, setQuotaError] = useState(false);

  // Two independent reasons to be inert — no project to upload into, or no thread to send to. Only
  // the first carries explanatory copy of its own; the second is said by the composer's notice,
  // which sits directly above this control.
  const inert = !projectId || disabled;

  /** The oversized zip waiting for the person's decision, and the limit it was measured against. */
  const [zipPrompt, setZipPrompt] = useState<{ file: File; limitMb: number } | null>(null);

  /** Uploads one file and sends the turn. The caller has already set `busyRef`; this releases it on every exit. */
  const upload = useCallback(
    async (file: File) => {
      if (!projectId) {
        busyRef.current = false;
        return;
      }
      setBusy(true);
      setError(null);
      setQuotaError(false);

      try {
        const uploaded = await uploadFile(projectId, file);
        const turn = buildAttachMessage(file.name, uploaded.id);
        // A draft already in the composer is kept: the user's words are the message, and the
        // attachment is context for them — dropping either would lose something they wrote.
        const draft = aui.composer.getState().text.trim();
        aui.composer.setText(draft ? `${draft}\n\n${turn}` : turn);
        aui.composer.send();
      } catch (cause) {
        // A full workspace is a state, not a network hiccup: it gets the prescribed sentence (and the
        // link the alert renders beside it), never the backend's own wording.
        const quota = isStorageQuotaError(cause);
        setQuotaError(quota);
        setError(
          quota
            ? t('assistant.attach.failed', { name: file.name, reason: storageQuotaMessage(storageQuotaInfo(cause), t, f) })
            : readableServerMessage(cause) !== null
              ? t('assistant.attach.failed', { name: file.name, reason: describeError(t, cause) })
              : t('assistant.attach.tryAgain', { name: file.name }),
        );
      } finally {
        busyRef.current = false;
        setBusy(false);
      }
    },
    [aui, projectId, t, f],
  );

  const handleChange = useCallback(
    async (event: ChangeEvent<HTMLInputElement>) => {
      const input = event.currentTarget;
      const file = input.files?.[0];

      // One upload per pick. A second change while the first is still in flight is the same gesture
      // arriving twice, not a second file; a control with nowhere to put the file, or no thread to
      // send the turn to, takes neither.
      if (!file || !projectId || inert || busyRef.current) {
        input.value = '';
        return;
      }

      // Taken before any await, so a second change event during the limit lookup is refused too.
      busyRef.current = true;
      // Cleared so the SAME file can be attached again: a change event only fires on a change,
      // and leaving the value set makes re-picking that file a silent no-op.
      input.value = '';

      // A zip over the unpack limit is stored but never read: say so, and let the person back out. Advice only,
      // so a failed lookup of the limit just means no warning. Only a zip costs the extra request.
      if (isZipFile(file)) {
        let limitMb: number | null = null;
        try {
          limitMb = (await getStorageUsage()).zipUnpackLimits?.maxMb ?? null;
        } catch {
          limitMb = null;
        }
        if (limitMb && zipsOverLimit([{ name: file.name, size: file.size, type: file.type }], limitMb).length > 0) {
          setZipPrompt({ file, limitMb });
          busyRef.current = false;
          return;
        }
      }

      await upload(file);
    },
    [projectId, inert, upload],
  );

  const closeZipPrompt = useCallback(() => setZipPrompt(null), []);
  const uploadZipAnyway = useCallback(() => {
    const pending = zipPrompt;
    setZipPrompt(null);
    if (!pending || busyRef.current) return;
    busyRef.current = true;
    void upload(pending.file);
  }, [zipPrompt, upload]);

  return (
    <>
      <div
        className={classes.attach}
        data-testid="assistant-attach"
        aria-busy={busy || undefined}
        aria-disabled={inert || undefined}
      >
        <VisuallyHidden>
          <input
            id={inputId}
            type="file"
            aria-label={t('assistant.attach.label')}
            aria-describedby={!projectId ? reasonId : undefined}
            disabled={inert || busy}
            onChange={handleChange}
          />
        </VisuallyHidden>
        {/* Decorative: the input above carries the accessible name, and this only draws it. */}
        <label
          htmlFor={inputId}
          className={classes.attachButton}
          data-disabled={inert || undefined}
          aria-hidden
        >
          {busy ? <Loader size={15} color="violet" /> : <IconPaperclip size={18} stroke={2} />}
        </label>
        {!projectId && (
          <span id={reasonId} className={classes.attachReason}>
            {t('assistant.attach.disabledReason')}
          </span>
        )}
      </div>
      <Modal
        opened={!!zipPrompt} onClose={closeZipPrompt} closeButtonProps={{ size: 44 }} centered
        title={t('assistant.attach.zipTooBigTitle')} data-testid="attach-zip-too-big-modal"
      >
        <Stack>
          <Text size="sm">
            {t('assistant.attach.zipTooBig', { name: zipPrompt?.file.name ?? '', limit: zipPrompt?.limitMb ?? 0 })}
          </Text>
          <Group justify="flex-end">
            <Button variant="default" style={{ minHeight: 44 }} onClick={closeZipPrompt}>{t('assistant.attach.zipCancel')}</Button>
            <Button style={{ minHeight: 44 }} onClick={uploadZipAnyway}>{t('assistant.attach.zipAnyway')}</Button>
          </Group>
        </Stack>
      </Modal>
      {error && (
        <span className={classes.attachError} role="alert">
          {error}
          {quotaError && (
            <>
              {' '}
              {/* next/link, so the dashboard's '/app' basePath is applied (a raw anchor left the app). */}
              <Link href="/settings/billing">{t('assistant.attach.seePlans')}</Link>
            </>
          )}
        </span>
      )}
    </>
  );
}
