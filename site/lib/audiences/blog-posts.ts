// The blog posts written for ONE audience, by slug. An article's call to action carries that
// audience to signup (`?for=<persona>`), so a teacher who signs up from the lesson-clips guide
// starts the welcome questions on "educator" like one who came through `/for/educators`.
//
// Only posts whose title or opening names their reader are listed; a comparison, a release note
// or a post for everyone carries no audience. It is kept here, next to the audience registry, and
// not on the post itself: it is a fact about the funnel (which audience page a post belongs with),
// and `blog-posts.test.ts` checks every slug against the files in `content/blog`, so a renamed or
// removed post fails the test instead of silently losing its audience.
//
// Pure (no React, no `@/` imports), so `node --test` can load it.
import type { LandingPersona } from '../attribution-persona.ts';

export const POST_AUDIENCES: Readonly<Record<string, LandingPersona>> = {
  'stop-editing-on-weekends': 'creator',
  'video-for-your-small-business': 'business',
  'lessons-into-short-clips': 'educator',
  'tell-your-cause': 'nonprofit',
};

/** The audience a post was written for, or undefined for a post that speaks to everyone. */
export function audienceOfPost(slug: string): LandingPersona | undefined {
  return Object.hasOwn(POST_AUDIENCES, slug) ? POST_AUDIENCES[slug] : undefined;
}
