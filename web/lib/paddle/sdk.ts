import { clearCheckoutSnapshot } from '@/components/billing/checkout-snapshot';
import {
  initializePaddle,
  type Paddle,
  type PricePreviewItem,
  type PricePreviewResponse,
} from '@paddle/paddle-js';
import type { PaddleClientConfig } from '../types/billing';
import { totalsByPriceId } from './pricing';

/**
 * The browser's half of the embedded checkout: load Paddle.js, ask it what things cost, and open
 * the one-page overlay for a transaction the SERVER already created.
 *
 * Deliberately thin. Every decision that moves money — which organization a payment belongs to,
 * which price a checkout carries, how many seats — is made on the server, and nothing here can
 * change it: this module is handed a `transactionId` and opens it. See `openOverlayCheckout`.
 */

let loading: { key: string; promise: Promise<Paddle | null> } | null = null;

/**
 * Downloads and initializes Paddle.js, once per (environment, token, customer) triple.
 *
 * `initializePaddle` is this package's own loader. It injects Paddle.js from
 * `https://cdn.paddle.com/paddle/v2/paddle.js` — the only source Paddle permits — and then calls
 * `Paddle.Environment.set(<environment>)` followed by `Paddle.Initialize({ token })`, which is the
 * order Paddle requires (the environment must be set before the account is). `Initialize` is the
 * current spelling of that step: this package's own types mark `Paddle.Setup` `@deprecated` in its
 * favour.
 *
 * Resolves to null when there is nothing to load (no token) and when the CDN cannot be reached.
 * Both are reported the same way because the caller's fallback is the same in both cases: the
 * provider's hosted page for the very same transaction.
 *
 * The package memoizes the CDN load per Paddle.js version — a rejected load included — so a tab
 * whose first attempt failed stays on that fallback until it is reloaded; retrying here could only
 * hand back the same rejected promise.
 */
/** Whether the overlay that is open reported a completed payment (see the event callback). */
let completed = false;

export function loadPaddle(config: PaddleClientConfig): Promise<Paddle | null> {
  const token = typeof config.clientToken === 'string' ? config.clientToken.trim() : '';
  if (token === '') return Promise.resolve(null);

  // The buyer Paddle.js is told about, so Paddle Retain can identify them. Blank and missing are the
  // same case: an organization that has never checked out has no customer id, and Paddle is left
  // without one rather than handed an empty string.
  const customerId = typeof config.customerId === 'string' ? config.customerId.trim() : '';

  // The customer id is part of the key: switching organization in one tab must not keep the Paddle
  // instance bound to the previous buyer.
  const key = `${config.environment}|${token}|${customerId}`;
  if (loading?.key !== key) {
    loading = {
      key,
      promise: initializePaddle({
        version: 'v1',
        environment: config.environment,
        token,
        // An overlay closed without a payment leaves no purchase to wait for, so the baseline the
        // return page would compare against is dropped. A completed checkout keeps it: the buyer is
        // about to be sent to the success URL, which needs it.
        eventCallback: (event) => {
          if (event.name === 'checkout.completed') completed = true;
          else if (event.name === 'checkout.closed') {
            if (!completed) clearCheckoutSnapshot();
            completed = false;
          }
        },
        // Omitted entirely — never `{}`, never `{ id: '' }` — when there is no customer to identify.
        ...(customerId ? { pwCustomer: { id: customerId } } : {}),
      })
        .then((paddle) => paddle ?? null)
        .catch(() => null),
    };
  }
  return loading.promise;
}

/**
 * What Paddle says these items cost for this buyer, as Paddle formatted it — or null when the
 * preview is unavailable (no Paddle.js, a refused call, an offline buyer). A null answer is never
 * turned into a number here: the caller shows the catalogue's list price and says that is what it
 * is.
 *
 * Read-only: this creates nothing, reserves nothing and charges nothing.
 */
export async function requestPricePreview(
  paddle: Paddle,
  items: PricePreviewItem[],
): Promise<Map<string, string> | null> {
  if (items.length === 0) return new Map();
  try {
    const preview: PricePreviewResponse = await paddle.PricePreview({ items });
    return totalsByPriceId(preview);
  } catch {
    return null;
  }
}

export interface OverlayCheckout {
  paddle: Paddle;
  /** The transaction the API created for this organization — never one the browser named. */
  transactionId: string;
  successUrl: string;
  /** The signed-in address, when the client has one, so the buyer does not retype it. */
  email?: string | null;
  /**
   * True when the server attached a discount to the transaction. The overlay then hides its own
   * discount box and keeps the buyer from removing the one the server applied.
   */
  discounted?: boolean;
}

/**
 * Opens Paddle's embedded one-page overlay for a transaction the server already created.
 *
 * **No `customData` is passed, and that is the point of opening the checkout this way.** A payment
 * is attributed to an organization through `custom_data.organization_id`, which the webhook reads
 * to decide whose credits a payment buys. Anything this browser sent would be tamperable — a buyer
 * could direct the credits at an organization that is not theirs — so the only supported way in is
 * a `transactionId`: the API created that transaction from the caller's own session
 * (`POST /api/v1/billing/checkout` → `PaddleBillingProvider.CreateCheckoutAsync`, which is where
 * `custom_data.organization_id` and the customer id are attached), and the organization id in it is
 * therefore one the server chose, not one the caller asserted.
 *
 * `settings.successUrl` brings the buyer back to this app's own post-checkout page. It grants
 * nothing: the webhook remains the only thing that changes a plan or a balance.
 */
export function openOverlayCheckout({
  paddle,
  transactionId,
  successUrl,
  email,
  discounted,
}: OverlayCheckout): void {
  const address = typeof email === 'string' && email.trim() !== '' ? email.trim() : null;

  paddle.Checkout.open({
    transactionId,
    // Prefill: `customer` is a top-level member of `CheckoutOpenOptions` in this package's types
    // (@paddle/paddle-js 1.6.5) — there is no `settings.customer` — and it accepts an email
    // address on its own. Omitted entirely when there is nothing to prefill, rather than sent empty.
    ...(address ? { customer: { email: address } } : {}),
    settings: {
      displayMode: 'overlay',
      variant: 'one-page',
      successUrl,
      ...(discounted ? { allowDiscountRemoval: false, showAddDiscounts: false } : {}),
    },
  });
}
