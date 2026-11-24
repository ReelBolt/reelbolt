'use client';

import useSWR from 'swr';
import {
  ACTIVE_PROMOTIONS_PATH,
  ADMIN_PROMOTIONS_PATH,
  CHECKOUT_PREVIEW_PATH,
  adminOrgSearchPath,
  adminPlansPath,
  adminUserOrgsPath,
  getActivePromotions,
  listAdminPlans,
  listAdminUserOrgs,
  listOrgCreditGrants,
  listOrgPlanGrants,
  listPromotionRedemptions,
  listPromotions,
  orgCreditGrantsPath,
  orgPlanGrantsPath,
  previewCheckout,
  promotionRedemptionsPath,
  searchAdminOrgs,
  type CheckoutPreviewRequest,
} from '../api/promotions';

/**
 * The promotion reads. Each admin hook takes `enabled` (or a nullable id) so a page that cannot use
 * the answer issues no request: a null SWR key is "do not fetch".
 */

export function useAdminPromotions(enabled = true) {
  return useSWR(enabled ? ADMIN_PROMOTIONS_PATH : null, () => listPromotions());
}

export function usePromotionRedemptions(promotionId: string | null) {
  return useSWR(promotionId ? promotionRedemptionsPath(promotionId) : null, () =>
    listPromotionRedemptions(promotionId as string),
  );
}

/** Organization search for granting credits. An empty search lists the first page. */
export function useAdminOrgSearch(search: string, enabled = true) {
  const key = enabled ? adminOrgSearchPath(search) : null;
  return useSWR(key, () => searchAdminOrgs(search), { keepPreviousData: true });
}

export function useAdminUserOrgs(userId: string | null) {
  return useSWR(userId ? adminUserOrgsPath(userId) : null, () => listAdminUserOrgs(userId as string));
}

export function useOrgCreditGrants(orgId: string | null) {
  return useSWR(orgId ? orgCreditGrantsPath(orgId) : null, () => listOrgCreditGrants(orgId as string));
}

/** The plans a platform admin may gift. Empty when billing is off (the route 404s). */
export function useAdminPlans(enabled = true) {
  return useSWR(enabled ? adminPlansPath() : null, () => listAdminPlans(), { revalidateOnFocus: false });
}

/** The organization's manual (Paddle-free) plan grants, newest first. */
export function useOrgPlanGrants(orgId: string | null) {
  return useSWR(orgId ? orgPlanGrantsPath(orgId) : null, () => listOrgPlanGrants(orgId as string));
}

/** Automatic promotions running now. Empty when billing is off. */
export function useActivePromotions(enabled = true) {
  return useSWR(enabled ? ACTIVE_PROMOTIONS_PATH : null, getActivePromotions, {
    revalidateOnFocus: false,
  });
}

/** The SWR key of one checkout preview. Exported so a caller can prime the cache it fills. */
export function checkoutPreviewKey(request: CheckoutPreviewRequest): string {
  return `${CHECKOUT_PREVIEW_PATH}#${JSON.stringify([
    request.plan ?? null,
    request.seats ?? null,
    request.topUpCredits ?? null,
    request.promoCode ?? null,
  ])}`;
}

/**
 * What the payment provider says one plan or pack costs after any discount. Null request = do not
 * fetch. A refusal is an answer, not something to retry: a code that is not valid for this target
 * stays not valid.
 */
export function useCheckoutPreview(request: CheckoutPreviewRequest | null) {
  return useSWR(request ? checkoutPreviewKey(request) : null, () => previewCheckout(request as CheckoutPreviewRequest), {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });
}
