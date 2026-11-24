import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';
import { apiFetch, ApiError } from './client';
import { billingActionMessage } from './billing';
import { readableServerMessage } from './describe-error';

/**
 * Promotions, promo codes and admin credit grants (saas-promotions). Every route lives under
 * `/api/v1/billing`, so every one of them answers 404 when billing is off on the deployment.
 *
 * Money rule: nothing here computes a price. The checkout preview's amounts are the payment
 * provider's own formatted strings and are shown exactly as they arrive.
 */

const BILLING = '/api/v1/billing';

export const ADMIN_PROMOTIONS_PATH = `${BILLING}/admin/promotions`;
export const ACTIVE_PROMOTIONS_PATH = `${BILLING}/promotions/active`;
export const REDEEM_PROMOTION_PATH = `${BILLING}/promotions/redeem`;
export const CHECKOUT_PREVIEW_PATH = `${BILLING}/checkout/preview`;

export const PROMOTION_PRESETS_PATH = `${ADMIN_PROMOTIONS_PATH}/presets`;

export function promotionPath(id: string): string {
  return `${ADMIN_PROMOTIONS_PATH}/${encodeURIComponent(id)}`;
}

export function promotionRedemptionsPath(id: string): string {
  return `${promotionPath(id)}/redemptions`;
}

export function adminOrgSearchPath(search: string): string {
  const q = search.trim();
  return q ? `${BILLING}/admin/orgs?search=${encodeURIComponent(q)}` : `${BILLING}/admin/orgs`;
}

export function adminUserOrgsPath(userId: string): string {
  return `${BILLING}/admin/users/${encodeURIComponent(userId)}/orgs`;
}

export function orgCreditGrantsPath(orgId: string): string {
  return `${BILLING}/admin/orgs/${encodeURIComponent(orgId)}/credit-grants`;
}

/** The plans a platform admin may grant: active plans, the trial plan excluded. */
export function adminPlansPath(): string {
  return `${BILLING}/admin/plans`;
}

export function orgPlanGrantsPath(orgId: string): string {
  return `${BILLING}/admin/orgs/${encodeURIComponent(orgId)}/plan-grants`;
}

export function revokeOrgPlanGrantPath(orgId: string, grantId: string): string {
  return `${orgPlanGrantsPath(orgId)}/${encodeURIComponent(grantId)}/revoke`;
}

// ------------------------------------------------------------------------------------- types

export type PromotionKind = 'creditCode' | 'planDiscount' | 'packDiscount' | 'packBonus';
export type PromotionStatus = 'active' | 'paused' | 'archived';
export type PromotionDiscountType = 'percentage' | 'flat';
export type PromotionDuration = 'once' | 'months' | 'forever';

/**
 * `PromotionDto`: every promotion column in camelCase, credits as whole credits, plus the
 * redemption count. Enum values are compared case-insensitively (see {@link sameEnum}) so a
 * PascalCase serializer still reads correctly.
 */
export interface Promotion {
  id: string;
  kind: PromotionKind | string;
  name: string;
  publicHeadline: string | null;
  code: string | null;
  status: PromotionStatus | string;
  startsAt: string | null;
  endsAt: string | null;
  maxRedemptions: number | null;
  redemptionCount: number;
  maxRedemptionsPerOrg: number;
  credits?: number | null;
  creditExpiryDays?: number | null;
  discountType?: PromotionDiscountType | string | null;
  discountAmount?: string | null;
  currencyCode?: string | null;
  durationKind?: PromotionDuration | string | null;
  durationMonths?: number | null;
  targetPlanKeys?: string[] | null;
  targetPackCredits?: number[] | null;
  targetPriceIds?: string[] | null;
  bonusPercent?: number | null;
  providerDiscountId?: string | null;
  createdAt: string;
  createdByUserId?: string | null;
  updatedAt?: string | null;
  /** How many redemptions are recorded against it. */
  redemptions: number;
}

/** `CreatePromotionDto`, exactly as the API binds it. Absent fields are omitted, never sent null. */
export interface CreatePromotionRequest {
  kind: PromotionKind;
  name: string;
  publicHeadline?: string;
  code?: string;
  startsAt?: string;
  endsAt?: string;
  maxRedemptions?: number;
  maxRedemptionsPerOrg?: number;
  // CreditCode
  credits?: number;
  creditExpiryDays?: number;
  // PlanDiscount / PackDiscount
  discountType?: PromotionDiscountType;
  discountAmount?: string;
  currencyCode?: string;
  duration?: PromotionDuration;
  durationMonths?: number;
  planKeys?: string[];
  // PackDiscount / PackBonus
  packCredits?: number[];
  // PackBonus
  bonusPercent?: number;
}

