import type { InferenceProviderKind } from './inference-provider';

/**
 * Rate cards are gone (model catalog, docs/design/model-catalog.md): prices now live on each model
 * capability, see `./pricing` and `ModelCapabilityPrice`. What remains here is the vocabulary the
 * usage ledger still reports in, re-exported so its readers keep one import.
 */
export type { UsageBillingMode, UsageKind } from './pricing';

/**
 * The provider kind a historic usage row is filed under. The backend enum also carries `OpenJev`,
 * which no provider form offers, so the ledger's slices widen the kind union with it rather than
 * render an unlabelled blank.
 */
export type RateCardProviderKind = InferenceProviderKind | 'OpenJev';
