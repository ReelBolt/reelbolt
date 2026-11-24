import { useT } from '@/i18n';
import { Section } from '@/components/layout/Section';
import type { Persona } from '@/lib/attribution-persona';
import { PROOF } from '@/lib/proof/registry';
import { samplesFor } from '@/lib/proof/validate';

/**
 * "Made with ReelBolt": the signed-off sample outputs, all of them or those of one persona.
 * Renders NOTHING while the registry has no matching entry, which is the case today. There is no
 * placeholder and no "coming soon": a gallery with invented or unapproved videos would be a false
 * claim, and an empty frame would promise something that does not exist yet.
 */
export function SampleGallery({ id = 'made-with-reelbolt', persona }: { id?: string; persona?: Persona }) {
  const t = useT('proof.samples');
  const samples = samplesFor(PROOF, persona);
  if (samples.length === 0) return null;
  return (
    <Section id={id}>
      <div className="max-w-3xl">
        <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">{t('title')}</h2>
        <p className="mt-4 text-ink-muted">{t('intro')}</p>
      </div>
      <ul className="mt-10 grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-3">
        {samples.map((sample) => (
          <li key={sample.id}>
            <figure>
              <video
                controls
                playsInline
                preload="none"
                poster={sample.posterSrc}
                aria-label={t('videoLabel', { title: sample.title })}
                className="aspect-video w-full border border-line bg-paper-2 object-contain"
              >
                <source src={sample.videoSrc} />
              </video>
              <figcaption className="mt-3">
                <span className="block font-display text-base font-bold text-ink">{sample.title}</span>
                <span className="mt-1 block font-mono text-xs text-ink-muted">{t('recipe', { recipe: sample.recipe })}</span>
              </figcaption>
            </figure>
          </li>
        ))}
      </ul>
    </Section>
  );
}
