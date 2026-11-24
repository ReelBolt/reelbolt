// GET /l/<code> on Cloudflare Pages: a tracked link (docs/design/new-user-funnel.md sections 3 and 6).
// The API counts the click and answers the landing page as a 302; this relays it. When it cannot,
// the visitor goes to the home page with the link code as utm_content, so the visit still belongs
// to the link (see lib/tracked-link.ts for the rules). The self-hosted stack serves the same path
// from nginx, so there is no Node route twin. public/_routes.json lists /l/*.
//
// Rate limits: this Function keeps none of its own. The per-visitor limit of this path is a
// Cloudflare rate-limit rule on /l/*. Behind it, the origin limits the relay as a whole: with
// SITE_FORWARD_TOKEN set here and on the origin, nginx recognises these requests and counts them in
// the relay's own zone; without it they share the per-address zone, where every click relayed from
// Cloudflare looks like one busy address. A request the origin turns away (429) or cannot serve
// ends on the home page like any other failure.
import { followTrackedLink } from '../../lib/tracked-link.ts';

interface Env {
  // Public origin of the API. Unset: nothing is counted and every link goes to the home page
  // (with its code as utm_content).
  REELBOLT_API_URL?: string;
  // Shared secret the origin checks to give this relay its own rate zone. Unset: not sent.
  // A secret: it goes upstream in one request header and nowhere else. Never log it.
  SITE_FORWARD_TOKEN?: string;
}

interface Context {
  request: Request;
  env: Env;
  params: { code?: string | string[] };
}

export const onRequest = ({ request, env, params }: Context): Promise<Response> =>
  followTrackedLink({ apiUrl: env.REELBOLT_API_URL, siteToken: env.SITE_FORWARD_TOKEN, code: params.code, request });
