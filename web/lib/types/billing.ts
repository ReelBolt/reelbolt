// Billing/usage types (WP B10b-UI). These mirror the response records on
// ReelBolt.Inference.Api.Controllers.BillingController exactly: ASP.NET Core serializes
// records with the default camelCase policy, so the field names below are the wire names.
//
// Every figure under `BillingUsage` is read straight off the `usage_events` ledger (B3b),
// so it is what was actually charged and actually consumed, priced at write time by the
// rate card that applied then — never a re-priced estimate.

/** The axes the usage endpoint can group by. Anything else is a 400 `unknown_group_by`. */
export type UsageGroupBy = 'day' | 'project' | 'kind' | 'model' | 'mode' | 'source';

/**
 * What a usage record was charged for (`groupBy=source`): a workflow run, the assistant, Claude Code
 * over MCP, the picture editor's AI tools, timeline suggestions, file summaries, search indexing, the
 * help search index, storage, or none of those. Append-only: an unknown value is a newer backend and
 * is shown by name.
 */
export type UsageSource =
  | 'Workflow'
  | 'Assistant'
  | 'Mcp'
  | 'Editor'
  | 'TimelineAssist'
  | 'FileSummary'
  | 'Indexing'
  | 'DocsIndex'
  | 'Storage'
  | 'Unattributed';

/**
 * `costUsd` is ReelBolt's own provider cost (COGS). The backend reports it only to a platform
 * admin; for everyone else it is `null`, and the UI simply omits the column.
 */
export interface UsageTotals {
  credits: number;
  events: number;
  costUsd: number | null;
}

/** One usage kind's consumed quantity, in that kind's own unit (tokens, seconds, bytes, GB-days). */
export interface UsageUnit {
  kind: string;
  quantity: number;
}

/** One group of the requested axis. `key` is the raw axis value; `label` is what to show. */
export interface UsageGroup {
  key: string;
  label: string;
  credits: number;
  events: number;
  costUsd: number | null;
  units: UsageUnit[];
}

/** The who-pays split (platform-managed provider, the org's own provider, the org's own runner). */
export interface UsageBillingMode {
  mode: string;
  credits: number;
  events: number;
  costUsd: number | null;
}

/** `GET /api/v1/billing/usage`. The window is half-open: `[from, to)`. */
export interface BillingUsage {
  from: string;
  to: string;
  groupBy: string;
  totals: UsageTotals;
  byBillingMode: UsageBillingMode[];
  groups: UsageGroup[];
}

/** The credit projection the ledger publishes (`/billing/summary`), in whole credits. */
export interface BillingCredits {
  balance: number;
  reserved: number;
  available: number;
  /**
   * The org's credit grants that still have credits left and have not expired, soonest expiry
   * first. Absent on an older backend, which the page treats as an empty list.
   */
  grants?: BillingCreditGrant[];
}

/** Where a grant of credits came from. Append-only: an unknown source is shown generically. */
export type CreditGrantSource = 'trial' | 'plan' | 'topUp' | 'promo';

/** One live credit grant on the summary, in whole credits. */
export interface BillingCreditGrant {
  source: CreditGrantSource | string;
  remaining: number;
  /** ISO timestamp; null for a grant that never expires. */
  expiresAt: string | null;
}

/** How a subscription (or a checkout) bills. */
export type BillingInterval = 'monthly' | 'annual';

export interface BillingSubscriptionSummary {
  planKey: string;
  planName: string;
  status: string;
  seats: number;
  periodStart: string | null;
  periodEnd: string | null;
  /** `monthly` or `annual`; absent on an older API, which only sold monthly. */
  interval?: BillingInterval;
  /** When the next monthly credit refill lands on a yearly plan; absent or null otherwise. */
  creditsRenewAt?: string | null;
  cancelAtPeriodEnd: boolean;
  /**
   * When a scheduled cancellation takes effect; null when none is scheduled. Optional only so an
   * older API (and older fixtures) without it still type-check: read it as "not scheduled".
   */
  cancelEffectiveAt?: string | null;
  /** A downgrade (or seat decrease) Paddle applies at `effectiveAt`; null/absent when none is pending. */
  scheduledChange?: ScheduledPlanChange | null;
  hasBillingCustomer: boolean;
  /**
   * The recurring promo discount on the subscription now; null (or absent, on an older API) when
   * there is none. A plan change to a price outside `targetPriceIds` ends it.
   */
  promoDiscount?: PromoDiscount | null;
}

