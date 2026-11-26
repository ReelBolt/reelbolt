import type { BlogPost, BlogTag, PostMeta } from './types';
import { findBySlug, listedOnly, validatePosts } from './validate';
import { meta as helloMeta } from '@/content/blog/hello-reelbolt';
import HelloReelBolt from '@/content/blog/hello-reelbolt';
import { meta as promoToolsMeta } from '@/content/blog/product-promo-video-tools-2026';
import ProductPromoVideoTools2026 from '@/content/blog/product-promo-video-tools-2026';
import { meta as arcadeMeta } from '@/content/blog/reelbolt-vs-arcade';
import ReelboltVsArcade from '@/content/blog/reelbolt-vs-arcade';
import { meta as avatarsMeta } from '@/content/blog/reelbolt-avatars';
import ReelboltAvatars from '@/content/blog/reelbolt-avatars';
import { meta as descriptMeta } from '@/content/blog/reelbolt-vs-descript';
import ReelboltVsDescript from '@/content/blog/reelbolt-vs-descript';
import { meta as heygenMeta } from '@/content/blog/reelbolt-vs-heygen';
import ReelboltVsHeygen from '@/content/blog/reelbolt-vs-heygen';
import { meta as invideoMeta } from '@/content/blog/reelbolt-vs-invideo';
import ReelboltVsInvideo from '@/content/blog/reelbolt-vs-invideo';
import { meta as runwayMeta } from '@/content/blog/reelbolt-vs-runway';
import ReelboltVsRunway from '@/content/blog/reelbolt-vs-runway';
import { meta as openSourceMeta } from '@/content/blog/why-reelbolt-is-open-source';
import WhyReelBoltIsOpenSource from '@/content/blog/why-reelbolt-is-open-source';
import { meta as speechMeta } from '@/content/blog/edits-that-respect-speech';
import EditsThatRespectSpeech from '@/content/blog/edits-that-respect-speech';
import { meta as enhanceMeta } from '@/content/blog/enhance-a-clip';
import EnhanceAClip from '@/content/blog/enhance-a-clip';
import { meta as apiMeta } from '@/content/blog/reelbolt-public-api';
import ReelboltPublicApi from '@/content/blog/reelbolt-public-api';
import { meta as whatsNewMeta } from '@/content/blog/whats-new-reelbolt-0-3';
import WhatsNew03 from '@/content/blog/whats-new-reelbolt-0-3';
import { meta as marketMeta } from '@/content/blog/recipe-marketplace';
import RecipeMarketplace from '@/content/blog/recipe-marketplace';
import { meta as storyToTellMeta } from '@/content/blog/a-story-to-tell';
import AStoryToTell from '@/content/blog/a-story-to-tell';
import { meta as firstVideoMeta } from '@/content/blog/first-video-in-one-sitting';
import FirstVideoInOneSitting from '@/content/blog/first-video-in-one-sitting';
import { meta as shotListMeta } from '@/content/blog/five-clip-shot-list';
import FiveClipShotList from '@/content/blog/five-clip-shot-list';
import { meta as smallBusinessMeta } from '@/content/blog/video-for-your-small-business';
import VideoForYourSmallBusiness from '@/content/blog/video-for-your-small-business';
import { meta as lessonsMeta } from '@/content/blog/lessons-into-short-clips';
import LessonsIntoShortClips from '@/content/blog/lessons-into-short-clips';
import { meta as causeMeta } from '@/content/blog/tell-your-cause';
import TellYourCause from '@/content/blog/tell-your-cause';
import { meta as weekendsMeta } from '@/content/blog/stop-editing-on-weekends';
import StopEditingOnWeekends from '@/content/blog/stop-editing-on-weekends';
import { meta as highlightRemovesMeta } from '@/content/blog/what-a-highlight-edit-removes';
import WhatAHighlightEditRemoves from '@/content/blog/what-a-highlight-edit-removes';
import { meta as nonEditorsMeta } from '@/content/blog/ai-video-editors-for-people-who-do-not-edit-2026';
import AiVideoEditorsForPeopleWhoDoNotEdit2026 from '@/content/blog/ai-video-editors-for-people-who-do-not-edit-2026';
import { meta as capcutMeta } from '@/content/blog/reelbolt-vs-capcut';
import ReelboltVsCapcut from '@/content/blog/reelbolt-vs-capcut';
import { meta as opusclipMeta } from '@/content/blog/reelbolt-vs-opusclip';
import ReelboltVsOpusclip from '@/content/blog/reelbolt-vs-opusclip';
import { meta as veedMeta } from '@/content/blog/reelbolt-vs-veed';
import ReelboltVsVeed from '@/content/blog/reelbolt-vs-veed';
import { meta as canvaMeta } from '@/content/blog/reelbolt-vs-canva';
import ReelboltVsCanva from '@/content/blog/reelbolt-vs-canva';

