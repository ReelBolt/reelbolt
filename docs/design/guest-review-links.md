# Guest review links (editor v2, Wave 4)

Status: design, written before the build (decision D6). Authority: this file is the contract for the backend
(`T-011`) and the frontend (`T-012`) tickets of `editor-v2-wave4`, and the input to the Security review.

## Problem
A marketer finishes a cut and wants a client or a boss, who has no ReelBolt account, to watch it, leave notes
at exact moments and say "approved" or "please change this". Today review is members-only
(`/app/editor/{projectId}/review/{timelineId}`), so people mail MP4s around and lose the thread.

## Decisions
1. **A link shows one finished render, not the live timeline.** The owner pins a specific passed render
   (default: the newest passed full-timeline render). The guest can reach that one output file and nothing
   else: no project files, no other renders, no timeline document, no source media. This bounds the public
   media exposure to a file the owner already chose to deliver.
2. **The link is a capability URL.** 32 random bytes, base64url (43 characters), returned once at creation
   inside the URL. The database keeps only its SHA-256. Anyone holding the URL can use it until it expires or
   is revoked. There is no guest account, no cookie, no ambient authority.
3. **Guests are self-asserted names.** A guest types a display name (1 to 80 characters). The UI says
   "(guest)" next to it everywhere. We store no IP address and no email.
4. **Every failure looks the same.** Unknown, malformed, expired, revoked, deleted-timeline, deleted-render
   and cross-tenant all answer one identical `404` with an empty JSON object, so a token cannot be probed.
5. **Writes are small and bounded.** Guests can add comments (and replies) and record a decision. They can
   never edit or delete anything, and cannot see members' private data.
6. **Owners are told, without spam.** Guest comments and decisions notify the link creator and opted-in
   members through the existing user-activity notifier, coalesced to at most one mail per link per 10 minutes.
7. **Optional passcode** is deliberately left out of this wave (listed as a follow-up); the expiry, the pinned
   render and instant revocation are the controls.

## Data (Inference API owns; one migration `AddGuestReviewLinks`; never alters an `ExcludeFromMigrations` table)
- `edit_review_links`: `id`, `project_id`, `organization_id`, `timeline_id`, `created_by_user_id`,
  `token_hash` (bytea 32, unique), `label` (<= 80, nullable), `render_execution_id` (the pinned render),
  `pinned_revision` (the timeline revision that render was made from), `allow_comments` (bool),
  `expires_at`, `created_at`, `revoked_at` (nullable), `last_viewed_at`, `view_count`, `owner_seen_at`,
  `last_notified_at`.
- `edit_review_decisions` (append-only): `id`, `link_id`, `project_id`, `timeline_id`,
  `decision` (`Approved` | `ChangesRequested`, stored by name), `guest_name`, `note` (<= 1000), `created_at`.
  The latest row per link is its current state.
- `edit_timeline_comments` gains nullable `guest_link_id` and `guest_name` (<= 80). A guest comment has
  `author_user_id = Guid.Empty`, so every existing equality check (edit/delete own comment) fails for it and
  members see it as "Name (guest)". Members with Edit may resolve or delete it, as for any comment.
- Tenant filter: links are addressable by caller-chosen id on the owner routes, so `edit_review_links` carries
  the named `Tenant` query filter and is added to `TenantBoundaryInventoryTests`; decisions are reached only
  through their link and are guarded at the link. Rows are deleted with the timeline (hard delete or trash
  purge), the project, the organization and the account purge.

## Owner API (authenticated; 404 not 403 across tenants; one `IProjectAccessService` consult per action)
Base `/api/v1/projects/{projectId}/edit-timelines/{id}/review-links`.
| Method | Path | Auth | Description |
|---|---|---|---|
| `POST` | `` | Edit | `{ label?, renderExecutionId?, expiresInDays? (1..90, default 14), allowComments? (default true) }` -> `201 { id, url, label, expiresAt, renderCreatedAt, stale }`. `url` appears ONLY in this response (`{origin}/app/review/g/{token}`; origin from `OutputLinks:PublicBaseUrl`/`CloudStorage:PublicBaseUrl` as output links do). The render must be a passed, non-preview, non-audio render of this timeline with an output file; otherwise `409 { code: "NO_RENDER_TO_SHARE", message }` (plain). Content-moderation publish gate applies (fail-closed when enabled), like export. At most 20 live links per timeline (`409 REVIEW_LINK_LIMIT`). |
| `GET` | `` | View | `{ links: [{ id, label, status (active\|expired\|revoked), expiresAt, createdAt, viewCount, lastViewedAt, stale, decision?: {decision, guestName, note?, at}, guestComments, unreadGuestActivity }] }`. Never contains the token or URL. |
| `GET` | `/{linkId}` | View | One link plus its decision history (<= 50) and guest comment count; marks `owner_seen_at = now` (so `unreadGuestActivity` resets). |
| `DELETE` | `/{linkId}` | Edit | Revoke now (sets `revoked_at`, `204`, idempotent). |
Member comment routes are unchanged; their DTO gains `guest: bool` and `guestName`.

