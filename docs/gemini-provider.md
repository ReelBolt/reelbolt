# Google Gemini inference provider

ReelBolt can run any agent on Google Gemini by registering a provider of kind `Gemini` (one per
account) with a model and a `Chat` or `Vision` capability under it. This document covers how that provider is wired,
how to obtain an API key using your Google account, and how subscription and billing models apply to testing and production.

## What this is, and what it deliberately is not

This is **Google Gemini as a model backend** accessed through Google's first-party OpenAI
compatibility layer (`https://generativelanguage.googleapis.com/v1beta/openai/`).

ReelBolt already has an agent harness: `ReelBoltAgentBase` + `AgentStepExecutor` +
`ToolGroupCatalog` + the room infrastructure supply the agent loop, structured output,
retry-with-feedback, tool scoping, step caching, and a container sandbox.

Rather than introducing an external SDK that might pull conflicting versions of
`Microsoft.Extensions.AI.Abstractions` (see the dependency incident documented in
[`docs/anthropic-provider.md`](anthropic-provider.md)), ReelBolt routes Gemini requests through its
already verified and unified `OpenAI` client (`client.GetChatClient(model).AsIChatClient()`).

Gemini's OpenAI compatibility endpoint natively supports:
- Standard chat completions and streaming
- Vision / multimodal content parts (`data:image/jpeg;base64,...` image URLs)
- Function calling / tool invocation
- Automatic mapping of OpenAI's `reasoning_effort` (`low`, `medium`, `high`) to Gemini's `thinking_level` / `thinking_budget`

The integration is cleanly contained as one arm of `ChatClientFactory.Build` — all agents remain
completely provider-agnostic.

## How to get an API key tied to your Google account

### Understanding Google Subscriptions vs. Developer API Keys

Anthropic and Google structure subscriptions and developer access differently:

* **Anthropic:** Offers personal CLI OAuth tokens (`sk-ant-oat...` via `claude setup-token`) tied to
  Claude Pro/Max subscriptions for individual CLI use.
* **Google:** Consumer subscriptions (**Gemini Advanced** or **Google One AI Premium**) are
  designed exclusively for consumer web and mobile interfaces (`gemini.google.com`) and Google
  Workspace integrations (Docs, Gmail, Drive). **Google subscriptions do not include direct API keys
  or API credits for external applications.**

However, Google provides a first-class developer portal with an exceptionally generous **Free Tier**:

| Tier | Cost | Quota / Limits | Best For |
|---|---|---|---|
| **Google AI Studio (Free Tier)** | **Free** (No credit card needed) | Up to 15 RPM (Requests Per Minute) and 1M TPM for Flash models | **Running tests**, local development, evaluation |
| **Google AI Studio (Pay-as-you-go)** | Pay per token (Google Cloud billing) | High throughput, priority tier | Production workflows, high-volume batches |
| **Google Developer Program** | Monthly cloud credits | Covers Gemini API usage on Google Cloud projects | Active developer subscribers with GCP projects |

### Step-by-Step: Obtaining your API Key

1. Navigate to [Google AI Studio](https://aistudio.google.com/app/apikey).
2. Sign in with the Google account that holds your subscription or project.
3. Click **"Create API key"**.
4. You can create a key in a new Google Cloud project or associate it with an existing GCP project.
   - For free test usage, you do not need to attach a credit card.
   - If you want pay-as-you-go limits without rate throttling, link the project to a Google Cloud Billing account.
5. Copy the generated API key (format: `AIzaSy...`).

## Configuring a provider in ReelBolt

Admin → Inference Providers → New, or `POST /api/v1/inference-providers`:

| Field | Value |
|---|---|
| Kind | `Gemini` |
| Capability | `Chat` or `Vision` (**not** `Transcription` — see below) |
| Base URL | `https://generativelanguage.googleapis.com/v1beta/openai/` (prefilled automatically) |
| Model | e.g. `gemini-2.5-flash`, `gemini-2.5-pro`, `gemini-1.5-flash`, `gemini-1.5-pro`, `gemini-3.8-flash` |
| API key | Paste your API key, or use the `env:` sentinel (see below) |
| Timeout | Defaults to 300s |

The form creates the provider with its first model; add more Gemini models on the same key with
**Add model** on the provider's page rather than a second provider. Set a `Chat` capability as
`IsDefault` to route all agents through Gemini, or assign it as a
per-agent override with `PUT /api/v1/agents/{id}/inference-provider` to test Gemini on a single step.

### The two credential modes

`ChatClientFactory.BuildGemini` branches on the stored key:

1. **The literal `env:`** — ReelBolt reads `GEMINI_API_KEY` (or `GOOGLE_API_KEY`) from the
   container's environment variables. This is the local-development path: export the variable in
   `.env`, store `env:` as the key in the database, and **no secret is persisted in the database**.
2. **Direct API Key (`AIza...`)** — The API key is stored encrypted at rest using ASP.NET Core Data
   Protection (`ISecretProtector`) on the shared `dpkeys` volume (`/keys`).

**An empty key is an error, not mode 1.** An empty or whitespace-only key throws
`InvalidOperationException` rather than silently falling back to ambient credentials. This prevents
misconfigured rows or failed decrypts from silently routing billing to the host's account.

## Capabilities

* **`Chat`**: Fully supported. Serves agent reasoning and structured completions.
* **`Vision`**: Fully supported. Gemini is natively multimodal; `VideoAnalyze` shot captioning
  works out of the box using inline base64 JPEG images.
* **`Transcription`**: **Rejected with HTTP 400** at the API boundary on create, update, and test.
  Google Gemini does not expose an OpenAI-compatible speech-to-text (`/audio/transcriptions`)
  endpoint. `TranscriptionClientFactory` throws an explanatory `NotSupportedException` as defense
  in depth. Continue using the bundled `whisper` service (or any OpenAI-compatible ASR provider) for
  transcription.
* **`WebSearch`**: Supported through the **native** API, not the OpenAI-compatibility endpoint:
  `generateContent` with the `google_search` tool. Priced per search query on Gemini 3 and newer ($14 per
  1,000) and per grounded prompt on 2.5 ($35 per 1,000), retrieved 2026-10-25; re-check before launch.
  Google's terms bar caching and forwarding grounded results and require the Search Suggestions to be
  shown, so the agent tool refuses them by default (`WebSearch:AllowRestrictedGrounding` is `false`). Get a
  legal review before enabling it for shared workspaces. See [`web-search.md`](web-search.md).

## Thinking Models & Token Headroom

Gemini models (particularly Gemini 2.5 Flash/Pro and Gemini 3) support reasoning / thinking.
Under the OpenAI compatibility layer, standard `reasoning_effort` parameters are automatically
mapped to Gemini's internal thinking budget or level.

Because thinking models spend output tokens on intermediate reasoning before emitting visible text,
the provider connectivity ping (`POST /api/v1/inference-providers/test`) allocates **1,024 tokens**
for `Gemini` (compared to 128 tokens for non-thinking endpoints), ensuring test pings do not get
truncated mid-thought.

## No Schema Migration

`InferenceProviderKind` is persisted as a string (`.HasConversion<string>()` in both DbContexts), so
adding `Gemini` required no EF Core migration.