/**
 * `subscription.promoDiscount` on the summary. `amount` is in Paddle's format: the percent for
 * `Percentage` (`"20"`), whole minor units for `Flat` (`"500"` is $5.00 with `currencyCode` USD).
 */
export interface PromoDiscount {
  discountType: 'Percentage' | 'Flat';
  amount: string;
  /** Set for `Flat` only. */
  currencyCode: string | null;
  /** The provider price ids (`pri_…`) the discount keeps applying to. */
  targetPriceIds: string[];
}

/** `subscription.scheduledChange` on the summary: the plan and seats the workspace moves to. */
export interface ScheduledPlanChange {
  planKey: string;
  planName: string;
  seats: number;
  effectiveAt: string;
}

/**
 * The 202 of `POST /billing/subscription/change-plan` and `POST /billing/seats`. Accepted, not
 * applied: the summary changes only when Paddle's webhook lands.
 *
 * - `upgrade`: effective now; `creditGrantMillis` is the prorated grant for the rest of the period.
 * - `downgrade`: effective at `effectiveAt` (the period end); no grant.
 * - `keep`: the scheduled change is removed; `effectiveAt` is null.
 */
export interface PlanChangeResult {
  direction: 'upgrade' | 'downgrade' | 'keep';
  effective: 'now' | 'period_end';
  effectiveAt: string | null;
  creditGrantMillis: number;
  seats: number;
  message: string;
  /** True when this change ended the subscription's promo discount. Absent on an older API. */
  promoDiscountRemoved?: boolean;
}

/** `POST /billing/subscription/cancel`: 202 `cancel_requested`, or 200 when already scheduled. */
export interface CancelSubscriptionResult {
  status: 'cancel_requested' | 'cancel_scheduled';
  effectiveAt: string | null;
}

/** `POST /billing/subscription/resume`: 202 `resume_requested`, or 200 `active` with nothing to undo. */
export interface ResumeSubscriptionResult {
  status: 'resume_requested' | 'active';
}

/**
 * One plan from the summary's `plans` array. The array lists only what the configured payment
 * provider can actually sell: the endpoint drops its two non-purchasable kinds (Trial and
 * Enterprise) and every plan with no price id for that provider, so anything in here can be bought.
 *
 * `priceMonthlyUsd` and `includedCredits` come from the API's own `plans` rows, which are upserted
 * at startup from `plans.catalog.json` — the single source of truth. Nothing on this page carries a
 * price of its own.
 */
