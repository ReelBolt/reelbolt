import { Container } from '@/components/layout/Container';
import { Eyebrow } from '@/components/ui/Eyebrow';
import { Button } from '@/components/ui/Button';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { Link } from '@/i18n/link';
import { useT } from '@/i18n';
import { costKey, formatSeconds, MIN_RATINGS_FOR_AVERAGE, type RecipeDetail } from '@/lib/recipes/types';
import { categoryName, fieldName, limitText, stepTypeName } from './labels';
import { PreviewVideo } from './PreviewVideo';

function Heading({ children }: { children: React.ReactNode }) {
  return <h2 className="font-display text-2xl font-bold text-ink">{children}</h2>;
}

/**
 * One recipe. Name, description, labels and prompts are the author's own words: shown as plain text nodes,
 * never translated, never HTML or markdown, never linkified. Everything around them is translated.
 */
export function RecipeDetailBody({ recipe }: { recipe: RecipeDetail }) {
  const t = useT('recipes');
  const rated = recipe.ratingAverage !== null && recipe.ratingCount >= MIN_RATINGS_FOR_AVERAGE;
  const costId = costKey(recipe.costClass);
  const hasNeeds = recipe.inputs.length > 0 || recipe.slots.length > 0;

  return (
    <>
      <div className="pt-12 pb-10 sm:pt-16">
        <Container>
          <Link
            href="/recipes"
            className="inline-flex min-h-11 items-center font-mono text-xs uppercase tracking-eyebrow text-ink-muted hover:text-ink"
          >
            {t('detail.back')}
          </Link>
          <Eyebrow className="mt-4">
            {recipe.category !== '' ? categoryName(t, recipe.category) : t('page.eyebrow')}
          </Eyebrow>
          <h1 className="mt-4 break-words font-display text-[clamp(2rem,5vw,3.25rem)] font-bold leading-[1] text-ink">
            {recipe.name}
          </h1>
          {recipe.authorLabel !== '' && (
            <p className="mt-3 break-words text-ink-muted">{t('detail.by', { author: recipe.authorLabel })}</p>
          )}
        </Container>
      </div>

      <div className="py-12 sm:py-16">
        <Container>
          <div className="grid gap-10 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
            <div className="space-y-12">
              <PreviewVideo id={recipe.id} name={recipe.name} hasPreview={recipe.previewSeconds > 0} mode="tap" />
              {recipe.description !== '' && (
                <p className="max-w-3xl whitespace-pre-line break-words text-lg text-ink-muted">{recipe.description}</p>
              )}

              {recipe.steps.length > 0 && (
                <section aria-labelledby="recipe-steps">
                  <h2 id="recipe-steps" className="font-display text-2xl font-bold text-ink">
                    {t('detail.stepsTitle')}
                  </h2>
                  <p className="mt-2 text-ink-muted">{t('detail.stepsIntro')}</p>
                  <ol className="mt-6 divide-y divide-line">
                    {recipe.steps.map((step) => (
                      <li key={`${step.order}-${step.type}`} className="flex gap-4 p-4">
                        <span className="font-mono text-sm text-ink-muted">{String(step.order).padStart(2, '0')}</span>
                        <div className="min-w-0">
                          <p className="font-display font-bold text-ink">{stepTypeName(t, step.type)}</p>
                          {step.label !== '' && <p className="break-words text-ink-muted">{step.label}</p>}
                          {step.limits.some((l) => limitText(t, l) !== null) && (
                            <ul aria-label={t('detail.limitsLabel')} className="mt-1 flex flex-wrap gap-2">
                              {step.limits.flatMap((limit) => {
                                const text = limitText(t, limit);
                                return text === null
                                  ? []
                                  : [
                                      <li key={limit} className="break-words border border-line px-2 py-0.5 font-mono text-xs text-ink-muted">
                                        {text}
                                      </li>,
                                    ];
                              })}
                            </ul>
                          )}
                        </div>
                      </li>
                    ))}
                  </ol>
                </section>
              )}

              <section aria-labelledby="recipe-needs">
                <h2 id="recipe-needs" className="font-display text-2xl font-bold text-ink">
                  {t('detail.needTitle')}
                </h2>
                {hasNeeds ? (
                  <div className="mt-6 grid gap-6 sm:grid-cols-2">
                    {recipe.inputs.length > 0 && (
                      <div>
                        <p className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">{t('detail.needInputs')}</p>
                        <ul className="mt-3 space-y-2">
                          {recipe.inputs.map((input) => (
                            <li key={input.key} className="break-words text-ink">
                              {input.label !== '' ? input.label : t('field.other')}
                            </li>
                          ))}
                        </ul>
                      </div>
                    )}
                    {recipe.slots.length > 0 && (
                      <div>
                        <p className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">{t('detail.needSlots')}</p>
                        <ul className="mt-3 space-y-2">
                          {recipe.slots.map((slot) => (
                            <li key={`${slot.kind}-${slot.stepOrder}-${slot.label}`} className="break-words text-ink">
                              {t.has(`slot.${slot.kind}`) ? t(`slot.${slot.kind}`) : t('slot.other')}
                              {slot.label !== '' && <span className="text-ink-muted">{` ${slot.label}`}</span>}
                            </li>
                          ))}
                        </ul>
                      </div>
                    )}
                  </div>
                ) : (
                  <p className="mt-3 text-ink-muted">{t('detail.needNone')}</p>
                )}
              </section>

              {recipe.prompts.length > 0 && (
                <section aria-labelledby="recipe-prompts">
                  <h2 id="recipe-prompts" className="font-display text-2xl font-bold text-ink">
                    {t('detail.promptsTitle')}
                  </h2>
                  <p className="mt-2 text-ink-muted">{t('detail.promptsNote')}</p>
                  <ul className="mt-6 space-y-4">
                    {recipe.prompts.map((prompt) => (
                      <li key={`${prompt.stepOrder}-${prompt.field}`} className="bg-paper-2 p-4">
                        <p className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
                          {t('detail.promptStep', { step: prompt.stepOrder })}
                          {prompt.field !== '' && <span>{` · ${fieldName(t, prompt.field)}`}</span>}
                        </p>
                        <p className="mt-2 whitespace-pre-wrap break-words font-mono text-sm text-ink">{prompt.text}</p>
                      </li>
                    ))}
                  </ul>
                </section>
              )}
            </div>

            <aside className="space-y-6 lg:sticky lg:top-28 lg:self-start" aria-label={t('detail.stats')}>
              <div className="bg-paper-2 p-6">
                <Button href={`/app/community/${recipe.id}`} variant="accent" size="lg" block>
                  {t('detail.copy')}
                </Button>
                <p className="mt-3 text-sm text-ink-muted">{t('detail.copyHint')}</p>
              </div>
              <dl className="divide-y divide-line">
                <div className="flex justify-between gap-4 p-4">
                  <dt className="text-ink-muted">{t('detail.stepsTitle')}</dt>
                  <dd className="font-mono text-ink">{recipe.stepCount}</dd>
                </div>
                {recipe.previewSeconds > 0 && (
                  <div className="flex justify-between gap-4 p-4">
                    <dt className="text-ink-muted">{t('detail.length')}</dt>
                    <dd className="font-mono text-ink">{formatSeconds(recipe.previewSeconds)}</dd>
                  </div>
                )}
                <div className="flex justify-between gap-4 p-4">
                  <dt className="text-ink-muted">{t('detail.copiesLabel')}</dt>
                  <dd className="font-mono text-ink">{recipe.copyCount}</dd>
                </div>
                <div className="flex justify-between gap-4 p-4">
                  <dt className="text-ink-muted">{t('detail.ratingLabel')}</dt>
                  <dd className="text-right text-ink">
                    {rated
                      ? t('detail.rating', { average: (recipe.ratingAverage ?? 0).toFixed(1), count: recipe.ratingCount })
                      : t('detail.ratingNone')}
                  </dd>
                </div>
                {recipe.costClass !== '' && (
                  <div className="flex justify-between gap-4 p-4">
                    <dt className="text-ink-muted">{t('cost.label')}</dt>
                    <dd className="text-ink">{costId !== null ? t(`cost.${costId}`) : t('field.other')}</dd>
                  </div>
                )}
              </dl>
              {recipe.costNote !== '' && (
                <p className="break-words bg-paper-2 p-4 text-sm text-ink-muted">
                  <span className="block font-mono text-xs uppercase tracking-eyebrow">{t('detail.costNoteLabel')}</span>
                  <span className="mt-1 block">{recipe.costNote}</span>
                </p>
              )}
            </aside>
          </div>
        </Container>
      </div>

      <div className="bg-paper-2 py-12">
        <Container>
          <Heading>{t('detail.ctaTitle')}</Heading>
          <p className="mt-3 max-w-2xl text-ink-muted">{t('detail.ctaBody')}</p>
          <div className="mt-6">
            <PrimaryCta location="recipe_detail" variant="outline" />
          </div>
        </Container>
      </div>
    </>
  );
}
