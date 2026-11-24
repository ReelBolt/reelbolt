# Anthropic (Claude) inference provider

ReelBolt can run any agent on Claude by registering a provider of kind `Anthropic` (one per
account) with a model and a `Chat` or `Vision` capability under it. This document covers how that provider is wired, and — importantly — **which
credential you are allowed to put in it**, which is not the same answer for local development and
for a deployed ReelBolt.

## What this is, and what it deliberately is not

This is **Claude as a model backend**, not the [Claude Agent SDK](https://code.claude.com/docs/en/agent-sdk/overview).

ReelBolt already has an agent harness: `ReelBoltAgentBase` + `AgentStepExecutor` +
`ToolGroupCatalog` + the room infrastructure supply the agent loop, structured output,
retry-with-feedback, tool scoping, step caching, and a container sandbox. The Agent SDK is a
*competing* harness — Claude Code packaged as a library, with its own built-in Read/Write/Edit/Bash
tools that duplicate the sandbox tools under different security properties. Adopting it would mean
running two harnesses side by side and taking a hard dependency on one vendor's agent loop.

There is also no official .NET Agent SDK (it ships for TypeScript and Python only), so using it at
all would require a Node sidecar container alongside `sandbox-executor`.

The decision was to keep ReelBolt's own harness and treat Claude purely as another `IChatClient`
behind the existing provider abstraction. The entire integration is therefore one arm of
`ChatClientFactory.Build` — every one of the agents is already provider-agnostic and needed no
change.

## Credentials: what is permitted

Anthropic draws a hard line between subscription credentials and API credentials. From
[Claude Code's legal & compliance page](https://code.claude.com/docs/en/legal-and-compliance):

> **OAuth authentication** is intended exclusively for purchasers of Claude Free, Pro, Max, Team,
> and Enterprise subscription plans and is designed to support ordinary use of Claude Code and
> other native Anthropic applications.
>
> **Developers** building products or services that interact with Claude's capabilities, including
> those using the Agent SDK, should use API key authentication through Claude Console or a
> supported cloud provider. Anthropic does not permit third-party developers to offer Claude.ai
> login into their own applications, or to route requests through Free, Pro, or Max plan
> credentials on behalf of their users.

And, on limits:

> Advertised usage limits for Pro and Max plans assume **ordinary, individual usage** of Claude Code
> and the Agent SDK.

Applied to ReelBolt:

| Scenario | Credential | Permitted |
|---|---|---|
| A developer running ReelBolt locally against their own account | Personal subscription (`sk-ant-oat…`) or a personal API key | **Yes** — this is individual use by the credential's owner |
| A developer's own unattended CI hammering a personal subscription | Subscription OAuth token | **Mechanically works, but strains "ordinary, individual usage"** — prefer an API key |
| A deployed ReelBolt serving other people's workflow executions | **API key** (Claude Console) or Bedrock / Vertex / Foundry | Subscription credentials are **not** permitted here — that is intermediating them on users' behalf |

The practical rule: a subscription credential is fine while you are the only person whose work it
runs. The moment ReelBolt executes workflows for anyone else, that row needs a Console API key.

If you go the subscription route, `claude setup-token` mints a one-year token that grants model
requests only.

## Configuring a provider

Admin → Inference Providers → New, or `POST /api/v1/inference-providers`:

| Field | Value |
|---|---|
| Kind | `Anthropic` |
| Capability | `Chat` or `Vision` (**not** `Transcription` — see below) |
| Base URL | `https://api.anthropic.com` (prefilled, and required — override only for a gateway, which must be a public address: `IsDisallowedEndpoint` rejects loopback and RFC1918, so an in-cluster gateway will not pass) |
| Model | e.g. `claude-opus-5`, `claude-sonnet-5`, `claude-haiku-4-5` |
| API key | See the three modes below |
| Timeout | Defaults to 300s |
| Dev/OAuth (Personal use only) | Off by default; see below — turn on only alongside an OAuth-shaped credential |

The form creates the provider with its first model. Add further Claude models on the same key with
**Add model** on the provider's page (`POST /api/v1/inference-providers/{id}/models`), not with a
second provider. Set a capability `IsDefault` to route every agent through it, or point a single agent
at it with `PUT /api/v1/agents/{id}/inference-provider` — that override works for built-in agents,
which makes it the cheapest way to try Claude on one step without moving the whole pipeline.

### The three credential modes

`ChatClientFactory.BuildAnthropic` branches on the stored key:

1. **The literal `env:`** — neither `ApiKey` nor `AuthToken` is set on the client, so the Anthropic
   SDK falls back to its own resolution: `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, or an
   `ant auth login` profile. This is the local-development path: export the variable, store `env:`
   as the key, and **no secret is ever persisted in the database**.
2. **Starts with `sk-ant-oat`** — sent as `Authorization: Bearer`. This is the OAuth/subscription
   shape (`oat` = OAuth token, as minted by `claude setup-token`). Read the permitted-use table
   above before using it.
3. **Anything else** — sent as `x-api-key`. The normal Console API key path, and the only one
   suitable for a deployed ReelBolt.

**An empty key is an error, not mode 1.** `AnthropicClient.ShouldAutoResolveCredentials` is
get-only and defaults to `true`, so leaving both properties null makes the SDK quietly use whatever
credential the container's environment carries. Three different faults produce an empty key — a
blank field, a whitespace-only paste, and a Data Protection key-ring mismatch (the resolver
deliberately degrades a failed decrypt to an empty string rather than throwing) — and under an
"empty means ambient" rule all three would silently reroute billing to the host's account. Since
*executing* a workflow needs no admin rights, any authenticated user could then spend it. So the
factory throws unless the `env:` sentinel says ambient use was actually intended.

To move a row back to mode 1 from the UI you must set the key field to `env:` explicitly; leaving
the field blank on edit means "leave the stored key unchanged", not "clear it".

Stored keys are encrypted at rest with ASP.NET Core Data Protection (`ISecretProtector`) on the
shared `dpkeys` volume, exactly like every other provider kind, and are never returned in
plaintext.

## "Dev/OAuth (Personal use only)"

Anthropic's Messages API silently validates requests authenticated with an OAuth credential (mode
2 above, or an `env:` row whose ambient credential turns out to be one): for every model except
Haiku, the `system` field's **first block** must be exactly

```
You are Claude Code, Anthropic's official CLI for Claude.
```

as its own separate block — not text merged into a larger one. Get this wrong (omit it, or
concatenate it into the rest of the system prompt) and the API returns an unhelpful HTTP 400
`invalid_request_error` with the message `"Error"`. This requirement is undocumented; reproduced
live in [anthropics/claude-code#40515](https://github.com/anthropics/claude-code/issues/40515),
whose proof section is where the exact wording above and the headers below come from.

Check the **"Dev/OAuth (Personal use only)"** switch (`InferenceProvider.PersonalOAuthMode`,
Anthropic providers only — the API rejects it on any other kind) to make ReelBolt present as that
traffic on every request the provider serves:

- `AnthropicChatOptionsAdapter` prepends the exact phrase above as its own leading system-role
  message on every call (`ChatClientFactory.BuildAnthropic` passes the flag through) — before an
  agent's own instructions when there are any, or as the sole system message when there are none
  (the provider connectivity-test "ping").
- `ChatClientFactory.BuildAnthropic` adds the `anthropic-beta: claude-code-20250219` flag (combined
  with `oauth-2025-04-20` into one comma-separated header when the stored key is also
  `sk-ant-oat`-shaped), plus `user-agent: claude-cli/2.1.85 (external, cli)` and `x-app: cli`.

This is a superset of, not a substitute for, the transport-level OAuth handling in mode 2 above:
that mode decides `Authorization: Bearer` vs. `x-api-key` and always sends the `oauth-2025-04-20`
beta flag for a `sk-ant-oat` key regardless of this switch; this switch is what makes the identity
prompt and the Claude-Code-flavoured headers happen, and is independent of key shape because an
`env:` row's ambient credential (resolved by the SDK itself) is invisible to ReelBolt — the switch
is the only way to declare "this row is personal-OAuth" when the key literal can't say so.

Read the permitted-use table earlier in this document before turning this on: it exists for the
same subscription-credential rows that table already covers, and does not change what is or isn't
allowed to serve other people's workflow executions.

## Capabilities

`Chat` and `Vision` both resolve through `IChatClientFactory`, and Claude is natively multimodal, so
one Anthropic model can carry both a `Chat` and a `Vision` capability.

`Transcription` is **rejected with a 400** at the API boundary, on create, update, and the ad-hoc
test endpoint: Anthropic exposes no speech-to-text API. `TranscriptionClientFactory` throws an
explanatory `NotSupportedException` as a second line of defence for rows written directly to the
database. Keep using the bundled `whisper` service (or any OpenAI-compatible ASR endpoint) for
transcription.

## Sampling parameters are not forwarded; reasoning effort is projected

`ReelBoltAgentBase.BuildChatOptions` builds one `ChatOptions` per agent, in the constructor of a
singleton, and reuses it for every run — while the provider behind it is resolved per run. So the
agent cannot know what it is about to talk to, and the options it produces are shaped for an
OpenAI-style backend in two ways that Claude cannot accept.

**Sampling parameters would hard-fail.** Every agent sets a `Temperature` (0.2–0.8) and some set
`TopP`/`TopK`. The Anthropic SDK's own `[Obsolete]` text is unambiguous that this is not merely
advisory:

> Models released after Claude Opus 4.6 do not support setting temperature. A value of 1.0 will be
> accepted for backwards compatibility, **all other values will be rejected with a 400 error**.

…and likewise any `top_k`, and any `top_p` below 0.99. Forwarding them would make **every** agent
run against a current Claude model fail. `AnthropicChatOptionsAdapter` therefore nulls all three, so
a per-agent temperature has no effect on the Anthropic path.

**Per-agent reasoning effort is provider-neutral.** An agent's `ReasoningEffort`
(`none`/`low`/`medium`/`xhigh` - a vLLM/Qwen chat-template vocabulary) used to reach the wire through
`ChatOptions.RawRepresentationFactory`, which produces an **OpenAI-SDK-typed** `ChatCompletionOptions`
that the Anthropic adapter could only drop - so a per-agent effort never applied on this path. Agents
and room seats now state it as `ChatOptions.Reasoning`, which `Microsoft.Extensions.AI.OpenAI` turns
into exactly the same `reasoning_effort` value as before on the OpenAI-shaped kinds and which the
Anthropic SDK understands natively. `AnthropicChatOptionsAdapter` still drops a
`RawRepresentationFactory` if some caller sets one, and still nulls the sampling parameters; it does
not touch `Reasoning` except for the floor below. See
[workflow-execution-optimization.md](workflow-execution-optimization.md#7-per-agent-reasoning-effort).

Measured by running this adapter over the real SDK against a stub transport (`ReasoningEffortMapperTests`)
- the body is the request the provider would have received:

| `ReasoningOptions.Effort` | Anthropic request body |
|---|---|
| *(unset)* | no `thinking`, no `output_config`: the model thinks at its own default effort, and those tokens count against `max_tokens` (see below) |
| `Low` | `"output_config":{"effort":"low"},"thinking":{"type":"adaptive"}` |
| `None` | **never sent** - floored to `Low` by the adapter (`ReasoningEffortMapper`) |

`None` on the SDK is `"thinking":{"type":"disabled"}`, and a model that always thinks rejects
`thinking.type=disabled` with a 400, which turns "stop thinking so much" into "the agent stops
working". So **on this kind the lowest effort is `low`**, for workflow agents (an agent whose effort is
`none` runs at `low`) and for the platform assistant (`AssistantChatLimits.AnthropicReasoningFloor`, the
original implementation of the same floor). See
[platform-assistant-mcp.md](platform-assistant-mcp.md#how-much-the-assistant-thinks-before-it-answers).

## Max output tokens

Anthropic's Messages API requires `max_tokens` on every request, unlike Chat Completions where it is
optional. No *agent* run sets `ChatOptions.MaxOutputTokens`, so the factory supplies a default of
**16,384** — sized for the largest structured outputs the engine produces (video edit
decisions, motion-graphics plans) while staying clear of the range where a *non-streaming* request
risks an HTTP timeout. Agents run through `AIAgent.RunAsync`, which does not stream. A per-request
`ChatOptions.MaxOutputTokens` still overrides it.

**The platform assistant is the one caller that sets its own ceiling**, and it does so with headroom:
`AssistantChatLimits.MaxOutputTokens` sends the configured answer allowance *plus*
`ReasoningHeadroomTokens` (8,192) — 24,576 by default — precisely because the thinking above shares
this same budget. See
[platform-assistant-mcp.md](platform-assistant-mcp.md#reply-length-and-why-a-truncated-reply-says-so).

**A truncated turn is now visible.** The SDK reports the API's `stop_reason` through
`ChatResponse.FinishReason`, mapping `max_tokens` to `ChatFinishReason.Length`; `ReelBoltAgentBase`
carries that onto `AgentRunResult.FinishReason` (`ChatClientAgent` propagates the LAST chat
response's reason, including through a tool-calling loop) and the assistant endpoint turns it into a
marker on the reply and reports the real reason in its `finish` frame. Before that, the reason was
dropped and the frame always said `stop`, so a reply cut off mid-sentence was indistinguishable from
a finished one. **This is still open on the workflow-engine side**: a truncated *agent step* has no
equivalent signal, which is the residual the room-seat incident below was a symptom of; the adapter's
floor keeps its `MaxTurnTokens` from starving thinking, but nothing yet reports that a step's output
was cut.

## Dependency note

The `Anthropic` package tracks the latest release (12.49.0). Getting there required upgrading the
whole `Microsoft.Agents.AI` family, and the reason is worth keeping:

**The incident.** Anthropic ≥ 12.10 requires `Microsoft.Extensions.AI.Abstractions` ≥ 10.4.0, and
10.4.0 **removed seven public types** (`FunctionApproval{Request,Response}Content`,
`McpServerToolApproval{Request,Response}Content`, `UserInput{Request,Response}Content`,
`IToolReductionStrategy`). `Microsoft.Agents.AI.Workflows` 1.0.0-rc2 was compiled against 10.3.0 and
still referenced them from `Specialized.AIAgentHostExecutor.ConfigureUserInputHandling`. Nothing in
this solution names that executor — but `InProcessExecution.RunStreamingAsync` instantiates it to
host an `AIAgent`, which is exactly what `EditRoom`, `GraphicsRoom` and `ColorGradeRoom` do. Result:

```
System.TypeLoadException : Could not load type 'Microsoft.Extensions.AI.UserInputResponseContent'
  from assembly 'Microsoft.Extensions.AI.Abstractions, Version=10.5.0.0'
```

…in all 13 room tests, with `dotnet build` passing clean. A static review had found those references
and judged them dormant on the grounds that nothing named the executor; the grep was right and the
conclusion was wrong. **Only CI caught it.**

**The rule this leaves behind.** `Microsoft.Agents.AI.*`, `Microsoft.Extensions.AI.*`, `OpenAI`,
`Azure.AI.OpenAI` and `Anthropic` are one version set. Mixing versions compiled against different
`Microsoft.Extensions.AI.Abstractions` releases is invisible to the compiler and fails at run time.
Before bumping any of them, scan the candidate assembly's type and member references against what
the others actually ship — the method is in the csproj comment, and it is what verified the current
set.

**Current set**, all checked that way:

| Package | Version | Note |
|---|---|---|
| `Microsoft.Agents.AI{,.OpenAI,.Workflows}` | 1.22.0 | 119 refs into Microsoft.Extensions.AI, none dangling |
| `Microsoft.Extensions.AI.Abstractions` | 10.10.0 | transitive |
| `OpenAI` | 2.13.0 | exactly what `Microsoft.Extensions.AI.OpenAI` 10.10.0 requires |
| `Azure.AI.OpenAI` | 2.9.0-beta.1 | built against OpenAI 2.9.1; 82 types + 145 members verified present in 2.13.0 |
| `Anthropic` | 12.49.0 | requires Abstractions 10.5.1, unifies up |

`Azure.AI.OpenAI` is the laggard — 2.9.0-beta.1 is the newest published, and it is the most likely
blocker for the next bump of this set.

## No schema migration

`InferenceProviderKind` is persisted as a string (`.HasConversion<string>()` in both DbContexts), so
adding `Anthropic` needed no EF migration — the same reason the `Vision` capability needed none.
