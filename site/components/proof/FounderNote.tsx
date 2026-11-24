import { useT } from '@/i18n';
import { Section } from '@/components/layout/Section';
import { PROOF } from '@/lib/proof/registry';
import { founderNoteOf } from '@/lib/proof/validate';

/**
 * A short note in the founder's own words. Renders NOTHING until the founder has written and
 * approved one (`lib/proof/registry`): nobody drafts it on their behalf.
 */
export function FounderNote({ id = 'founder-note' }: { id?: string }) {
  const t = useT('proof.founder');
  const note = founderNoteOf(PROOF);
  if (!note) return null;
  return (
    <Section id={id}>
      <div className="max-w-3xl">
        <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">{t('title')}</h2>
        <figure className="mt-6">
          <blockquote className="space-y-4 text-ink-2">
            <p className="whitespace-pre-line">{note.text}</p>
          </blockquote>
          <figcaption className="mt-4 font-mono text-xs text-ink-muted">{note.approvedBy}</figcaption>
        </figure>
      </div>
    </Section>
  );
}
