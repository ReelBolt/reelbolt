# Social publishing

Publish a finished video to YouTube Shorts or LinkedIn, now or at a scheduled time, and read back basic counts for 30 days.
Customer page: [`user-guide/projects/publishing.md`](user-guide/projects/publishing.md). Developer-app credentials and review
steps: [`social-publishing-credentials.md`](social-publishing-credentials.md). Draft Terms and Privacy copy (not in force):
[`social-publishing-legal-draft.md`](social-publishing-legal-draft.md). Instagram, TikTok and X are not built.

Everything lives in the Inference API (`Services/SocialPublishing/`, controllers `SocialController`, `SocialPostsController`,
`SocialMetricsController`). The engine maps no social table. Nothing is live until a platform's client id and secret are set; tests
make no live platform call (adapters take an injectable `HttpClient` and replay hand-written fixtures).

## Tables (migrations `AddSocialPublishing`, `SocialPublishingEngine`, `SocialPublishLedger`)

| Table | Holds | Tenant filter |
|-------|-------|---------------|
| `social_accounts` | One connected account per (organization, platform, remote account id): protected access and refresh tokens, expiry, granted scopes, `status` (`active`, `reconnect_needed`, `revoked`), connecting user. | Named `Tenant` |
| `social_oauth_states` | Hashed single-use state, encrypted PKCE verifier, starting user, expiry. | No (reached by its own state) |
| `social_posts` | Org, project, output reference, account, platform, text snapshot, options (privacy, AI label), `scheduled_at` UTC plus IANA time zone, `status`, lease owner and expiry, attempt count, remote id and URL, failure code and message, creator. | Named `Tenant` |
| `social_publish_ledger` | One publish slot per post (organization, platform, UTC day, post id; no foreign key to posts or projects): what the caps count. | No (reached by organization id) |
| `social_post_metrics` | One row per reading: post, captured at, views, likes, comments, shares, saves (null = the platform does not provide it), source. | No (reached via its post) |

Both caller-addressable tables are in `TenantBoundaryInventoryTests`. The text is a snapshot copied at schedule time, so editing a
post text later never changes a queued post. Platform ids are an append-only int enum (`SocialPlatformKind`); the URL slugs
(`youtube`, `linkedin`) never change once shipped because they are registered with the vendors.

## State machine

`draft -> scheduled -> publishing -> published | failed | cancelled | unknown`

- `scheduled -> publishing` is one conditional `UPDATE` that sets `lease_owner` and `lease_expires_at` (default 120 s, renewed every
  third of that while uploading). Two replicas cannot both claim a post.
- A `failed` or `unknown` post can be retried; a `scheduled` one can be edited or cancelled. Retrying an `unknown` post needs
  `confirmNotPosted: true` (else `409 confirm_not_posted`). Disconnecting an account cancels its queued posts, and so does the deletion of its organization (see Disconnect and deletion).
- Failure codes (`SocialFailureCodes`): `missed_window`, `outcome_unknown`, `output_missing`, `output_unavailable`, `access_lost`,
  `account_unavailable`, `reconnect_needed`, `plan_not_entitled`, `platform_unavailable`, `preflight_failed`, `monthly_cap_reached`,
  `daily_budget_reached`, `moderation_blocked`, `moderation_pending`, `platform_refused`, `retries_exhausted`, `rate_limited`,
  `platform_error`, `cancelled`. Messages are neutral and carry no token or raw platform body.

## Lease and "unknown is never retried"

The sweeper (`SocialPublishSweeper`, every `Engine:SweepIntervalSeconds`) first turns expired leases into `unknown`, then fails
posts too late (`missed_window`), then claims due posts within the concurrency caps.

If a process dies or loses its lease after the platform may have received the final publish request, the post becomes `unknown`
and is **never retried automatically**: a duplicate public post is worse than a missed one. The adapter sets `PostMayExist` on any
failure after that request is sent. The user is told to check the channel and may retry after confirming. If the platform answers
after the lease was lost, the remote id is still recorded, even over `unknown`.

Retryable failures (rate limit, network, platform 5xx) back off `BackoffSeconds * 4^(n-1)` (or the platform's `Retry-After` when
larger) up to `MaxAttempts` (3). Permanent failures (refusal, expired grant, over a limit) fail at once.

## Fire-time re-check

`PrepareAsync` runs for every claim, in this order, and fails closed:

1. Organization exists and is not deleted.
2. Project exists and belongs to the organization (a project that is gone fails the post as `output_missing`).
3. **The person who scheduled it** is still a member with Edit permission on the project, now.
4. Account exists, belongs to the org and platform, and is `active` (`reconnect_needed` and `revoked` have their own codes).
5. Plan feature `social_publishing` is still on (`PlanGate.CheckSocialPublishing`).
6. Platform is configured.
7. Output still resolves; `ProjectOutputHold` and the project quarantine marker do not cover it.
8. Platform limits preflight (`SocialPreflight`, table below) on the real file.
9. Moderation of the text and the video through `ISocialPostModeration` (fail closed; a pending verdict is retryable).
10. Caps, last, so a post refused for any other reason never takes a slot: monthly per organization, the organization's share of the
    platform's daily budget, and the platform's global daily budget (see the ledger below).

