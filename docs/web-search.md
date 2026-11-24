# Web search for agents

**Status:** shipped (initiative `agent-web-search`). Internal background; the customer-facing page is
[`user-guide/workflows/web-search.md`](user-guide/workflows/web-search.md). Vendor facts were read on
2026-10-25 through a summarising fetch tool and were not cross-checked against raw pages; see
"Known gaps".

## What it is

A first-party agent tool, `WebSearch(query, maxResults?)`, in `ToolGroup.WebSearch`. It runs a live web
search through the provider registered under `InferenceProviderCapability.WebSearch` and returns one
shape whatever the vendor: title, URL, snippet, optional date, and an optional synthesised answer. The
agent sees the results as fenced, untrusted text. Nothing fetches the URLs.

## Providers and how each is called

| Provider | Row | Call |
|---|---|---|
| DeepSeek | `InferenceProviderKind.DeepSeek` | `POST {endpoint}/v1/tools/search`, Bearer key, body carries `query` and `count`. Structured results (`title`, `url`, `snippet`, `summary`). DeepSeek's Responses API `web_search` is not used: undocumented output shape, `deepseek-v4-pro` only. |
| OpenAI | `OpenAICompatible` row whose endpoint host is `api.openai.com` | `POST https://api.openai.com/v1/responses` with the `web_search` tool, `tool_choice: required`, `include: web_search_call.action.sources`, `store: false`. Citations come from `url_citation` annotations and the sources list. Any other `OpenAICompatible` host is refused at the API boundary for this capability. |
| Gemini | `InferenceProviderKind.Gemini` | Native `POST {base}/v1beta/models/{model}:generateContent` with `x-goog-api-key` and `tools: [{"google_search": {}}]`. Never the `/v1beta/openai/` endpoint, which has no search tool for chat models. |

Other provider kinds are refused for the `WebSearch` capability on create and update. A plain search
vendor (Tavily or Brave) is not added: Tavily needs a new `InferenceProviderKind` and an account, and was
recommended in the research only as a fallback; Brave's terms forbid storing results and using them for
model improvement, which clashes with persisting tool output in step results.

## Capability, usage kind and prices

- Capability `InferenceProviderCapability.WebSearch` (appended to the enum).
- Usage kind `UsageKind.WebSearches`: 1 unit is one search query or action.
- Catalog prices are USD per unit in `ModelPriceCatalog`, from vendor list prices **retrieved 2026-10-25;
  re-check before launch**:

| Provider and model rows | Price per 1,000 units |
|---|---|
| DeepSeek `deepseek-search` | $0.75 |
| OpenAI `gpt-4o-mini`, `gpt-4o` | $10 |
| Gemini 3 and newer (`gemini-3.8-flash`) | $14 (per search query) |
| Gemini 2.5 (`gemini-2.5-pro`, `gemini-2.5-flash`) | $35 (per grounded prompt) |

Free quotas the vendors publish (Gemini's monthly pool, DeepSeek's client-bundled free period) are not
modelled: every unit is priced. Model tokens of an OpenAI or Gemini search call are metered under the
normal token kinds at the capability model's price.

## Resolution

`ResolveWebSearchAsync`: explicit provider id, then the default row of the `WebSearch` capability, then
none. There is no legacy fallback. When nothing resolves, the tool returns `WEB_SEARCH_UNAVAILABLE` as a
tool result; it never fails the step.

## The tool contract

- Name `WebSearch`; arguments `query` (1 to 200 characters) and `maxResults` (1 to 8, default 5, clamped
  to `MaxResultsPerCall`).
- A successful answer is a short header saying the block is untrusted third-party text, then the JSON
  between `[WEB SEARCH RESULTS]` and `[END WEB SEARCH RESULTS]`. Lookalike markers inside result text are
  rewritten. Text is sanitised and truncated (title 200, snippet 600, answer 1,200, URL 500 characters,
  6,000 in total).
- Failures are tool results, not exceptions:

| Code | When |
|---|---|
| `WEB_SEARCH_INVALID_QUERY` | Empty query, longer than 200 characters, or it looks like a URL. After NFKC normalisation, removal of format characters (zero-width, bidi, soft hyphen), backslash read as slash and defanged dots (`[.]`) restored, a query is refused for a scheme (`://`, `hxxp`, spaced forms), `www.`, or a dotted host with an alphabetic TLD (2+ letters, any script) followed by `:port`, `/` and any character, or `?`/`=`/`#` and a non-space character. IP-literal hosts are parsed the way a browser does (integer, `0x` hex, octal, short forms such as `127.1`, a trailing dot, bracketed IPv6 with a zone id or IPv4-mapped) and refused when followed by `:port`, `/` or `?`/`=`/`#` plus a character; plain decimals such as `3.5/5` stay allowed. Percent-encoded text is decoded once and re-tested. `node.js?`, `3.5/5`, `4.99/month`, `v1.2/v1.3` and `example.com pricing` are allowed. This is defence in depth, not a guarantee. |
| `WEB_SEARCH_CAP_REACHED` | The per-step or per-run cap is spent. |
| `WEB_SEARCH_UNAVAILABLE` | Disabled, no provider resolved, or the provider reported itself unavailable. |
| `WEB_SEARCH_UNPRICED` | The provider's model has no WebSearch price (the price resolver refused it). |
| `WEB_SEARCH_UNSUPPORTED_ENDPOINT` | Vendor code passed through: the endpoint is not a supported search endpoint. |
| `WEB_SEARCH_RATE_LIMITED`, `WEB_SEARCH_AUTH`, `WEB_SEARCH_BAD_RESPONSE`, `WEB_SEARCH_TIMEOUT` | Vendor codes passed through from the client. |
| `WEB_SEARCH_RESTRICTED_PROVIDER` | The resolved provider is Gemini (restricted terms) and `AllowRestrictedGrounding` is off. Refused before the provider call: nothing is billed and the call does not count toward a cap. |
| `WEB_SEARCH_FAILED` | Any other unexpected failure. |

