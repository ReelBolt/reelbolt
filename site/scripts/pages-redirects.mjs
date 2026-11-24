// The `_redirects` file of the static Cloudflare Pages bundle, as a pure function of the build
// environment, so `node --test` can pin it (lib/pages-redirects.test.ts).
//
// Pages only ever answers `/app/*` itself where the zone does not route that path to the
// dashboard: the pages.dev address, a preview deployment, or production before the route exists.
//
//  - Signup OFF (`NEXT_PUBLIC_SIGNUP_ENABLED` is not `true`): the site links nowhere under `/app`,
//    and the dashboard is not deployed for visitors. A stray `/app` link goes to the contact page.
//  - Signup ON: every "Start free" button links to `/app/signup`, with the visitor's audience and
//    campaign in the query. Sending those to `/contact` would end the funnel on the wrong page
//    without an error anyone sees, so no `/app` rule is written. Where Pages still answers `/app/*`
//    the visitor gets the 404 page, which is a visible fault to fix in routing, not a silent one.

/** @param {Record<string, string | undefined>} env */
export function pagesRedirects(env) {
  if (env.NEXT_PUBLIC_SIGNUP_ENABLED === 'true') return '';
  return '/app /contact 302\n/app/* /contact 302\n';
}