export interface BillingPlan {
  key: string;
  name: string;
  /** `Subscription` or `PerSeat` — the PlanKind member name, as the database stores it. */
  kind: string;
  /** List price per month in USD; per seat for a `PerSeat` plan. */
  priceMonthlyUsd: number;
  /** Minimum seat count when the plan is per-seat; null otherwise. */
  minSeats: number | null;
  /** Credits granted each billing period, in whole credits (per seat for a per-seat plan). */
  includedCredits: number;
  /** Storage allowance in GB; null when unlimited. Optional: an older API omits it. */
  storageGb?: number | null;
  /** What the plan grants; a null limit means unlimited. Optional: an older API omits it. */
  entitlements?: {
    maxOutputHeight: number | null;
    watermark: boolean;
    byoAllowed: boolean;
    roomsAllowed: boolean;
    videoGenAllowed: boolean;
    segmentationAllowed: boolean;
    audioGenAllowed: boolean;
    audioCleanupAllowed: boolean;
    maxConcurrentExecutions: number | null;
    assistantMonthlyCredits: number | null;
    /** Per-seat allowance (Team); null when the plan is not per seat. */
    assistantMonthlyCreditsPerSeat?: number | null;
    /** True only for a plan with no assistant cap (Enterprise). */
    assistantUnlimited?: boolean;
    retentionDays: number | null;
    runnerAllowed: boolean;
    priority: number;
  };
  /**
   * The provider price id (**`pri_…`**) a checkout for this plan will actually carry — the same
   * one the API sends to Paddle when it creates the transaction. It is what lets the browser ask
   * Paddle for this plan's localized price, and it is why a plan without one is absent from the
   * array rather than present with a blank string.
   */
  priceId: string;
  /** List price per year in USD (per seat for a `PerSeat` plan); null or absent when the plan has no yearly price. */
  priceAnnualUsd?: number | null;
  /** The provider price id of the yearly price; null or absent when the provider cannot sell it. */
  annualPriceId?: string | null;
  /** Feature lines from the plan catalog, when the API sends them; absent on an older API. */
  highlights?: string[];
  /** True for the plan the organization is on now. */
  current: boolean;
}

/**
 * The browser half of the embedded checkout, as `GET /api/v1/billing/summary` serves it under
 * `paddle`. Null when the configured provider is not Paddle — in which case there is no Paddle.js
 * checkout to set up at all.
 */
export interface PaddleClientConfig {
  /**
   * Paddle.js's own vocabulary — `'sandbox'` or `'production'` — which the API translates from its
   * `sandbox`/`live` setting. The browser passes it to `Paddle.Environment.set` unchanged, so it
   * can never be pointed at an environment other than the one the server bills through.
   */
  environment: 'sandbox' | 'production';
  /**
   * The publishable (client-side) token. **Not a secret**: Paddle's browser library is designed to
   * be initialized with it and it grants nothing by itself. Null when the deployment has not
   * configured one, which means there is no embedded checkout to open — the buyer is sent to the
   * provider's hosted page for the same transaction instead.
   */
  clientToken: string | null;
  /**
   * The Paddle customer id (`ctm_…`) of the active organization — the buyer Paddle.js is told about
   * so Paddle Retain can identify them — or null when the organization has never checked out. It is
   * the Paddle customer id, **never** the internal organization id and never an email. Not a secret:
   * Paddle's own browser library is designed to be initialized with it, like the token above.
   */
  customerId: string | null;
}

/** `topUp` on the summary: whether credits may be bought, and in which pack sizes. */
export interface BillingTopUp {
  /**
   * False on a free Trial organization. D9 limits top-ups to paying customers, so a Trial org
   * upgrades first; this is the server's own answer, not a rule re-derived here.
   */
  available: boolean;
  /** Configured pack sizes in credits, ascending. Empty when the provider has no top-up prices. */
  packs: number[];
}

/**
 * The subset of `GET /api/v1/billing/summary` this page reads. The endpoint answers 404 when
 * `Billing:Enabled=false`, which is a deployment fact to render, not an error.
 */