/** The PATCH body: only these may change after create. */
export interface UpdatePromotionRequest {
  name?: string;
  publicHeadline?: string;
  status?: PromotionStatus;
  endsAt?: string;
  clearEndsAt?: boolean;
}

export interface PromotionRedemption {
  id: string;
  kind: string;
  organizationId: string;
  organizationName: string | null;
  userId: string | null;
  providerTransactionId: string | null;
  credits: number | null;
  discountTotal: string | null;
  currencyCode: string | null;
  /** A paid, reserved checkout honoured past a cap. */
  overshoot: boolean;
  /** `notGranted`: a refused grant/count support may need to make good. */
  outcome: PromotionRedemptionOutcome | string;
  /** Why it was not granted (camelCase enum member), or null when granted. */
  notGrantedReason: PromotionNotGrantedReason | string | null;
  /** ISO time a plan change ended this subscription discount; null while it still applies. */
  discountRemovedAt: string | null;
  createdAt: string;
}

export type PromotionRedemptionOutcome = 'granted' | 'notGranted';

export type PromotionNotGrantedReason =
  | 'superseded'
  | 'expired'
  | 'reservedOverCap'
  | 'notReserved'
  | 'overshootBoundReached'
  | 'userCapReached'
  | 'orgCapReached'
  | 'checkoutMismatch';

export interface AdminOrg {
  id: string;
  name: string;
  kind: string;
  ownerEmail: string | null;
  planKey: string | null;
  /** Whole credits. */
  balance: number;
}

export interface AdminCreditGrant {
  id: string;
  credits: number;
  expiresAt: string;
  reason: string;
  grantedByUserId: string;
  createdAt: string;
}

export interface GrantCreditsRequest {
  requestId: string;
  credits: number;
  expiresAt: string;
  reason: string;
}

/** One plan an admin can grant: active, non-trial, from `GET /admin/plans`. */
export interface AdminPlan {
  key: string;
  name: string;
  kind: string;
  priceMonthlyUsd: number;
}

/** `revoked` is a stamped revoke; `expired` is a live grant whose period end has passed. */
export type AdminPlanGrantStatus = 'active' | 'revoked' | 'expired';

/**
 * One manual (Paddle-free) plan grant. Provider-backed subscriptions never appear here; the grant
 * writes a `subscriptions` row with no provider, and `status` is decided by the API.
 */
export interface AdminPlanGrant {
  id: string;
  organizationId: string;
  planKey: string;
  planName: string;
  periodStart: string;
  periodEnd: string;
  reason: string;
  grantedByUserId: string;
  subscriptionId: string;
  supersededSubscriptionId: string | null;
  revokedAt: string | null;
  revokedByUserId: string | null;
  revokeReason: string | null;
  status: AdminPlanGrantStatus | string;
}

export interface GrantPlanRequest {
  requestId: string;
  planKey: string;
  durationDays: number;
  reason: string;
}

/** The revoke body: the reason is optional and kept with the grant for audit. */
export interface RevokePlanGrantRequest {
  reason?: string;
}

export interface RedeemResult {
  credits: number;
  expiresAt: string;
  headline: string | null;
}

/** One automatic, in-window promotion from `promotions/active`. Never carries a code or an id. */
export interface ActivePromotion {
  kind: 'planDiscount' | 'packDiscount' | 'packBonus' | string;
  headline: string | null;
  endsAt: string | null;
  discountType?: string | null;
  percentOff?: number | null;
  amountOffMinor?: number | null;
  currencyCode?: string | null;
  bonusPercent?: number | null;
  planKeys?: string[] | null;
  packCredits?: number[] | null;
  durationKind?: string | null;
  durationMonths?: number | null;
}

export interface CheckoutPreviewRequest {
  plan?: string;
  seats?: number;
  topUpCredits?: number;
  promoCode?: string;
  interval?: 'annual';
}

/** The money strings are the payment provider's own, formatted for the buyer. Show them verbatim. */
export interface CheckoutPreview {
  currencyCode: string;
  subtotal: string;
  discount: string;
  total: string;
  /** Raw integer minor units (additive; present for Stripe). Formatted with `formatPreviewMoney`. */
  subtotalMinor?: number | null;
  discountMinor?: number | null;
  totalMinor?: number | null;
  unitSubtotalMinor?: number | null;
  unitTotalMinor?: number | null;
  minorUnitExponent?: number | null;
  promotion: { headline: string | null; kind: string } | null;
  bonusCredits: number | null;
}

// ----------------------------------------------------------------------------------- helpers

