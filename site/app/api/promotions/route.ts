import { NextResponse } from 'next/server';
import { loadPromotionsBody, PROMOTIONS_CACHE_CONTROL } from '@/lib/promotions/active';

// NOTE: This route lives at /api/promotions, NOT /api/v1/*, so nginx's `/api/v1/*` proxy rules never
// intercept it. It is a same-origin proxy for the billing API's anonymous promotions list, and it
// is read at request time, never prerendered.
export const dynamic = 'force-dynamic';

export async function GET() {
  const body = await loadPromotionsBody(process.env.REELBOLT_API_URL);
  return NextResponse.json(body, { headers: { 'cache-control': PROMOTIONS_CACHE_CONTROL } });
}
