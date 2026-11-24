/**
 * Tracked links and their funnel (docs/design/new-user-funnel.md sections 4 and 6). The closed sets
 * below are the ones the Inference API validates against (`AcquisitionVocabulary`); the wire types
 * keep channel, medium and persona as plain strings so a row written by a newer server still renders.
 */

/** Where a link is posted. `utm_source` of the link. */
export const LINK_CHANNELS = [
  'hackernews',
  'reddit',
  'linkedin',
  'x',
  'producthunt',
  'youtube',
  'tiktok',
  'instagram',
  'facebook',
  'github',
  'discord',
  'newsletter',
  'email',
  'blog',
  'directory',
  'press',
  'partner',
  'ads',
  'other',
] as const;
export type LinkChannel = (typeof LINK_CHANNELS)[number];

/** The kind of placement. `utm_medium` of the link. */
export const LINK_MEDIA = ['social', 'community', 'video', 'email', 'referral', 'paid', 'owned', 'press'] as const;
export type LinkMedium = (typeof LINK_MEDIA)[number];

/** A link code: 2 to 40 characters. It is the link's `utm_content`, so it is always a valid UTM value. */
export const LINK_CODE_PATTERN = /^[a-z0-9][a-z0-9-]{1,39}$/;
export const LINK_CODE_MAX_LENGTH = 40;

/** A UTM value, as every service sanitizes it. A link's campaign token is one. */
export const UTM_VALUE_PATTERN = /^[a-z0-9][a-z0-9._~+-]{0,63}$/;
export const UTM_VALUE_MAX_LENGTH = 64;

export const LINK_PLACEMENT_MAX_LENGTH = 160;
export const LINK_DESTINATION_MAX_LENGTH = 200;

/** The funnel windows the page offers, in days. The API clamps to 1..365. */
export const FUNNEL_PERIODS = [7, 30, 90, 365] as const;
export type FunnelPeriod = (typeof FUNNEL_PERIODS)[number];
export const DEFAULT_FUNNEL_PERIOD: FunnelPeriod = 30;

/** A tracked link: list item, create response and patch response. */
export interface MarketingLink {
  id: string;
  code: string;
  channel: string;
  medium: string;
  campaign: string;
  placement: string;
  destinationPath: string;
  persona: string | null;
  costMinor: number | null;
  costCurrency: string | null;
  createdAt: string;
  archivedAt: string | null;
  /** `/l/<code>`: the address the redirect answers on the site's host. */
  shortPath: string;
  /** The landing page plus the link's campaign parameters; works without the redirect. */
  longPath: string;
  totalClicks: number;
}

export interface MarketingLinkList {
  items: MarketingLink[];
}

export interface CreateMarketingLinkRequest {
  code: string;
  channel: string;
  medium: string;
  campaign: string;
  placement: string;
  destinationPath: string;
  persona?: string;
  costMinor?: number;
  costCurrency?: string;
}

/** Every field is optional: an absent one is left alone, `null` clears persona and cost. */
export interface PatchMarketingLinkRequest {
  placement?: string;
  destinationPath?: string;
  persona?: string | null;
  costMinor?: number | null;
  costCurrency?: string | null;
  archived?: boolean;
}

/** How many accounts reached each step. Steps are counted independently, not as a strict sequence. */
export interface FunnelAccountSteps {
  signups: number;
  verified: number;
  onboarded: number;
  firstProject: number;
  firstRun: number;
  firstVideo: number;
  paid: number;
}

export interface MarketingLinkFunnelItem extends FunnelAccountSteps {
  linkId: string;
  code: string;
  channel: string;
  medium: string;
  campaign: string;
  placement: string;
  destinationPath: string;
  archived: boolean;
  costMinor: number | null;
  costCurrency: string | null;
  clicks: number;
  botHits: number;
  visitors: number;
}

export interface MarketingLinkFunnel {
  days: number;
  generatedAt: string;
  /** Mirrors `Attribution:UrlCaptureEnabled`: whether account counts per link are complete. */
  urlCaptureEnabled: boolean;
  /**
   * Whether this server accepts the visit beacon at all (`TELEMETRY_VISIT_INGEST_ENABLED`). False:
   * no visitor row is ever written, so every link's `visitors` is 0 whatever people accepted on the
   * site. Absent from an API that does not report it.
   */
  visitIngestEnabled?: boolean;
  items: MarketingLinkFunnelItem[];
  /**
   * The accounts of the period that belong to no link: no link code at all, or a `utm_content` that
   * is no link's code. With the link rows it is every account of the period, each counted once.
   */
  unattributed: FunnelAccountSteps;
}
