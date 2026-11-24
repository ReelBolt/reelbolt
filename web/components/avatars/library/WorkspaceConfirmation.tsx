'use client';

import { useCallback, useMemo, useState, type ReactNode } from 'react';
import { Alert, Checkbox, NumberInput, Stack } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { defaultStatementLocale, statementLocales } from '@/lib/avatars/model';
import { libraryStatements, retentionDaysOk, statementsFromRefusal, type LibraryLimits } from '@/lib/avatars/library';
import type { AvatarLibraryRequirements, AvatarSubjectKind } from '@/lib/types/avatars';
import { useFormat, useT } from '@/i18n';
import { StatementBlock } from '../ReferenceParts';

/** What a workspace-wide confirmation sends: the statement verbatim, and for a person the adult tick and the keep-for days. */
export interface WorkspaceConfirmationBody {
  consentStatement: string;
  consentLocale: string;
  subjectIsAdult?: boolean;
  retentionDays?: number;
}

export interface WorkspaceConfirmation {
  kind: AvatarSubjectKind;
  statement: string;
  locale: string;
  locales: string[];
  attested: boolean;
  adult: boolean;
  retentionDays: number;
  setLocale: (locale: string) => void;
  setAttested: (attested: boolean) => void;
  setAdult: (adult: boolean) => void;
  setRetentionDays: (days: number) => void;
  /** The server answered a refusal with a newer statement: it is now the one shown, and the tick was cleared. */
  refreshed: boolean;
  /** Every field is complete, so the confirmation can be sent. */
  ready: boolean;
  body: WorkspaceConfirmationBody;
  /**
   * Reads a refused call. When the refusal carries the server's statement texts they replace the ones shown
   * and the tick is cleared (the person confirms the text they read); answers whether that happened.
   */
  acceptRefusal: (err: unknown) => boolean;
}

/**
 * The state of one workspace-wide confirmation: the statement of the kind (a person's consent or an object's
 * statement for every project of the workspace), the language it is read in and its tick, and for a person the
 * 18-or-older tick and the keep-for days. Used when saving to the library and when confirming an item again.
 */
export function useWorkspaceConfirmation({ kind, library, limits, locales: listed }: {
  kind: AvatarSubjectKind;
  library: Pick<AvatarLibraryRequirements, 'consentStatements' | 'objectStatements'> | null;
  limits: LibraryLimits;
  /** The languages in the server's order, when a project's requirements listed them. */
  locales?: readonly string[];
}): WorkspaceConfirmation {
  const fm = useFormat();
  const person = kind === 'Person';
  const [served, setServed] = useState<Record<string, string> | null>(null);
  const statements = useMemo(() => served ?? libraryStatements(library, kind), [served, library, kind]);
  const locales = useMemo(() => statementLocales(served ? [] : listed ?? [], statements), [served, listed, statements]);
  const [picked, setPicked] = useState<string | null>(null);
  const locale = picked && statements[picked] ? picked : defaultStatementLocale(fm.locale, locales, statements);
  const [attested, setAttested] = useState(false);
  const [adult, setAdult] = useState(false);
  const [retentionDays, setRetentionDays] = useState<number>(limits.defaultRetentionDays);

  const statement = statements[locale] ?? '';
  const ready = statement.length > 0 && attested && (!person || (adult && retentionDaysOk(retentionDays, limits)));
  const body = useMemo<WorkspaceConfirmationBody>(() => ({
    consentStatement: statement,
    consentLocale: locale,
    ...(person ? { subjectIsAdult: adult, retentionDays } : {}),
  }), [statement, locale, person, adult, retentionDays]);

  const acceptRefusal = useCallback((err: unknown) => {
    if (!(err instanceof ApiError)) return false;
    if (err.code !== 'consent_statement_required' && err.code !== 'object_statement_required') return false;
    const texts = statementsFromRefusal(err.body);
    if (!texts) return false;
    setServed(texts);
    setAttested(false);
    return true;
  }, []);

  return {
    kind, statement, locale, locales, attested, adult, retentionDays,
    setLocale: setPicked, setAttested, setAdult, setRetentionDays,
    refreshed: served !== null, ready, body, acceptRefusal,
  };
}

/** The fields of a workspace-wide confirmation: the statement with its language switch and tick, then a person's two fields. */
export function WorkspaceConfirmationFields({ confirmation: c, limits, about }: {
  confirmation: WorkspaceConfirmation;
  limits: LibraryLimits;
  /** An optional line above the statement (who it is about). */
  about?: ReactNode;
}) {
  const t = useT('avatars.library.confirmation');
  const tForm = useT('avatars.form');
  const person = c.kind === 'Person';
  if (!c.statement) {
    return <Alert color="red" variant="light" data-testid="library-no-statement">{t('noStatement')}</Alert>;
  }
  return (
    <Stack gap="sm">
      {c.refreshed && <Alert color="yellow" variant="light" data-testid="library-statement-changed">{t('statementChanged')}</Alert>}
      <StatementBlock
        title={person ? t('consentTitle') : t('objectTitle')} testId="library-statement" about={about}
        statement={c.statement} locale={c.locale} locales={c.locales} onLocale={c.setLocale}
        attested={c.attested} onAttested={c.setAttested}
      />
      {person && (
        <>
          <Checkbox label={tForm('adult')} checked={c.adult} onChange={(e) => c.setAdult(e.currentTarget.checked)} />
          <NumberInput
            label={tForm('keepFor')} description={t('keepForHint', { min: limits.minRetentionDays, max: limits.maxRetentionDays })}
            min={limits.minRetentionDays} max={limits.maxRetentionDays} allowDecimal={false} required
            value={c.retentionDays} onChange={(v) => c.setRetentionDays(typeof v === 'number' ? v : Number.NaN)}
            error={retentionDaysOk(c.retentionDays, limits) ? undefined : tForm('keepForInvalid', { min: limits.minRetentionDays, max: limits.maxRetentionDays })}
          />
        </>
      )}
    </Stack>
  );
}