## Disconnect and organization deletion

Disconnect and organization deletion share one path (`SocialConnectionService.RevokeAccountAsync`): under the **same per-account guard as
a token refresh**, revoke at the platform (best effort), blank both tokens, delete the account's metrics, cancel its draft and scheduled
posts and mark the account `revoked`. A refresh in flight therefore finishes before the account is revoked, and one that starts later
reloads the row under the guard and sees `revoked`: it can never write tokens or `active` over a revoked account.

Organizations are only soft-deleted (the Go API sets `organizations.deleted_at`; so does the account purge), so nothing cascades. The
publish sweeper calls `PurgeDeletedOrganizationsAsync` each pass: every account of a deleted organization that still holds tokens gets the
same treatment (queued posts end as `organization_deleted`). A failure is retried on the next pass. This cleanup runs even when no
provider is configured (it only deletes tokens); only the publish half of a pass needs a configured platform.

## Assistant approval and the text snapshot

The confirmation token binds the draft's arguments **and a hash of the resolved title, body and hashtags** (a saved `postTextId` is read
when the card text is made). `ApplySchedulePost` resolves the text again, checks the token against the hash, and creates the post with
exactly that snapshot (never the saved text a second time). A saved post text edited between the proposal, the card and the click
therefore makes the token stale (`stale confirmationToken`) and nothing is scheduled; the card is not shown at all when the text it would
carry no longer matches the token.

## Org-authority re-check

OAuth callback and disconnect re-read the **current** Owner/Admin role (`IsCurrentAdminAsync`, `OrgAuthority.IsOrganizationAdmin`)
instead of trusting the role at the time the state was issued, so a member demoted between "Connect" and the callback cannot attach
or detach an account. Token refresh and metrics reads skip accounts of a deleted organization.

## Security decisions

- Tokens go through `ISecretProtector`, are never logged and never returned by any route or assistant tool.
- State is hashed, single use and bound to the starting user; the anonymous callback validates its own state. PKCE verifier encrypted.
- Refresh runs under a cross-replica Postgres advisory lock per account (`SocialRefreshLock`); after waiting, the stored token is read
  again because another replica may have refreshed. Refresh margin 2 minutes, one forced retry on 401; a failed refresh sets
  `reconnect_needed` and notifies the person who connected the account, once (on the change into `reconnect_needed`; the metrics
  collector and the publish engine share this, and a later failed refresh of an already flagged account sends nothing more).
- LinkedIn fails closed on visibility: only an explicit `public` is `PUBLIC`, anything else is `CONNECTIONS`; the preflight rejects
  `unlisted` and `private`. YouTube defaults to `private`.
- Hashtags are reduced to letters, digits and underscore in the adapter, `ComposeBody` and route validation, so nothing a platform's
  markup could read as a mention, link or escape gets through. Reserved characters in the body stay escaped.
- The video streams from the object store to the platform in one request; it is never held in memory or written to disk.
- Project history auto-capture leaves the two assistant `Apply` tools out (with a recorded reason), because a schedule or cancel changes no project file.

## Metrics

`SocialMetricsCollector` (hosted by `SocialMetricsSweeper`, every `MetricsSweepIntervalSeconds`, default 900) reads each published
post at 1 h, 6 h, 1 d, 3 d, 7 d, 14 d and 30 d after `published_at`, plus 2 days of grace, then stops. Counts: views, likes, comments,
shares, saves; null means "not provided". YouTube: `videos.list` statistics (views, likes, comments). LinkedIn: member post
analytics need a scope a Share-on-LinkedIn app does not have, so every number is null (`linkedin.unavailable`). A rate-limited
platform pauses the sweep. Routes: `GET /api/v1/social/posts/{id}/metrics`, `GET /api/v1/social/metrics/summary`. Disconnecting
deletes the account's metrics.

## Limits table

`SocialPlatformLimits` is the one versioned table (`Version` 1, `LastChecked` 2026-11-13). Re-check against each platform's
developer page before each release; a change is a new `Version` and date.

| Platform | Video size | Length | Aspect (w/h) | Title | Notes |
|----------|-----------:|-------:|-------------:|-------|-------|
| YouTube Shorts | 8 GiB (our cap) | 1 to 180 s | 0.4 to 1.0 | required | Text limits come from `PostTextPlatformLimits.YouTubeShorts`. |
| LinkedIn | 5 GiB | 3 to 1800 s | 1/2.4 to 2.4 | none | Text limits from `PostTextPlatformLimits.LinkedIn`; privacy `public` or `connections`. |

Caps are counted from the **publish ledger** (`social_publish_ledger`: organization id, platform, UTC day, post id), one row per post
written when the post passes the checks above and takes its slot (a retry of the same post takes no second one; a post refused by a cap
gives its fresh slot straight back). It has no foreign key to posts or projects, so deleting a project (which cascades its posts away)
never hands quota back; it goes only with the organization. Unfiltered table reached by organization id inside the engine
(`TenantBoundaryInventoryTests` lists it with the parent-reached tables).