// Each content module exports `meta` (everything except Body) and its component as the default
// export. Add a post here, nowhere else. `validatePosts` runs at import time, so a bad entry fails
// the build.
const entries: { meta: PostMeta; Body: BlogPost['Body'] }[] = [
  { meta: openSourceMeta, Body: WhyReelBoltIsOpenSource },
  { meta: helloMeta, Body: HelloReelBolt },
  { meta: descriptMeta, Body: ReelboltVsDescript },
  { meta: runwayMeta, Body: ReelboltVsRunway },
  { meta: heygenMeta, Body: ReelboltVsHeygen },
  { meta: invideoMeta, Body: ReelboltVsInvideo },
  { meta: arcadeMeta, Body: ReelboltVsArcade },
  { meta: promoToolsMeta, Body: ProductPromoVideoTools2026 },
  { meta: avatarsMeta, Body: ReelboltAvatars },
  { meta: speechMeta, Body: EditsThatRespectSpeech },
  { meta: enhanceMeta, Body: EnhanceAClip },
  { meta: apiMeta, Body: ReelboltPublicApi },
  { meta: whatsNewMeta, Body: WhatsNew03 },
  { meta: marketMeta, Body: RecipeMarketplace },
  { meta: storyToTellMeta, Body: AStoryToTell },
  { meta: firstVideoMeta, Body: FirstVideoInOneSitting },
  { meta: shotListMeta, Body: FiveClipShotList },
  { meta: smallBusinessMeta, Body: VideoForYourSmallBusiness },
  { meta: lessonsMeta, Body: LessonsIntoShortClips },
  { meta: causeMeta, Body: TellYourCause },
  { meta: weekendsMeta, Body: StopEditingOnWeekends },
  { meta: highlightRemovesMeta, Body: WhatAHighlightEditRemoves },
  { meta: nonEditorsMeta, Body: AiVideoEditorsForPeopleWhoDoNotEdit2026 },
  { meta: capcutMeta, Body: ReelboltVsCapcut },
  { meta: opusclipMeta, Body: ReelboltVsOpusclip },
  { meta: veedMeta, Body: ReelboltVsVeed },
  { meta: canvaMeta, Body: ReelboltVsCanva },
];

validatePosts(entries.map((entry) => entry.meta));

const POSTS: BlogPost[] = entries
  .map(({ meta, Body }) => ({ ...meta, Body }))
  .sort((a, b) => (a.date === b.date ? a.slug.localeCompare(b.slug) : a.date < b.date ? 1 : -1));

/**
 * Every post, listed or unlisted, newest first. Use this for build-time routes (static params) so
 * an unlisted post still gets its page. Listings must use `listedPosts()`.
 */
export function allPosts(): BlogPost[] {
  return POSTS;
}

/** Every post a listing shows: the registry without the `unlisted` ones, newest first. */
export function listedPosts(): BlogPost[] {
  return listedOnly(POSTS);
}

/** The post at this slug, listed or unlisted. */
export function postBySlug(slug: string): BlogPost | undefined {
  return findBySlug(POSTS, slug);
}

export function postsByTag(tag: BlogTag): BlogPost[] {
  return listedPosts().filter((post) => post.tags.includes(tag));
}

/** Tags that have at least one listed post, in the order they first appear newest-first. */
export function tagsWithPosts(): BlogTag[] {
  const seen: BlogTag[] = [];
  for (const post of listedPosts()) {
    for (const tag of post.tags) {
      if (!seen.includes(tag)) seen.push(tag);
    }
  }
  return seen;
}
