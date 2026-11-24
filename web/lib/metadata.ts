import type { Metadata } from 'next';

/**
 * The product name as it appears in the browser tab.
 */
export const APP_NAME = 'ReelBolt';

/**
 * The tab title for one route segment, e.g. `routeTitle('Billing') -> "Billing · ReelBolt"`.
 *
 * Every layout in the dashboard declares its title through this helper, and that is not
 * belt-and-braces: Next applies `title.template` to a child segment's title and then, for a
 * segment whose `title` is a plain STRING, ends up with no template of its own to hand down —
 * the resolved title is a string, so the next segment down has nothing to substitute into, and
 * the suffix silently disappears from everything below. Concretely, before this helper
 * `/app/admin/billing` rendered `<title>Billing</title>` while `/app/admin` rendered
 * `<title>Admin Overview · ReelBolt</title>`. A segment therefore has to re-declare the template
 * for its own children, which is what returning the object (rather than a bare string) does.
 *
 * The mechanism is Next's own: `lib/metadata/resolvers/resolve-title.js` returns
 * `{ absolute, template }` and `template` is null for a string title, while
 * `accumulateMetadata` in `lib/metadata/resolve-metadata.js` carries that forward as its
 * `titleTemplates.title` stash.
 */
export function routeTitle(name: string): Metadata['title'] {
  return { template: `%s · ${APP_NAME}`, default: name };
}
