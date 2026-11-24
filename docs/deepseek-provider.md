# DeepSeek inference provider

ReelBolt can run any agent on DeepSeek by registering a provider of kind `DeepSeek` (one per
account) with a model and a `Chat`, `Vision` or `Decision` capability under it. This document covers how that provider is wired,
how to obtain an API key, and supported capabilities.

## What this is, and what it deliberately is not

This is **DeepSeek as a model backend** accessed through DeepSeek's official OpenAI
compatibility layer (`https://api.deepseek.com/` or `https://api.deepseek.com/v1`).

ReelBolt already has an agent harness: `ReelBoltAgentBase` + `AgentStepExecutor` +
`ToolGroupCatalog` + the room infrastructure supply the agent loop, structured output,
retry-with-feedback, tool scoping, step caching, and a container sandbox.

Rather than introducing any third-party or conflicting SDK that could introduce dependencies on
`Microsoft.Extensions.AI.Abstractions`, ReelBolt routes DeepSeek requests through its
verified and unified `OpenAI` client (`client.GetChatClient(model).AsIChatClient()`).

DeepSeek's OpenAI compatibility endpoint natively supports:
- Standard chat completions and streaming
- Vision / multimodal content parts (`deepseek-flash` accepts images)
- Function calling / tool invocation
- Reasoning models (e.g. DeepSeek-R1 / DeepSeek-V3 / deepseek-reasoner / deepseek-flash)

The integration is cleanly contained as one arm of `ChatClientFactory.Build` — all agents remain
completely provider-agnostic.

## How to get an API key

