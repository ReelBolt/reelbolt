# Social publishing: developer-app credentials

What an operator must create, configure and get approved at each platform before publishing works. Until a platform's client id and
secret are set, that platform is hidden in the app and its connect route answers `503 provider_not_configured`. Architecture:
[`social-publishing.md`](social-publishing.md). Secret handling: [`secrets.md`](secrets.md).

Every claim about a platform's own rules below was written from the code and from knowledge as of mid-2026. Anything marked
**Verify** must be re-checked against the platform's current developer pages before go-live; none of it was checked live.

## Redirect URI

Register this exact URI on each platform's app, with no trailing slash:

```text
{PublicBaseUrl}/api/v1/social/{slug}/oauth/callback
```

`{PublicBaseUrl}` is `SocialPublishing:PublicBaseUrl`, set from `PUBLIC_BASE_URL` in compose (an absolute http or https origin; unset
makes connect answer 503). `{slug}` is `youtube` or `linkedin` (`SocialPlatforms.Slug`). For example
`https://app.example.com/api/v1/social/youtube/oauth/callback`. Register one URI per environment (staging and production are separate
apps or separate URIs).

## Environment variables

| Variable | Config key | Platform |
|----------|------------|----------|
| `SOCIAL_YOUTUBE_CLIENT_ID`, `SOCIAL_YOUTUBE_CLIENT_SECRET` | `SocialPublishing__YouTube__ClientId` / `ClientSecret` | YouTube |
| `SOCIAL_LINKEDIN_CLIENT_ID`, `SOCIAL_LINKEDIN_CLIENT_SECRET` | `SocialPublishing__LinkedIn__ClientId` / `ClientSecret` | LinkedIn |
| `PUBLIC_BASE_URL` | `SocialPublishing__PublicBaseUrl` | all |

Both values of a pair must be set. The secrets are never logged. Add them to the secret store before the deploy (see `secrets.md`).

## YouTube (YouTube Shorts)

- **App:** a Google Cloud project with the YouTube Data API v3 enabled and an OAuth client of type **Web application**, with the
  redirect URI above.
- **Scopes requested:** `https://www.googleapis.com/auth/youtube.upload` (post; sensitive) and
  `https://www.googleapis.com/auth/youtube.readonly` (read statistics and the channel identity). Offline access is requested for the
  refresh token.
- **Reviews needed:** Google OAuth app verification for the sensitive scope, and the YouTube API Services compliance audit. Until the
  audit passes, videos uploaded by the app are forced **private**; the app says so in the Accounts tab and the Publish dialog.
- **Quota:** default 10,000 units a day per project. An upload costs 1,600 and a statistics read 1, so about 6 uploads a day for all
  customers together (`DailyBudgetYouTube` 6, 400 units kept for reads). Request a quota extension after the audit and then raise
  `SocialPublishing:Engine:DailyBudgetYouTube` and `SocialPublishing:YouTube:QuotaUnitsPerDay`.
- **Verify before go-live:** whether the audit and verification are both required for the scopes above and how long they take; the
  Shorts length limit (the table says 180 s); the current per-upload quota cost; whether private-until-audited still applies. The
  existing Google verification (login) does not cover these scopes.

## LinkedIn

- **App:** a LinkedIn developer app, associated with a company page, with the **Share on LinkedIn** product (scope
  `w_member_social`) and **Sign In with LinkedIn using OpenID Connect** (scopes `openid`, `profile`, used only to read the member id
  and name). Add the redirect URI above under Auth.
- **Scopes requested:** `openid profile w_member_social`.
- **Reviews needed:** none beyond adding the two products for member posting. **Community Management API** approval is needed only to
  post as an organization page or to read analytics; it is **not** built, so LinkedIn posts show "Not available" for counts.
- **Quota:** LinkedIn applies per-app and per-member daily limits that are not published as a fixed number. `DailyBudgetLinkedIn` is
  150 posts a day across all customers as a conservative guard.
- **Token life:** access tokens last about 60 days and refresh tokens are issued only to approved apps. A member whose token lapses
  without a refresh token must reconnect (the account becomes `reconnect_needed`).
- **Verify before go-live:** the Videos and Posts API versions (`SocialPublishing:LinkedIn:ApiVersion`, default `202511`; LinkedIn
  retires versions after about a year); whether Share on LinkedIn is still self-serve; refresh-token availability; the 5 GB and 30
  minute video limits; real daily limits.

## Instagram, TikTok, X (not built)

Recorded so nobody applies before the adapters exist.

| Platform | Credentials and scopes | Approval | Notes (verify) |
|----------|------------------------|----------|----------------|
| Instagram Reels | Meta app with Instagram Graph API content publishing (`instagram_content_publish`, plus insights scope) | Meta app review and business verification | Business or Creator accounts only; Instagram fetches the video from a public URL we would give; about 100 API posts per 24 h per account. |
| TikTok | Content Posting API app, `video.publish` and `video.list` | Production app and Content Posting audit; before it, direct posts are private only | Chunked `FILE_UPLOAD`; the API requires creator-side settings (privacy, comments, duet) and offers an AI-generated flag. |
| X | Developer account with an API key, secret and OAuth 2.0 client | Paid or pay-per-use access | Pricing and rate rules changed several times; confirm the cost per post and per read first. |

## Go-live checklist

1. App created, redirect URI registered per environment, secrets in the store, `PUBLIC_BASE_URL` correct.
2. Reviews and audits above granted (or the private-only limitation accepted and stated).
3. Items marked **Verify** re-checked; `SocialPlatformLimits.LastChecked` and `Version` bumped if a number changed.
4. Terms and Privacy updated and accepted (see [`social-publishing-legal-draft.md`](social-publishing-legal-draft.md)).
5. Connect a test account, publish a private test video, read it back, disconnect, and check the platform shows the grant revoked.