## Public API (anonymous; `/api/v1/review-links/{token}`; no cookies; nginx blanks `Authorization`)
All responses: `Cache-Control: private, no-store`, `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`,
no CORS headers (same-origin page only). The token is never logged (route value excluded from logging scopes,
request-logging redaction test, nginx log format without the URI).
| Method | Path | Description |
|---|---|---|
| `GET` | `` | `{ title (timeline name), expiresAt, allowComments, renderCreatedAt, stale (timeline revision moved past pinned_revision), durationSec?, comments: [{ id, parentId?, authorName, guest, body, frame, durationFrames?, resolved, createdAt }], decision?: { decision, guestName, at } }`. Counts a view (`view_count`, `last_viewed_at`; at most one count per minute per link to avoid write amplification). No project/org/user ids, no emails (member authors appear by display name only). `comments` holds only the notes written through THIS link plus replies (any author) to those notes: member top-level notes, other links' comments and other threads are never returned. |
| `GET` | `/media` | The pinned render's mp4 with Range (`206`), `Content-Type: video/mp4`, `Content-Disposition: inline`, streamed (no URL, no storage key). Moderation publish gate applies on first serve per link (cached decision, fail-closed when enabled). |
| `POST` | `/comments` | `{ name, body, frame, durationFrames?, parentId? }` -> `201` the comment. 400 on invalid with plain messages (body 1..2000, name 1..80, no control characters after normalisation, `frame` in range, `parentId` must be a live comment of this timeline). 403-like states are 404s. Requires `allow_comments`. Caps: 200 guest comments per link, 10 per minute per link (app level), plus the nginx write zone. |
| `POST` | `/decision` | `{ decision: "approved" \| "changesRequested", name, note? }` -> `201`. Append-only; at most 20 per link. A `changesRequested` decision needs a `note` or at least one guest comment. |
Rendering rule: every guest string is plain text, stored with control characters stripped and rendered by
React as text; mail bodies are plain text; nothing guest-supplied is ever put in a URL, header or log line.

## Notifications
`IUserActivityNotifier` gets `GuestReviewCommentAsync` / `GuestReviewDecisionAsync` (best-effort, swallowed on
failure). Recipients: the link creator plus members who opted into editor activity (existing preference
mechanism, `docs/notifications.md`). Coalescing: the sender writes `last_notified_at` and sends at most one
notification per link per 10 minutes ("3 new notes on Client cut"); the event carries the link LABEL and
timeline name, counts and the decision word, never guest text beyond a 80-character plain excerpt, and a link
to the member review page. In-app: `unreadGuestActivity` on the list response drives a badge on the Review
button and the hub timeline card.

## Web
- Public page `/app/review/g/{token}` (outside the app shell; `proxy.ts` admits exactly `^/review/g/[A-Za-z0-9_-]{43}$`;
  `robots: noindex, nofollow`; `Referrer-Policy: no-referrer`; no auth hooks, no SWR credentials: `fetch` with
  `credentials: 'omit'`). Mobile first. Dark viewer, video with comment markers under the player, a name field
  (remembered in `localStorage`), comment box pinned to the current timecode, replies, "Approve" and "Request
  changes" buttons (44 px), a "Newer edits exist" banner when `stale`, and plain "This link has expired or was
  turned off." for every failure (single message).
- Member side: the review page and the Deliver tab get "Share for review" (create dialog: label, render picker
  defaulting to the newest, expiry, comments on/off; shows the URL once with Copy; the list with status, last
  decision, views, Revoke with confirm); guest comments and the decision banner on the member review page;
  the unread badge.
- nginx: `location ~* ^/api/v1/review-links/` modelled on output links (own log format without the URI, unbuffered,
  Range passthrough, `auth_basic off`, `Authorization` blanked, zone `review_links` 120r/m burst 60 per client
  IP for reads, zone `review_links_write` 10r/m burst 10 keyed only on POST).

## Threat model (what the Security review checks)
Token entropy and hashing; uniform 404 (no oracle); no token in logs/referrers/analytics (site GA must not load
on this page); one-render bound (no path from token to other media); Range abuse and concurrent streams (nginx
limits, no unbounded buffering); stored XSS via names/comments/notes (plain text only, CSP of the page);
comment flooding (caps, rate limits); revocation and expiry race; tenant isolation on owner routes (`Tenant`
filter, 404); deletion cascades (timeline trash purge, project/org/account deletion remove links, decisions and
guest comments); notification content injection; moderation gate; guest names impersonating members (label
"(guest)"); mail amplification (coalescing, creator + opted-in members only, no guest-controlled recipient).

## Out of scope (listed, not built)
Passcode on a link, per-guest identity, @mentions, email invitations from the app, live (non-render) preview,
guests seeing other renders, guest edits/deletes, download of the render by the guest (the page streams only).
