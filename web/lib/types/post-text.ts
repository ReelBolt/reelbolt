/** Wire types of `/api/v1/projects/{projectId}/outputs/{outputId}/post-texts` (camelCase). */

export type PostTextPlatformId = 'tiktok' | 'instagram_reels' | 'youtube_shorts' | 'linkedin' | 'x';
export type HashtagSourceChoice = 'model' | 'web';

export interface PostTextRequest {
  platforms: string[];
  tone?: string | null;
  language?: string | null;
  about?: string | null;
  readVideo: boolean;
  hashtagSource: HashtagSourceChoice;
}

export interface PostTextPlatformInfo {
  id: string;
  label: string;
  hasTitle: boolean;
  maxTitleChars: number;
  maxBodyChars: number;
  maxHashtags: number;
  suggestedHashtags: number;
  hashtagsInBody: boolean;
}

export interface PostTextLineItem {
  kind: string;
  credits: number | null;
  state: string;
  note: string | null;
  heldSeparately: boolean;
}

export interface PostTextStartTranscript {
  method: string;
  path: string;
  kinds: string[];
}

export interface PostTextVideoState {
  mediaRef: string;
  transcript: string;
  frames: string;
  transcriptNote: string | null;
  framesNote: string | null;
  startTranscript: PostTextStartTranscript | null;
}

export interface PostTextEstimate {
  billingEnabled: boolean;
  credits: number | null;
  balance: number | null;
  affordable: boolean;
  included: boolean;
  model: string | null;
  language: string;
  platforms: { version: number; lastChecked: string; items: PostTextPlatformInfo[] };
  lineItems: PostTextLineItem[];
  disclosure: string | null;
  video: PostTextVideoState | null;
  totalCredits: number | null;
}

export interface HashtagCitation {
  title: string;
  host: string;
  url: string;
}

export interface HashtagBasis {
  kind: string;
  source: string;
  /** Shown as it is; never reworded. */
  label: string;
  sources?: HashtagCitation[] | null;
  note?: string | null;
}

export interface PostTextDraft {
  platform: string;
  label: string;
  title: string | null;
  body: string;
  hashtags: string[];
  text: string;
  characters: number;
  limit: number;
  titleLimit: number | null;
  trimmed: boolean;
}

export interface PostTextResult {
  outputId: string;
  language: string;
  tone: string | null;
  creditsUsed: number | null;
  drafts: PostTextDraft[];
  hashtagBasis: HashtagBasis;
  video?: PostTextVideoState | null;
}

export interface PostTextPreparing {
  status: 'preparing';
  retryAfterSeconds: number;
  video?: PostTextVideoState | null;
  message?: string;
}

export interface SavedPostText {
  id: string;
  outputId: string;
  platform: string;
  language: string;
  title: string | null;
  body: string;
  hashtags: string[];
  text: string;
  characters: number;
  limit: number;
  titleLimit: number | null;
  tone: string | null;
  edited: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface SavedPostTextList {
  items: SavedPostText[];
}

export interface SavePostTextBody {
  language: string;
  title: string | null;
  body: string;
  hashtags: string[];
  tone: string | null;
  edited: boolean;
}
