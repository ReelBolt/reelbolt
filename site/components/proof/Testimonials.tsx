import { useT } from '@/i18n';
import { Section } from '@/components/layout/Section';
import { PROOF } from '@/lib/proof/registry';
import { testimonialsOf } from '@/lib/proof/validate';

/**
 * What real customers said, each with written permission on file (`permissionRef`, never shown).
 * Renders NOTHING while the registry is empty, which is the case today: ReelBolt has no customer
 * testimonials, and none may be written for it. The quote is printed verbatim, in the language it
 * was said in.
 */
export function Testimonials({ id = 'in-their-words' }: { id?: string }) {
  const t = useT('proof.testimonials');
  const testimonials = testimonialsOf(PROOF);
  if (testimonials.length === 0) return null;
  return (
    <Section id={id}>
      <div className="max-w-3xl">
        <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">{t('title')}</h2>
      </div>
      <ul className="mt-10 grid grid-cols-1 gap-6 md:grid-cols-2">
        {testimonials.map((testimonial) => (
          <li key={`${testimonial.name}:${testimonial.date}`}>
            <figure className="h-full bg-paper-2 p-6">
              <blockquote className="text-ink">
                <p>{testimonial.quote}</p>
              </blockquote>
              <figcaption className="mt-4 font-mono text-xs text-ink-muted">
                <span className="block text-ink-2">{testimonial.name}</span>
                <span className="block">{testimonial.role}</span>
              </figcaption>
            </figure>
          </li>
        ))}
      </ul>
    </Section>
  );
}
