/** Social publishing wire types (inference SocialDtos / SocialPostDtos / SocialMetricsDtos). camelCase JSON, no token ever. */

export type SocialProviderId = 'youtube' | 'linkedin' | (string & {});
export type SocialAccountStatus = 'active' | 'reconnect_needed' | 'revoked';
export type SocialPostStatus = 'draft' | 'scheduled' | 'publishing' | 'published' | 'failed' | 'cancelled' | 'unknown';
export type SocialOutputKind = 'step_result' | 'project_file';
export type SocialPrivacy = 'public' | 'unlisted' | 'private' | 'connections';

export interface SocialProvider { provider: SocialProviderId; displayName: string }

export interface SocialAccount {
  id: string;
  provider: SocialProviderId;
  displayName: string | null;
  avatarUrl: string | null;
  status: SocialAccountStatus;
  lastError: string | null;
  createdAt: string;
}

export interface SocialPostOptions { privacy?: SocialPrivacy | null; aiGenerated?: boolean | null; commentsEnabled?: boolean | null }

export interface SocialPost {
  id: string;
  projectId: string;
  outputKind: SocialOutputKind;
  outputId: string;
  accountId: string;
  platform: SocialProviderId;
  format: string;
  title: string | null;
  body: string | null;
  hashtags: string[];
  postTextId: string | null;
  options: SocialPostOptions;
  scheduledAt: string | null;
  scheduledLocal: string | null;
  timeZone: string | null;
  status: SocialPostStatus;
  attempts: number;
  nextAttemptAt: string | null;
  remotePostUrl: string | null;
  failureCode: string | null;
  failureMessage: string | null;
  createdByUserId: string;
  createdAt: string;
  updatedAt: string;
  publishedAt: string | null;
  canEdit: boolean;
  canCancel: boolean;
  canRetry: boolean;
}

export interface SocialPostList { items: SocialPost[]; total: number }

export interface SocialPreflightIssue { code: string; severity: 'error' | 'warning'; field: string; message: string; rerenderVertical: boolean }
export interface SocialPreflight {
  platform: SocialProviderId;
  limitsVersion: number;
  limitsLastChecked: string;
  ok: boolean;
  issues: SocialPreflightIssue[];
}

export interface SocialPostContent {
  title: string | null;
  body: string | null;
  hashtags: string[];
  options: SocialPostOptions;
}

export interface CreateSocialPostBody extends SocialPostContent {
  projectId: string;
  outputKind: SocialOutputKind;
  outputId: string;
  accountId: string;
  postTextId?: string | null;
  publishNow: boolean;
  scheduledLocal?: string | null;
  timeZone?: string | null;
}

export interface UpdateSocialPostBody extends Partial<SocialPostContent> {
  scheduledLocal?: string | null;
  timeZone?: string | null;
}

export interface SocialMetricReading {
  capturedAt: string;
  views: number | null;
  likes: number | null;
  comments: number | null;
  shares: number | null;
  saves: number | null;
  source: string;
}
export interface SocialMetricTotals { views: number | null; likes: number | null; comments: number | null; shares: number | null; saves: number | null }
export interface SocialPlatformMetrics { platform: SocialProviderId; posts: number; totals: SocialMetricTotals }
export interface SocialPostMetricsItem {
  postId: string;
  projectId: string;
  platform: SocialProviderId;
  title: string | null;
  publishedAt: string | null;
  remotePostUrl: string | null;
  latest: SocialMetricReading | null;
}
export interface SocialMetricsSummary {
  days: number;
  publishedPosts: number;
  totals: SocialMetricTotals;
  platforms: SocialPlatformMetrics[];
  posts: SocialPostMetricsItem[];
}
