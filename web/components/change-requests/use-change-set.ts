'use client';

import { useCallback, useState } from 'react';
import { EMPTY_FILM_DRAFT, type ChangeEntry, type FieldsChange, type FilmDraft } from './change-set';

/**
 * The changes waiting to be sent together, one per step. Adding a step that is already there replaces it. `fields` is the
 * app's fields and run settings changed in the same request (docs/design/app-inputs.md 29), or null. `film` is the video's
 * own draft (its note, scenes, cut and start version); a non-empty note counts as one change in the set.
 */
export function useChangeSet() {
  const [entries, setEntries] = useState<ChangeEntry[]>([]);
  const [fields, setFields] = useState<FieldsChange | null>(null);
  const [film, setFilm] = useState<FilmDraft>(EMPTY_FILM_DRAFT);
  const upsert = useCallback((entry: ChangeEntry) => {
    setEntries((prev) => (prev.some((e) => e.stepOrder === entry.stepOrder)
      ? prev.map((e) => (e.stepOrder === entry.stepOrder ? entry : e))
      : [...prev, entry]));
  }, []);
  const remove = useCallback((stepOrder: number) => setEntries((prev) => prev.filter((e) => e.stepOrder !== stepOrder)), []);
  const patchFilm = useCallback((patch: Partial<FilmDraft>) => setFilm((prev) => ({ ...prev, ...patch })), []);
  const clear = useCallback(() => { setEntries([]); setFields(null); setFilm(EMPTY_FILM_DRAFT); }, []);
  return { entries, upsert, remove, clear, fields, setFields, film, patchFilm };
}

export type ChangeSetState = ReturnType<typeof useChangeSet>;