- Configuration (`WebSearch:` section, `WebSearchToolOptions`):

| Key | Default |
|---|---|
| `WebSearch:Enabled` | `true` |
| `WebSearch:MaxCallsPerStep` | 5 |
| `WebSearch:MaxCallsPerRun` | 20 |
| `WebSearch:MaxResultsPerCall` | 8 |
| `WebSearch:AllowRestrictedGrounding` | `false` |

A cap is reserved before the call, so a failed call still counts.

## Gemini grounding terms

Google's Gemini API additional terms bar caching, analysing and training on grounded results, bar showing
them to anyone but the user who sent the prompt, and require the Search Suggestions to be displayed. The
adapter marks its result `restricted` and keeps the Search Suggestions HTML apart. The tool refuses a Gemini
provider before the call (`WEB_SEARCH_RESTRICTED_PROVIDER`, nothing billed, none of its text reaches the model) while
`AllowRestrictedGrounding` is `false`, which is the default. Do not turn it on for shared workspaces
until a display for the Suggestions exists and the terms have been reviewed. The terms page was read
through a summariser; read it directly before launch.

## Who gets the tool

`ToolGroupCatalog` grants `ToolGroup.WebSearch` to `ScriptwriterAgent`, `DirectorAgent`, `ShotDirector`
and `NarrationWriter` only. Custom agents cannot pick tool groups, so they do not get it. An Agent step
whose agent has the group may be step-cached only if it did not search: a step that made at least one search call is never stored (`StepCachePolicy.IsAdmissible`, also under `CacheMode=Always`), while a step that had the tool and did not search can be cached. Known edge: a result cached from before a provider was enabled can be served until the cache TTL.

## Billing

`ColdStartHoldPlan` adds a term to the credit hold of such a step: `MaxCallsPerStep` default (5) times the
default WebSearch provider's per-unit price. The term is 0 when no provider resolves or when the provider
is the organization's own (not charged). The run settles by the actual `WebSearches` usage events, not by the hold.
- Search tokens (OpenAI and Gemini model tokens, including Gemini thought and tool-use prompt tokens) are billed only where the model has a Chat token price. When the price lookup succeeds and finds none they are informational (a failed lookup still settles the step at its hold): they are recorded but neither charged nor do they make the step settle at its full hold. The WebSearches unit is always charged.
- Units: OpenAI bills per search action (zero if none ran); Gemini 3 and newer (and any model name that is not `gemini-<major>`) bill per search query, 2.5 and older one unit per call that ran any search; DeepSeek bills 1 for a non-empty raw result list (before URL filtering). No search, no charge.

## Safety

- Results are untrusted text: fenced, labelled, sanitised, truncated.
- A query that looks like a URL (a scheme such as `https://` or `hxxp://`, a `www.` host, or a host followed by a path or query string) is refused with `WEB_SEARCH_INVALID_QUERY` before any provider call and does not count toward a cap, so a prompt-injected query cannot make the provider open a page with project data attached. A bare domain (`example.com pricing`) is allowed.
- A web search key of `env:` is honoured only for a platform-managed provider; on an organization's own provider it fails with an Auth error.
- There is no fetch tool. URLs (http or https, no userinfo) are shown for citation and never opened.
  A DeepSeek Fetch tool would need its own SSRF-guarded design.
- Logs carry provider, counts, duration and status. They never carry the query or the results.

## Not included

The platform assistant and the MCP surface do not get a search tool in this release, so the assistant tool
count is unchanged (94 tools, 93 over MCP).

## Known gaps

- The DeepSeek response shape was parsed from the vendor docs; no live call has confirmed it.
- OpenAI and Gemini search tokens are metered through the Chat capability, whose catalog rows can be
  `NoPrice`; those tokens are then unpriced while the per-search fee is charged.
- The per-run cap is counted in process; it does not survive an engine restart mid-run.
- Room and ReviewLoop seats are not held for search; only plain Agent steps get the hold term.
- OpenAI's guide is inconsistent about model names (`gpt-5.5` in text, another name in examples); the
  catalog rows use `gpt-4o-mini` and `gpt-4o`. Re-check before pinning a model.
- Google's Gemini docs label `generateContent` Legacy; no sunset date was seen.
- Tavily and Brave are not added (see "Providers").

For what leaves the host see [`provider-data-handling.md`](provider-data-handling.md), "Web search".
Provider pages: [`deepseek-provider.md`](deepseek-provider.md), [`gemini-provider.md`](gemini-provider.md).
- Nothing fetches the URLs a search returns (results are displayed only), so no SSRF guard applies to this feature.