export interface BillingSummary {
  /** The provider's key (`paddle`), not a display name — see `providerDisplayName`. */
  billingProvider: string | null;
  /** The provider's display name from the API ("Paddle", "Stripe"); optional on an older API. */
  billingProviderDisplayName?: string | null;
  /** The Paddle.js settings, or null when the configured provider is not Paddle. */
  paddle: PaddleClientConfig | null;
  canManageBilling: boolean;
  subscription: BillingSubscriptionSummary | null;
  credits: BillingCredits;
  /** What can be bought right now: the plan catalogue and the top-up packs. */
  topUp: BillingTopUp;
  plans: BillingPlan[];
  /** When the next monthly credit refill lands on a yearly plan; also read from `subscription`. Optional. */
  creditsRenewAt?: string | null;
  /**
   * The monthly assistant allowance: credits of assistant and Claude Code use the plan includes before
   * they are charged from the balance. Optional because an older backend does not send it.
   */
  assistantAllowance?: AssistantAllowance | null;
  /**
   * What the organization's plan allows right now. `maxOutputHeight` is the largest short side a
   * render may have, in pixels; null means unlimited. Optional: an older API omits it.
   */
  entitlements?: { maxOutputHeight: number | null; unlocks?: PlanUnlock[] };
  /**
   * The one-time free-first-video credits: `remaining` is what is still unspent. Null when the
   * organization has none (never granted, spent, or expired). Optional: an older API omits it.
   */
  firstVideoGrant?: FirstVideoGrant | null;
  /** "unavailable": the grant was refused; show a plain note. "none": nothing to show. Optional for older APIs. */
  firstVideoGrantStatus?: FirstVideoGrantStatus;
}

/**
 * The lowest catalogue plan that includes a feature the workspace's plan lacks (`entitlements.unlocks`). `feature` is the
 * plan-gate key (`video_generation`, `resolution`, ...); the name is the catalogue's, never typed in the UI.
 */
export interface PlanUnlock {
  feature: string;
  planKey: string;
  planName: string;
}

export type FirstVideoGrantStatus = 'granted' | 'unavailable' | 'none';

/** `summary.firstVideoGrant`, in whole credits; `expiresAt` is an ISO timestamp. */
export interface FirstVideoGrant {
  credits: number;
  expiresAt: string;
  remaining: number;
}

/** `summary.assistantAllowance`, in whole credits. `includedCredits: null` means unlimited. */
export interface AssistantAllowance {
  includedCredits: number | null;
  usedCredits: number;
  periodStart: string;
  periodEnd: string;
}

// --------------------------------------------------------------------------- pre-run estimate

/**
 * Where one step's figure came from — `WorkflowStepCostEstimateResponse.Basis` by name. The backend
 * is explicit about this so a cold-start DEFAULT is never shown as a measurement (B7):
 *
 * - `History` — this organization's own recorded usage for this step type AND agent.
 * - `StepTypeHistory` — this organization's own usage for the step type, but not this agent.
 * - `Default` — B0's measured per-step-type defaults, i.e. no history behind it at all.
 * - `NotCharged` — a step type that is not billed in credits today.
 * - `AgentTypeHistory` — this organization's own usage for agents of the same kind.
 * - `OwnProvider` — an agent step on the organization's own provider key, so its LLM part is free.
 * - `AudioGeneration` — a music/sound-effect step priced from its own settings (takes x the model's price).
 *
 * Append-only: a value this build has never heard of is a newer backend, and is passed through by
 * name rather than dropped.
 */
export type CreditEstimateBasis =
  | 'History'
  | 'StepTypeHistory'
  | 'Default'
  | 'NotCharged'
  | 'AgentTypeHistory'
  | 'OwnProvider'
  | 'AudioGeneration';

/**
 * What the run-level usual range was built from:
 *
 * - `PastRuns` — the spread of this workflow's own past run totals (`pastRunCount` of them).
 * - `StepHistory` — the steps' own recorded costs in this workspace, combined.
 * - `FirstRun` — typical figures for these steps; nothing of this workspace's to go on yet.
 *
 * Absent on an older backend, which the UI answers with its earlier `hasHistory` wording.
 */
export type EstimateRangeBasis = 'PastRuns' | 'StepHistory' | 'FirstRun';

/** One step of a run's estimate, in whole credits. */
export interface WorkflowStepCostEstimate {
  stepOrder: number;
  stepType: string;
  agentDefinitionId: string | null;
  label: string | null;
  lowCredits: number;
  highCredits: number;
  /** What the engine holds before this step; summed, this is the run's `requiredCredits`. */
  requiredCredits: number;
  basis: CreditEstimateBasis;
  /** The step's central figure for one execution. Absent on an older backend. */
  typicalCredits?: number;
  /** How many times the step is expected to run (above 1 inside a review loop). Absent on an older backend. */
  expectedRuns?: number;
  /** A fixed-price step's ceiling ("up to"); 0 for a step priced by usage. Absent on an older backend. */
  fixedMaxCredits?: number;
}