Quotas and caps: YouTube costs 1,600 units per upload and 1 per statistics read against a default 10,000 per day, so
`DailyBudgetYouTube` is 6 uploads a day for all customers together until Google grants an extension; 400 units are set aside for reads
(`MetricsQuotaUnitsPerDay`). One organization may take at most `SocialPublishing:YouTube:MaxPerOrgPerDay` of them (default 2, 0 = no
per-organization limit), so a single workspace cannot use up the shared quota. `DailyBudgetLinkedIn` 150. `MonthlyPostCapPerOrganization`
300 (slots taken in a UTC calendar month). Scheduling is allowed up to 366 days ahead; a time more than a minute in the past is refused.

## Routes

`GET /api/v1/social/providers` (configured platforms only), `GET accounts`, `POST {provider}/connect` (Owner/Admin; `503
provider_not_configured`), anonymous `GET {provider}/oauth/callback`, `DELETE accounts/{id}`; `POST posts/preflight`, `POST posts`,
`GET posts`, `GET posts/{id}`, `PATCH posts/{id}`, `POST posts/{id}/cancel`, `POST posts/{id}/retry`; the two metrics routes above. Creating a post
checks the plan feature; the web UI is the Publishing page and the Publish dialog.

## Plan and notifications

Plan feature `social_publishing` (`socialPublishingAllowed` in both byte-identical `plans.catalog.json` copies): off on `trial`,
on for `creator`, `pro`, `team`, `enterprise`. Refused as `PLAN_NOT_ENTITLED` with feature `social_publishing`. Notification category
`social_publishing` (published, failed, reconnect needed), spelled the same in Go, the preference column and TypeScript.

## Assistant tools

Seven tools in `AssistantSocialTools`, each running the app's own route code: reads `ListSocialAccounts`, `ListScheduledPosts`,
`GetPostPerformance`; pairs `ProposeSchedulePost`/`ApplySchedulePost` and `ProposeCancelScheduledPost`/`ApplyCancelScheduledPost`.
The two `Apply` tools are in the **always-ask** approval class: the card appears in auto mode, over any cost limit, and "always allow"
does not switch it off. The card names account, platform, project, video and time by name and shows the whole text. The pairs are not in
the MCP catalog and refuse MCP and unattended turns; the three reads are read-only over MCP. There is no workflow step. Counts: 153 chat
tools, 101 over MCP. See [`platform-assistant-mcp.md`](platform-assistant-mcp.md).

## Configuration

Compose passes `SocialPublishing__*` (names in [`secrets.md`](secrets.md)); a platform with no id and secret is hidden.

| Key | Default | Meaning |
|-----|---------|---------|
| `SocialPublishing:Enabled` | true | Off hides every platform. |
| `SocialPublishing:PublicBaseUrl` (`PUBLIC_BASE_URL`) | none | Origin for the redirect URI; unset or not http(s) makes connect answer 503. |
| `SocialPublishing:YouTube:ClientId` / `ClientSecret` | none | `SOCIAL_YOUTUBE_CLIENT_ID` / `SOCIAL_YOUTUBE_CLIENT_SECRET`. |
| `SocialPublishing:YouTube:QuotaUnitsPerDay` / `MetricsQuotaUnitsPerDay` | 10000 / 400 | Our view of the Google quota. |
| `SocialPublishing:YouTube:MaxPerOrgPerDay` | 2 | One organization's share of `DailyBudgetYouTube`; 0 = no per-organization limit. |
| `SocialPublishing:LinkedIn:ClientId` / `ClientSecret` | none | `SOCIAL_LINKEDIN_CLIENT_ID` / `SOCIAL_LINKEDIN_CLIENT_SECRET`. |
| `SocialPublishing:LinkedIn:ApiVersion` | 202511 | `LinkedIn-Version` header (YYYYMM); LinkedIn retires versions after about a year. |
| `SocialPublishing:HttpTimeoutSeconds` | 300 | Per request, uploads included. |
| `SocialPublishing:Engine:SweepIntervalSeconds` | 30 | 0 or less turns the sweeper off. |
| `SocialPublishing:Engine:LeaseSeconds` | 120 | Claim lifetime. |
| `SocialPublishing:Engine:MissedWindowMinutes` | 60 | Later than this fails `missed_window`. |
| `SocialPublishing:Engine:MaxAttempts` / `BackoffSeconds` | 3 / 60 | Retryable failures. |
| `SocialPublishing:Engine:MaxConcurrentPerPlatform` / `PerAccount` | 2 / 1 | Uploads at once. |
| `SocialPublishing:Engine:MonthlyPostCapPerOrganization` | 300 | 0 = no cap. |
| `SocialPublishing:Engine:DailyBudgetYouTube` / `DailyBudgetLinkedIn` | 6 / 150 | Posts per UTC day across all organizations; 0 = no cap. |
| `SocialPublishing:Engine:MetricsSweepIntervalSeconds` / `MetricsBatchSize` | 900 / 50 | 0 or less turns the collector off. |
| `SocialPublishing:Engine:ClaimBatchSize` | 10 | Due posts claimed per sweep. |