1. Navigate to the [DeepSeek Platform](https://platform.deepseek.com/).
2. Sign in or register an account.
3. Go to **API Keys** and generate a new key (`sk-...`).
4. Copy the generated API key.

## Configuring a provider in ReelBolt

Admin → Inference Providers → New, or `POST /api/v1/inference-providers`:

| Field | Value |
|---|---|
| Kind | `DeepSeek` |
| Capability | `Chat` or `Vision` (**not** `Transcription` — see below) |
| Base URL | `https://api.deepseek.com` (prefilled automatically) |
| Model | e.g. `deepseek-chat`, `deepseek-reasoner`, `deepseek-flash` |
| API key | Paste your API key, or use the `env:` sentinel (see below) |
| Timeout | Defaults to 300s |

The form creates the provider with its first model; add more DeepSeek models on the same key with
**Add model** on the provider's page rather than a second provider. Set a `Chat` capability as
`IsDefault` to route all agents through DeepSeek, or assign it as a
per-agent override with `PUT /api/v1/agents/{id}/inference-provider` to test DeepSeek on a single step.

### The two credential modes

`ChatClientFactory.BuildDeepSeek` branches on the stored key:

1. **The literal `env:`** — ReelBolt reads `DEEPSEEK_API_KEY` from the container's environment variables.
   Export the variable in `.env`, store `env:` as the key in the database, and **no secret is persisted in the database**.
2. **Direct API Key (`sk-...`)** — The API key is stored encrypted at rest using ASP.NET Core Data
   Protection (`ISecretProtector`) on the shared `dpkeys` volume (`/keys`).

**An empty key is an error, not mode 1.** An empty or whitespace-only key throws
`InvalidOperationException` rather than silently falling back to ambient credentials. This prevents
misconfigured rows or failed decrypts from silently routing billing to the host's account.

## Capabilities

* **`Chat`**: Fully supported. Serves agent reasoning and structured completions.
* **`Vision`**: Fully supported. DeepSeek models such as `deepseek-flash` provide vision / multimodal capabilities;
  `VideoAnalyze` shot captioning works out of the box using inline base64 JPEG images.
* **`Transcription`**: **Rejected with HTTP 400** at the API boundary on create, update, and test.
  DeepSeek does not expose an OpenAI-compatible speech-to-text (`/audio/transcriptions`) endpoint.
  `TranscriptionClientFactory` throws an explanatory `NotSupportedException` as defense in depth.
  Continue using the bundled `whisper` service (or any OpenAI-compatible ASR provider) for transcription.
* **`WebSearch`**: Supported. Calls the DeepSeek Search API (`POST {endpoint}/v1/tools/search`), priced
  at $0.75 per 1,000 searches (retrieved 2026-10-25; re-check before launch). It is model-independent; the
  Responses API `web_search` tool is not used. The query leaves the host and DeepSeek processes outside the
  EU and the US. See [`web-search.md`](web-search.md).

## Vision calls and the JSON-schema response format

DeepSeek answers a JSON-schema `response_format` with HTTP 400 ("This response_format type is
unavailable now"). ReelBolt's vision callers — `VideoAnalyze` shot captioning and the object locator
that seeds `TrackObjects` — go through `StructuredChatFallback` (WorkflowEngine), which retries a
rejected request with plain JSON mode and then with no format, since every vision prompt already
demands strict JSON and parses defensively. The fallback **remembers** the format a provider
accepted, keyed by the model capability's id plus its model name, for 12 hours per engine process, so a
DeepSeek vision capability pays the rejected first attempt once rather than on every call. A provider that
accepts the schema format leaves nothing remembered and behaves exactly as before; if the remembered
format is later rejected too, the call falls back to the next plainer one and remembers that; and
after 12 hours the schema format is tried again, so a provider that adds support is picked up
without a restart.

## Reasoning Models & Token Headroom

DeepSeek reasoning models (such as `deepseek-reasoner`) generate chain-of-thought tokens before
emitting answer content. Because reasoning tokens count against max output tokens, the provider
connectivity ping (`POST /api/v1/inference-providers/test`) allocates **1,024 tokens** for `DeepSeek`
(compared to 128 tokens for non-thinking endpoints), ensuring test pings do not get truncated mid-thought.

## Reasoning effort and turning thinking off

DeepSeek's thinking mode is **on by default**, so an agent that should not think has to turn it off
explicitly - `reasoning_effort` cannot do it. From DeepSeek's thinking-mode guide
(<https://api-docs.deepseek.com/guides/thinking_mode>):

* thinking is switched with the request-body field `{"thinking": {"type": "enabled/disabled"}}` (OpenAI
  format; `extra_body` in the Python SDK), enabled by default at effort `high`;
* `reasoning_effort` accepts `low`, `high` and `max`, and maps minimal to low, low to low, medium to
  high, high to high, **xhigh to high**, max to max, ultra to max;
* `temperature`, `presence_penalty` and `frequency_penalty` are accepted but have no effect in thinking
  mode; `top_p` below 0.95 is treated as 0.95;
* tool calls work in thinking mode, but `reasoning_content` must be passed back in every later request
  that carries `tools`, or the API answers 400.

The guide does not say what `reasoning_effort: "none"` does and it is not one of the accepted values,
so ReelBolt never sends it to DeepSeek. `ReasoningEffortMapper` (applied by `ReasoningEffortChatClient`
around every DeepSeek client) turns an agent effort of `none` into `{"thinking":{"type":"disabled"}}`
with **no** `reasoning_effort`; `low`, `medium` and `xhigh` are sent as they are and DeepSeek maps them
(`xhigh` and `medium` both mean `high`). The disable field is added through the OpenAI SDK's
`ChatCompletionOptions.Patch` raw-JSON hook, and `ReasoningEffortMapperTests` proves it by inspecting
the serialized request body. The same applies to the platform assistant, whose default effort is `none`.

Not verified here: whether the OpenAI client path round-trips DeepSeek's `reasoning_content` on
tool-calling turns in thinking mode (an effort other than `none`). The shipped agents that call tools
heavily default to `low`, which is thinking mode, so a 400 on a later tool turn would show up there.

## No Schema Migration

`InferenceProviderKind` is persisted as a string (`.HasConversion<string>()` in both DbContexts), so
adding `DeepSeek` requires no EF Core migration.
