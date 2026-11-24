'use client';

import React, { useState } from 'react';
import { Button, Text, Textarea } from '@mantine/core';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { compileMergeCaptionWithNext, compileSplitCaption } from '@/lib/editing/subtitles/subtitles';
import { BottomSheet, type SheetDetent } from './BottomSheet';
import { WordPicker } from './WordPicker';
import { captionRows, compileDeleteCaption, compileNewCaption, planGenerate } from './captions-logic';
import { useTranscriptWords } from './use-transcript-words';
import { showUndoSnackbar } from './UndoSnackbar';
import classes from './phone.module.css';
import { useT } from '@/i18n';
import { useHelperText } from './helper-text';
import { TranscriptStart } from '../panels/TranscriptStart';

const TAP = { minHeight: 44, minWidth: 44 } as const;

export interface CaptionsSheetProps {
  doc: EditTimeline;
  projectId: string;
  seedStepResultId: string | null;
  readOnly: boolean;
  playheadFrame: number;
  newItemId: () => string;
  newTrackId: () => string;
  /** One batch = one undo step. */
  onApply: (ops: readonly EditOp[], label: string) => void;
  onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => void;
  onSeek: (frame: number) => void;
  onUndo: () => void;
  /** Opens the Remove silence dialog (the desktop one, full screen on a phone). */
  onRemoveSilence: () => void;
  /** Opens the Cut to the music dialog. */
  onCutToBeat: () => void;
  /** The desktop panels (analysis, transcript, markers, edits, looks, SRT import): kept one tap away under "More tools". */
  moreTools?: React.ReactNode;
}

type Editing = { kind: 'edit'; id: string; text: string } | { kind: 'new'; text: string };

/**
 * The Captions sheet (RM.12, R09.5): the captions on the timeline with a full-width edit field, split at the
 * playhead, merge with the next and delete; generate captions from speech; one-tap Remove silence; and Delete
 * words. Takes, compare, the cut workspace and stills are not offered on a phone.
 */