/**
 * `GET /api/v1/projects/{projectId}/workflows/{id}/estimate` (B7): what a run is expected to cost,
 * before anyone presses Execute.
 *
 * A self-host install (`Billing:Enabled=false`) answers `billingEnabled: false` with every figure
 * zero, and the UI must then show NOTHING about cost — no estimate, no warning.
 */
export interface WorkflowCostEstimate {
  /** False when nothing is metered, estimated or gated on this deployment. */
  billingEnabled: boolean;
  /**
   * Whether the organization has any recorded step usage to estimate from. False means every figure
   * came from the per-step-type defaults, which the UI states rather than presenting as measured.
   */
  hasHistory: boolean;
  /**
   * The bottom of the usual range of the variable part (fixed-price steps excluded). An older
   * backend sent the sum of the per-step p50 here.
   */
  lowCredits: number;
  /** The top of the usual range of the variable part. An older backend sent the sum of the per-step p90. */
  highCredits: number;
  /** The run's central figure for the variable part. Absent on an older backend. */
  typicalCredits?: number;
  /** The most the fixed-price steps (e.g. generated video) can add, shown as "up to". Absent on an older backend. */
  fixedMaxCredits?: number;
  /** What the usual range was built from. Absent on an older backend. */
  rangeBasis?: EstimateRangeBasis;
  /** How many past runs `PastRuns` is based on. Absent on an older backend. */
  pastRunCount?: number;
  /** Codes for why the range is as wide as it is, widest first. Unknown codes are ignored. */
  spreadReasons?: string[];
  /** What the balance must cover for the run to start. Exactly what `POST execute` compares against. */
  requiredCredits: number;
  /** The organization's available credits (balance minus open holds); null when billing is off. */
  balanceCredits: number | null;
  affordable: boolean;
  steps: WorkflowStepCostEstimate[];
  /**
   * The caller's OWN daily video limit, what their runs used today and the room left; sent only for a workflow that
   * generates video. Absent from an older backend. `dailyLimitCredits` null = no daily limit.
   */
  videoSpendLimit?: {
    dailyLimitCredits: number | null;
    alertThresholdCredits: number | null;
    usedTodayCredits: number;
    remainingTodayCredits: number | null;
    alertReached: boolean;
    limitReached: boolean;
  } | null;
}

/**
 * The 402 body `POST .../execute` answers with when the balance cannot start the run. `required` and
 * `balance` are the ones the UI acts on; `shortfall` is derived here so every caller renders the
 * same number instead of subtracting twice.
 */
export interface InsufficientCredits {
  /** The credits the run needs up front (the sum of the per-step holds). */
  required: number;
  /** The workspace's available credits, as the server read them for this refusal. */
  balance: number;
  /** `required - balance`, floored at zero: what has to be topped up, at least, to start. */
  shortfall: number;
  /** The bottom/top of the estimate the refusal was based on; null when the body omitted them. */
  low: number | null;
  high: number | null;
  /** Whether the figures came from recorded usage; false means they are the cold-start defaults. */
  hasHistory: boolean;
  /** The server's own `missing` (required rounded up minus balance rounded down); null when the body omitted it. */
  missing?: number | null;
  /** What the run usually costs (the server's `typical`); null when there is no history. */
  typical?: number | null;
  /** The fixed-price generated-video ceiling inside `required` (`fixedMax`); null when the body omitted it. */
  fixedMax?: number | null;
  /**
   * The server's own explanation ("needs up to X to start (usually about Y)... N more are needed"), shown
   * as-is rather than composed here; null when the body carried none.
   */
  message: string | null;
}