/** Enum strings compared without regard to case: `"CreditCode"` and `"creditCode"` are one value. */
export function sameEnum(value: string | null | undefined, expected: string): boolean {
  return typeof value === 'string' && value.toLowerCase() === expected.toLowerCase();
}

/** Codes are uppercase letters and digits, 4 to 32 long. */
export const PROMO_CODE_PATTERN = /^[A-Z0-9]{4,32}$/;

/** What a customer typed, normalized the way the server normalizes it. */
export function normalizePromoCode(raw: string): string {
  return raw.replace(/\s+/g, '').toUpperCase();
}

/** The machine code of a refusal: the body's `code`/`error`, else the message itself. */
function refusalCode(error: ApiError): string {
  return (error.code ?? error.message ?? '').trim();
}

const PROMO_CODES: ReadonlySet<string> = new Set(['promo_code_invalid', 'promo_already_redeemed', 'promo_rate_limited']);

/**
 * The customer-facing sentence for a failed redeem, preview or checkout with a code. Anything that
 * is not a promo refusal falls back to the billing page's own messages.
 */
export function promoCodeMessage(error: unknown, t: T = enT): string {
  if (error instanceof ApiError) {
    if (error.status === 429) return t('libLabels.billingActions.promo_rate_limited');
    const code = refusalCode(error);
    if (PROMO_CODES.has(code)) return t(`libLabels.billingActions.${code}`);
  }
  return billingActionMessage(error, t);
}

const ADMIN_MESSAGES: Record<string, string> = {
  promo_code_taken: 'Another promotion already uses that code.',
  automatic_promotion_overlap:
    'An active automatic promotion of this kind already covers some of these plans or packs in an overlapping window. Pause it or narrow the targets.',
  target_not_purchasable: 'One of the chosen plans or packs has no price at your payment provider, so it cannot be discounted.',
  billing_not_configured: 'No payment provider is configured, so a discount cannot be created.',
  billing_provider_error: 'The payment provider rejected the request. Nothing was saved.',
  PLAN_GRANT_PROVIDER_ACTIVE:
    'This organization pays through the billing provider. Change or cancel that subscription with the payment provider; a manual grant would sit beside it.',
  PLAN_GRANT_NOT_MANUAL: 'That subscription is managed by the billing provider and cannot be revoked here.',
};

/** The admin-facing sentence for a failed promotion or credit-grant action. Admin copy may name Paddle. */
export function promotionAdminMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const code = refusalCode(error);
    if (code in ADMIN_MESSAGES) return ADMIN_MESSAGES[code];
    if (error.status === 403) return 'Only platform administrators can do this.';
    if (error.status === 404) return 'Billing is not enabled on this deployment.';
    if (error.errors.length > 0) return error.errors.join(' ');
    const own = readableServerMessage(error);
    if (own) return own;
  }
  const own = readableServerMessage(error);
  if (own) return own;
  return 'Something went wrong. Nothing was saved.';
}

// ---------------------------------------------------------------------------------- requests

export async function listPromotions(): Promise<Promotion[]> {
  const res = await apiFetch<{ items: Promotion[] }>(ADMIN_PROMOTIONS_PATH);
  return res.items ?? [];
}

export function createPromotion(body: CreatePromotionRequest): Promise<Promotion> {
  return apiFetch<Promotion>(ADMIN_PROMOTIONS_PATH, { method: 'POST', body: JSON.stringify(body) });
}

export function updatePromotion(id: string, body: UpdatePromotionRequest): Promise<Promotion> {
  return apiFetch<Promotion>(promotionPath(id), { method: 'PATCH', body: JSON.stringify(body) });
}

export async function listPromotionRedemptions(id: string): Promise<PromotionRedemption[]> {
  const res = await apiFetch<{ items: PromotionRedemption[] }>(promotionRedemptionsPath(id));
  return res.items ?? [];
}

export async function searchAdminOrgs(search: string): Promise<AdminOrg[]> {
  const res = await apiFetch<{ items: AdminOrg[] }>(adminOrgSearchPath(search));
  return res.items ?? [];
}

export async function listAdminUserOrgs(userId: string): Promise<AdminOrg[]> {
  const res = await apiFetch<{ items: AdminOrg[] }>(adminUserOrgsPath(userId));
  return res.items ?? [];
}

/** The org's admin grants, newest first. Tolerates a bare array as well as `{ items }`. */
export async function listOrgCreditGrants(orgId: string): Promise<AdminCreditGrant[]> {
  const res = await apiFetch<{ items: AdminCreditGrant[] } | AdminCreditGrant[]>(orgCreditGrantsPath(orgId));
  return Array.isArray(res) ? res : (res.items ?? []);
}

