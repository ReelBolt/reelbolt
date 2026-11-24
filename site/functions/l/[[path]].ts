// Everything else under /l/ on Cloudflare Pages (the bare folder, a deeper path): the home page,
// exactly as nginx answers it (`location /l/ { return 302 /; }`). One-segment paths are the tracked
// links themselves and go to the more specific ./[code].ts.
import { trackedLinkHome } from '../../lib/tracked-link.ts';

export const onRequest = (): Response => trackedLinkHome();
