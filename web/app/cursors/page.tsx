import type { Metadata } from 'next';
import catalog from '@/lib/cursors/catalog.json';
import { CursorContactSheet, type CursorCatalog } from './CursorContactSheet';

export const metadata: Metadata = {
  title: 'Cursor proposal',
  // A design proposal for review, never a product page: keep it out of search and never link it.
  robots: { index: false, follow: false, nocache: true },
};

/**
 * The cursor proposal contact sheet: every cursor at real size on the three backgrounds it has to
 * survive, with its hotspot marked. Deliberately unlinked from the app — see
 * docs/design/cursors.md. The artwork and the catalog come from /brand via
 * `node brand/scripts/sync.mjs`; never edit web/lib/cursors/catalog.json or web/public/cursors/.
 */
export default function CursorProposalPage() {
  return <CursorContactSheet catalog={catalog as CursorCatalog} />;
}