/** A replayed `requestId` answers with the same row, so a double submit grants once. */
export function grantOrgCredits(orgId: string, body: GrantCreditsRequest): Promise<AdminCreditGrant> {
  return apiFetch<AdminCreditGrant>(orgCreditGrantsPath(orgId), { method: 'POST', body: JSON.stringify(body) });
}

/** The plans an admin may grant: active plans, trial excluded, in catalog order. */
export async function listAdminPlans(): Promise<AdminPlan[]> {
  const res = await apiFetch<{ items: AdminPlan[] }>(adminPlansPath());
  return res.items ?? [];
}

/** The org's manual plan grants, newest first. Tolerates a bare array as well as `{ items }`. */
export async function listOrgPlanGrants(orgId: string): Promise<AdminPlanGrant[]> {
  const res = await apiFetch<{ items: AdminPlanGrant[] } | AdminPlanGrant[]>(orgPlanGrantsPath(orgId));
  return Array.isArray(res) ? res : (res.items ?? []);
}

/** A replayed `requestId` answers with the same row, so a double submit grants once. */
export function grantOrgPlan(orgId: string, body: GrantPlanRequest): Promise<AdminPlanGrant> {
  return apiFetch<AdminPlanGrant>(orgPlanGrantsPath(orgId), { method: 'POST', body: JSON.stringify(body) });
}

/** Revokes one manual grant. A provider-backed or already-revoked grant answers 409 / idempotent 200. */
export function revokeOrgPlanGrant(
  orgId: string,
  grantId: string,
  body: RevokePlanGrantRequest = {},
): Promise<AdminPlanGrant> {
  return apiFetch<AdminPlanGrant>(revokeOrgPlanGrantPath(orgId, grantId), {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function redeemPromoCode(code: string): Promise<RedeemResult> {
  return apiFetch<RedeemResult>(REDEEM_PROMOTION_PATH, {
    method: 'POST',
    body: JSON.stringify({ code: normalizePromoCode(code) }),
  });
}

/** Automatic promotions running now. An empty list when billing is off (404). */
export async function getActivePromotions(): Promise<ActivePromotion[]> {
  try {
    const res = await apiFetch<{ items: ActivePromotion[] }>(ACTIVE_PROMOTIONS_PATH);
    return res.items ?? [];
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return [];
    throw error;
  }
}

export function previewCheckout(body: CheckoutPreviewRequest): Promise<CheckoutPreview> {
  return apiFetch<CheckoutPreview>(CHECKOUT_PREVIEW_PATH, { method: 'POST', body: JSON.stringify(body) });
}

/** Whether an automatic promotion covers a plan (an empty or absent target list means every plan). */
export function promotionCoversPlan(promotion: ActivePromotion, planKey: string): boolean {
  if (!sameEnum(promotion.kind, 'planDiscount')) return false;
  const keys = promotion.planKeys ?? [];
  return keys.length === 0 || keys.includes(planKey);
}

/** Whether an automatic promotion covers a credit pack (discount or bonus). */
export function promotionCoversPack(promotion: ActivePromotion, credits: number): boolean {
  if (!sameEnum(promotion.kind, 'packDiscount') && !sameEnum(promotion.kind, 'packBonus')) return false;
  const packs = promotion.packCredits ?? [];
  return packs.length === 0 || packs.includes(credits);
}

/** What `GET /admin/promotions/presets` answers: ready-made create bodies, `null` fields left unset. */
export interface PromotionPresets {
  founderPrice: {
    kind: PromotionKind;
    name: string;
    publicHeadline: string | null;
    discountType: PromotionDiscountType | null;
    discountAmount: string | null;
    duration: PromotionDuration | null;
    endsAt: string | null;
    code: string | null;
    planKeys: string[] | null;
  };
}

/** Used when the presets route is not there (an older API): the same Founder price, held locally. */
export const FOUNDER_PRICE_FALLBACK: PromotionPresets['founderPrice'] = {
  kind: 'planDiscount',
  name: 'Founder price',
  publicHeadline: 'Founder price: 50% off',
  discountType: 'percentage',
  discountAmount: '50',
  duration: 'forever',
  endsAt: '2027-01-31T22:59:00Z',
  code: null,
  planKeys: null,
};

/** The Founder price preset from the server, or the local copy when the route 404s. */
export async function fetchFounderPricePreset(): Promise<PromotionPresets['founderPrice']> {
  try {
    const presets = await apiFetch<PromotionPresets>(PROMOTION_PRESETS_PATH);
    return presets.founderPrice;
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return FOUNDER_PRICE_FALLBACK;
    throw error;
  }
}
