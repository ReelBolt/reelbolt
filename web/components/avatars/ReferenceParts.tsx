'use client';

import type { ReactNode } from 'react';
import { Blockquote, Checkbox, Group, ScrollArea, SegmentedControl, Select, Stack, Text } from '@mantine/core';
import { kindOfMime, maxBytesFor, type PickedReference } from '@/lib/avatars/model';
import type { AvatarLimits, AvatarReferenceKind, AvatarReferenceRole, AvatarRequirements } from '@/lib/types/avatars';
import type { ProjectFile } from '@/lib/types/project';
import { formatBytes } from '@/lib/utils/bytes';
import { useFormat, useT } from '@/i18n';

/**
 * The files a person can tick for an avatar of either kind, each with what it shows. The caller decides which
 * files are offered and which roles a kind may take (a person's or an object's); nothing here knows the kind.
 */
export function ReferenceFilePicker({ files, picked, requirements, limits, rolesOf, onToggle, onRole, maxHeight }: {
  /** The files offered, already filtered and sorted. */
  files: readonly ProjectFile[];
  picked: readonly PickedReference[];
  requirements: Pick<AvatarRequirements, 'mimeTypes'>;
  limits: AvatarLimits;
  rolesOf: (kind: AvatarReferenceKind) => readonly AvatarReferenceRole[];
  onToggle: (fileId: string) => void;
  onRole: (fileId: string, role: AvatarReferenceRole) => void;
  /** The list scrolls past this height; undefined lets it grow (a phone, where the modal itself scrolls). */
  maxHeight?: number;
}) {
  const t = useT('avatars.form');
  const tAv = useT('avatars');
  const fm = useFormat();
  return (
    <ScrollArea.Autosize mah={maxHeight} type="auto">
      <Stack gap={6} data-testid="avatar-file-list">
        {files.map((f) => {
          const kind = kindOfMime(f.mimeType, requirements);
          if (!kind) return null;
          const pick = picked.find((p) => p.fileId === f.id);
          const tooBig = f.sizeBytes > maxBytesFor(kind, limits);
          return (
            <Group key={f.id} wrap="wrap" gap="xs" align="center" style={{ minHeight: 44 }}>
              <Checkbox
                checked={Boolean(pick)} onChange={() => onToggle(f.id)} style={{ flex: '1 1 220px', minWidth: 0 }}
                label={<Text size="sm" truncate>{f.originalFileName}</Text>}
                description={`${tAv(`kinds.${kind}`)} · ${formatBytes(f.sizeBytes, fm)}${tooBig ? ` · ${t('tooBigFile')}` : ''}`}
                aria-label={t('selectFile', { name: f.originalFileName })}
              />
              {pick && (
                <Select
                  aria-label={t('roleFor', { name: f.originalFileName })} size="sm" w={190} allowDeselect={false}
                  data={rolesOf(kind).map((r) => ({ value: r, label: tAv(`roles.${r}`) }))}
                  value={pick.role} onChange={(v) => v && onRole(f.id, v as AvatarReferenceRole)}
                />
              )}
            </Group>
          );
        })}
      </Stack>
    </ScrollArea.Autosize>
  );
}

/**
 * A statement the server serves, shown verbatim in the language the person picks, with the tick that confirms
 * it. Changing the language clears the tick: the person confirms the text they are reading.
 */
export function StatementBlock({ title, about, statement, locale, locales, onLocale, attested, onAttested, testId }: {
  title: string;
  /** An optional line above the statement (who it is about). */
  about?: ReactNode;
  statement: string;
  locale: string;
  /** The languages the statement is served in; the switch shows only with more than one. */
  locales: readonly string[];
  onLocale: (locale: string) => void;
  attested: boolean;
  onAttested: (attested: boolean) => void;
  testId: string;
}) {
  const t = useT('avatars.form');
  return (
    <Stack gap={6}>
      <Group justify="space-between" wrap="wrap" gap="xs">
        <Text size="sm" fw={600}>{title}</Text>
        {locales.length > 1 && (
          <SegmentedControl
            size="sm" value={locale} aria-label={t('statementLanguage')}
            onChange={(v) => { onLocale(v); onAttested(false); }}
            data={locales.map((l) => ({ value: l, label: t.has(`languages.${l}`) ? t(`languages.${l}`) : l }))}
          />
        )}
      </Group>
      {about}
      <Blockquote color="violet" p="sm" data-testid={testId} lang={locale} style={{ whiteSpace: 'pre-wrap', fontSize: 'var(--mantine-font-size-sm)' }}>
        {statement}
      </Blockquote>
      <Checkbox label={t('attest')} checked={attested} onChange={(e) => onAttested(e.currentTarget.checked)} />
    </Stack>
  );
}
