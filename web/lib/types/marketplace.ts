import type { AppCategory, AppCostClass, RecipeNeedAction } from './workflow';

/** Community recipe gallery shapes (docs/design/recipe-marketplace.md). Free text is plain text: render it as text only. */

export type MarketplaceSort = 'new' | 'copied' | 'rated';

export interface MarketplaceRecipeCard {
  id: string;
  name: string;
  /** First 200 characters of the description. */
  description: string;
  category: AppCategory | string;
  language: string;
  costClass: AppCostClass;
  stepCount: number;
  stepTypes: string[];
  authorLabel: string;
  copyCount: number;
  /** Null until the server decides to share an average. */
  ratingAverage: number | null;
  ratingCount: number;
  previewSeconds: number;
  listedAt: string;
  version: number;
}

export interface MarketplaceStep {
  order: number;
  type: string;
  label: string;
  /** Short plain strings: loop rounds, maximum spend. */
  limits?: string[];
}

export interface MarketplacePrompt {
  stepOrder: number;
  field: string;
  text: string;
}

export type MarketplaceSlotKind = 'provider' | 'file' | 'voice';

export interface MarketplaceSlot {
  kind: MarketplaceSlotKind;
  stepOrder: number;
  label: string;
}

export interface MarketplaceInput {
  key: string;
  label: string;
  kind: string;
}

export interface MarketplaceRecipeDetail extends MarketplaceRecipeCard {
  steps: MarketplaceStep[];
  prompts: MarketplacePrompt[];
  slots: MarketplaceSlot[];
  inputs: MarketplaceInput[];
  costNote: string;
}

export interface MarketplaceRecipePage {
  items: MarketplaceRecipeCard[];
  nextCursor?: string | null;
}

export interface MarketplaceFacet {
  key: string;
  count: number;
}

export interface MarketplaceFacets {
  categories: MarketplaceFacet[];
  stepTypes: MarketplaceFacet[];
  languages: MarketplaceFacet[];
  costClasses?: MarketplaceFacet[];
}

export interface MarketplaceMe {
  copied: boolean;
  canRate: boolean;
  myStars: number | null;
  reported: boolean;
}

/** An open slot left in a copy, reported like a template card's "needs". */
export interface MarketplaceNeed {
  need: string;
  message: string;
  action: RecipeNeedAction;
}

export interface MarketplaceCopyResult {
  workflowId: string;
  name: string;
  needs: MarketplaceNeed[];
}

export const REPORT_REASONS = ['illegal', 'sexual', 'copyright', 'spam', 'misleading', 'other'] as const;
export type MarketplaceReportReason = (typeof REPORT_REASONS)[number];

export const REPORT_NOTE_MAX = 500;

export interface MarketplaceFilters {
  q: string;
  category: string;
  cost: string;
  stepType: string;
  language: string;
  sort: MarketplaceSort;
}

/* ---- Publish, My recipes, admin (W-2). Admin DTO field names are this build's choice; the API must match them. ---- */

export type MarketplaceRecipeStatus = 'draft' | 'pending' | 'listed' | 'rejected' | 'removed' | 'unpublished';
export type MarketplacePreviewStatus = 'preparing' | 'ready' | 'failed';

export interface MarketplaceConsent {
  version: string;
  text: string;
  licenceSummary: string;
}

export interface PublishRefusal {
  code: string;
  stepOrder?: number | null;
  message: string;
}

export interface PublishRemoved {
  stepOrder: number;
  field: string;
  what: string;
}

export interface PublishPreview {
  publishable: boolean;
  refusals: PublishRefusal[];
  removed: PublishRemoved[];
  slots: MarketplaceSlot[];
  steps: MarketplaceStep[];
  prompts: MarketplacePrompt[];
  costClass: AppCostClass;
}

export interface PublishBody {
  projectId: string;
  workflowId: string;
  /** `step_result:<guid>` (a render) or `file:<guid>` (a project file). */
  outputRef: string;
  name: string;
  description: string;
  category: string;
  language: string;
  authorLabel: string;
  consentAccepted: boolean;
  consentVersion: string;
}

export interface PublishResult {
  id: string;
  status: MarketplaceRecipeStatus;
  version: number;
  previewStatus: MarketplacePreviewStatus;
}

export interface MineRecipe {
  id: string;
  name: string;
  status: MarketplaceRecipeStatus;
  /** Reason category when rejected or removed (a category only, never free text). */
  reasonCategory?: string | null;
  /** True while the recipe is hidden pending a review of reports. */
  hiddenByReports?: boolean;
  version: number;
  previewStatus: MarketplacePreviewStatus;
  copyCount: number;
  runCount: number;
  ratingAverage: number | null;
  ratingCount: number;
  description?: string;
  category?: string;
  language?: string;
  authorLabel?: string;
}

export const REJECT_REASONS = ['content', 'unsafe', 'copyright', 'low_quality', 'other'] as const;
export type MarketplaceRejectReason = (typeof REJECT_REASONS)[number];

export type AdminTab = 'pending' | 'reports' | 'listed' | 'removed';

export interface AdminChecks {
  /** Text verdict, e.g. "pass", "review", "block". */
  text?: string | null;
  preview?: string | null;
  lintCodes?: string[];
}

export interface AdminRecipe {
  id: string;
  /** The version the row shows; approve and reject send it back with the digest. */
  versionId: string;
  digest: string;
  hiddenByReports?: boolean;
  /** Status and SHA-256 of the preview file of this version, when the API sends them. */
  previewStatus?: MarketplacePreviewStatus | null;
  previewSha256?: string | null;
  name: string;
  description?: string;
  authorLabel: string;
  /** The organization name exactly as the API gives it. */
  orgName?: string | null;
  version: number;
  status: MarketplaceRecipeStatus;
  checks?: AdminChecks;
  prompts?: MarketplacePrompt[];
}

export interface AdminReport {
  id: string;
  recipeId: string;
  recipeName: string;
  reason: string;
  note?: string | null;
  /** Open reports on this recipe. */
  count?: number;
  createdAt?: string;
}
