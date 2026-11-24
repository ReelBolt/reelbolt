import { useT } from '@/i18n';
import { Section } from '@/components/layout/Section';
import type { Persona } from '@/lib/attribution-persona';
import { PROOF, type SampleMedia } from '@/lib/proof/registry';
import { beforeAfterSample, clockTime } from '@/lib/proof/validate';

export interface BeforeAfterSide {
  /** One sentence for the whole side. */
  summary: string;
  points: readonly string[];
}

function Clip({ media, label }: { media: SampleMedia; label: string }) {
  // No autoplay and nothing fetched until the visitor presses play: the poster is the only request.
  return (
    <video
      controls
      playsInline
      preload="none"
      poster={media.posterSrc}
      aria-label={label}
      className="aspect-video w-full border border-line bg-paper object-contain"
    >
      <source src={media.videoSrc} />
    </video>
  );
}

function Points({ items }: { items: readonly string[] }) {
  return (
    <ul className="mt-5 space-y-2 text-sm text-ink-muted">
      {items.map((item) => (
        <li key={item} className="flex items-start gap-3">
          <span aria-hidden="true" className="mt-1.5 h-2 w-2 shrink-0 bg-accent" />
          <span>{item}</span>
        </li>
      ))}
    </ul>
  );
}

/**
 * Before and after, in words. The two sides are qualitative on purpose: no number appears unless
 * it is a real fact of a real run.
 *
 * When the proof registry holds a signed-off sample that carries its raw footage (for this
 * `persona`, or any sample when no persona is given), the section also shows that real pair, the
 * raw clip under "Before" and the finished video under "After", with the measured facts of that
 * run. The registry ships empty, so today the section is words only.
 */
export function BeforeAfter({
  id = 'before-after',
  title,
  intro,
  before,
  after,
  persona,
}: {
  id?: string;
  title: string;
  intro?: string;
  before: BeforeAfterSide;
  after: BeforeAfterSide;
  persona?: Persona;
}) {
  const t = useT('proof.beforeAfter');
  const sample = beforeAfterSample(PROOF, persona);
  return (
    <Section id={id} className="bg-paper-2">
      <div className="max-w-3xl">
        <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">{title}</h2>
        {intro && <p className="mt-4 text-ink-muted">{intro}</p>}
      </div>
      <div className="mt-10 grid grid-cols-1 gap-6 md:grid-cols-2">
        <div className="bg-paper p-6">
          <p className="font-mono text-[11px] uppercase tracking-eyebrow text-ink-muted">{t('before')}</p>
          {sample && (
            <div className="mt-4">
              <Clip media={sample.before} label={t('rawLabel', { title: sample.title })} />
            </div>
          )}
          <p className="mt-4 font-display text-xl font-bold text-ink-2">{before.summary}</p>
          <Points items={before.points} />
        </div>
        <div className="border border-accent bg-paper p-6">
          <p className="font-mono text-[11px] uppercase tracking-eyebrow text-accent-ink">{t('after')}</p>
          {sample && (
            <div className="mt-4">
              <Clip media={sample} label={t('finishedLabel', { title: sample.title })} />
            </div>
          )}
          <p className="mt-4 font-display text-xl font-bold text-ink">{after.summary}</p>
          <Points items={after.points} />
        </div>
      </div>
      {sample && (
        <div className="mt-6 font-mono text-xs text-ink-muted">
          <p>{t('madeWith', { title: sample.title, recipe: sample.recipe })}</p>
          {sample.facts && (
            <ul className="mt-2 flex flex-wrap gap-x-6 gap-y-1">
              <li>{t('facts.source', { time: clockTime(sample.facts.sourceSeconds) })}</li>
              <li>{t('facts.output', { time: clockTime(sample.facts.outputSeconds) })}</li>
              <li>{t('facts.cuts', { count: sample.facts.cuts })}</li>
            </ul>
          )}
        </div>
      )}
    </Section>
  );
}