export function CaptionsSheet({ doc, projectId, seedStepResultId, readOnly, playheadFrame, newItemId, newTrackId, onApply, onSetProperty, onSeek, onUndo, onRemoveSilence, onCutToBeat, moreTools }: CaptionsSheetProps) {
  const show = useHelperText();
  const t = useT('editor.phone.captionsSheet');
  const tBeat = useT('editor.panels.cutToBeatDialog');
  const rows = captionRows(doc, playheadFrame);
  const transcript = useTranscriptWords(doc, projectId, seedStepResultId);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [editDetent, setEditDetent] = useState<SheetDetent>('full');
  const [wordsOpen, setWordsOpen] = useState(false);
  const [wordsDetent, setWordsDetent] = useState<SheetDetent>('full');
  const [moreOpen, setMoreOpen] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);

  const cleaned = editing?.text.trim() ?? '';
  const done = () => {
    if (!editing) return;
    if (cleaned.length === 0) {
      setProblem('A caption cannot be empty. Delete it instead.');
      return;
    }
    if (editing.kind === 'edit') {
      const current = rows.find((r) => r.id === editing.id);
      if (current && current.text !== cleaned) onSetProperty(editing.id, 'text', cleaned, t('editCaptionText'));
    } else {
      const plan = compileNewCaption(doc, playheadFrame, cleaned, newItemId, newTrackId);
      if (plan.refusal) {
        setProblem(plan.refusal);
        return;
      }
      onApply(plan.ops, t('addCaption'));
      setMessage(t('captionAddedAtThe'));
    }
    setProblem(null);
    setEditing(null);
  };
  const cancelEdit = () => {
    setProblem(null);
    setEditing(null);
  };

  const generate = () => {
    const track = transcript.mediaTracks[0]?.id;
    if (!track) {
      setMessage(t('addAVideoOr'));
      return;
    }
    const plan = planGenerate(doc, transcript.onTrack(track), track, transcript.fps, transcript.matches, newItemId, newTrackId);
    if (plan.ops.length > 0) onApply(plan.ops, t('generateCaptions'));
    setMessage(plan.message);
  };
  const hasSpeech = transcript.all.length > 0;
  const captionTracks = doc.tracks.filter((tr) => tr.items.some((i) => i.type === 'caption'));

  return (
    <div className={classes.sheetStack} data-testid="phone-captions-sheet">
      <section className={classes.sheetStack} style={{ padding: 0 }} aria-label={t('quickFixes')}>
        <Text size="sm" fw={600}>
          {t('quickFixes2')}
        </Text>
        <div className={classes.sheetRow} style={{ gap: 12 }}>
          <Button variant="default" h={44} disabled={readOnly} onClick={onRemoveSilence} data-testid="phone-captions-remove-silence">
            {t('removeSilence')}
          </Button>
          <Button variant="default" h={44} disabled={readOnly} onClick={onCutToBeat} data-testid="phone-captions-cut-to-beat">
            {tBeat('cutToTheMusic')}
          </Button>
          <Button variant="default" h={44} disabled={readOnly} onClick={() => setWordsOpen(true)} data-testid="phone-captions-delete-words">
            {t('deleteWords')}
          </Button>
        </div>
      </section>

      <section className={classes.sheetStack} style={{ padding: 0 }} aria-label={t('captions')}>
        <Text size="sm" fw={600}>
          {t('captions2', { length: rows.length })}
        </Text>
        <div className={classes.sheetRow} style={{ gap: 12 }}>
          <Button variant="default" h={44} disabled={readOnly} onClick={() => setEditing({ kind: 'new', text: '' })} data-testid="phone-captions-add">
            {t('addAtThePlayhead')}
          </Button>
          <Button variant="default" h={44} disabled={readOnly || !hasSpeech} onClick={generate} data-testid="phone-captions-generate">
            {t('generateFromSpeech')}
          </Button>
        </div>
        {!hasSpeech && !transcript.isLoading && (
          <div data-testid="phone-captions-no-speech">
            <TranscriptStart doc={doc} projectId={projectId} readOnly={readOnly} tapStyle={TAP} emptyText={t('generateNeedsTheSpeech')} />
          </div>
        )}
        {captionTracks.map((tr) => {
          const shown = tr.enabled !== false;
          return (
            <Button
              key={tr.id}
              variant={shown ? 'filled' : 'default'}
              h={44}
              aria-pressed={shown}
              disabled={readOnly}
              onClick={() => onApply([{ type: 'setTrackProperty', trackId: tr.id, key: 'enabled', value: !shown }], t('captionsOnPicture'))}
              data-testid={`phone-captions-lane-${tr.id}`}
            >
              {`${t('captionsOnPicture')}: ${shown ? t('captionsShown') : t('captionsHidden')}`}
            </Button>
          );
        })}
        {rows.length === 0 ? (
          <Text size="sm" c="dimmed" data-testid="phone-captions-none">
            {t('noCaptionsYet')}
          </Text>
        ) : (
          <>
            <Text size="xs" c="dimmed">
              {t('toSplitACaption')}
            </Text>
            {rows.map((row) => (
              <div key={row.id} className={classes.captionRow} data-testid={`phone-caption-${row.id}`}>
                <Button variant="subtle" h={44} px={8} onClick={() => onSeek(row.startFrame)} aria-label={t('goToTheCaption', { timecode: row.timecode })} data-testid={`phone-caption-time-${row.id}`}>
                  {row.timecode}
                </Button>
                <Text size="sm" className={classes.captionText} data-testid={`phone-caption-text-${row.id}`}>
                  {row.text.replace(/\n/g, ' / ')}
                </Text>
                <div className={classes.captionActions}>
                  <Button variant="default" h={44} px={10} disabled={readOnly} onClick={() => setEditing({ kind: 'edit', id: row.id, text: row.text })} data-testid={`phone-caption-edit-${row.id}`}>
                    {t('edit')}
                  </Button>
                  <Button variant="default" h={44} px={10} disabled={readOnly || !row.splittable} onClick={() => onApply(compileSplitCaption(doc, row.id, playheadFrame, newItemId()), t('splitCaption'))} data-testid={`phone-caption-split-${row.id}`}>
                    {t('split')}
                  </Button>
                  <Button variant="default" h={44} px={10} disabled={readOnly || !row.mergeable} onClick={() => onApply(compileMergeCaptionWithNext(doc, row.id), t('mergeCaptions'))} data-testid={`phone-caption-merge-${row.id}`}>
                    {t('mergeWithNext')}
                  </Button>
                  <Button
                    variant="light"
                    color="red"
                    h={44}
                    px={10}
                    disabled={readOnly}
                    onClick={() => {
                      onApply(compileDeleteCaption(row.id), t('deleteCaption'));
                      showUndoSnackbar({ message: t('captionDeleted'), onUndo });
                    }}
                    data-testid={`phone-caption-delete-${row.id}`}
                  >
                    {t('delete')}
                  </Button>
                </div>
              </div>
            ))}
          </>
        )}
        {message && (
          <Text size="sm" role="status" data-testid="phone-captions-message">
            {show(message)}
          </Text>
        )}
      </section>

      {moreTools && (
        <section aria-label={t('moreTools')}>
          <Button variant="subtle" h={44} onClick={() => setMoreOpen((o) => !o)} aria-expanded={moreOpen} data-testid="phone-captions-more">
            {moreOpen ? t('hideMoreTools') : t('moreToolsAnalysisTranscript')}
          </Button>
          {moreOpen && <div data-testid="phone-captions-more-body">{moreTools}</div>}
        </section>
      )}

      {editing && (
        <BottomSheet key="caption-edit" opened onClose={cancelEdit} title={editing.kind === 'new' ? t('newCaption') : t('editCaption')} detent={editDetent} onDetentChange={setEditDetent} testId="caption-edit-sheet">
          <div className={classes.sheetStack}>
            <Textarea
              rows={5}
              label={t('captionText')}
              value={editing.text}
              autoFocus
              onChange={(e) => {
                const text = e.currentTarget.value;
                setEditing((cur) => (cur ? { ...cur, text } : cur));
              }}
              styles={{ input: { fontSize: 16 } }}
              data-testid="caption-edit-text"
            />
            {problem && (
              <p className={classes.sheetProblem} role="alert" data-testid="caption-edit-problem">
                {show(problem)}
              </p>
            )}
            <div className={classes.sheetRow} style={{ gap: 12 }}>
              <Button variant="default" h={44} onClick={cancelEdit} data-testid="caption-edit-cancel">
                {t('cancel')}
              </Button>
              <Button h={44} onClick={done} data-testid="caption-edit-done">
                {t('done')}
              </Button>
            </div>
          </div>
        </BottomSheet>
      )}

      {wordsOpen && (
        <BottomSheet key="delete-words" opened onClose={() => setWordsOpen(false)} title={t('deleteWords2')} detent={wordsDetent} onDetentChange={setWordsDetent} testId="delete-words-sheet">
          <WordPicker doc={doc} projectId={projectId} seedStepResultId={seedStepResultId} readOnly={readOnly} newItemId={newItemId} onApply={onApply} onSeek={onSeek} onDone={() => setWordsOpen(false)} />
        </BottomSheet>
      )}
    </div>
  );
}
