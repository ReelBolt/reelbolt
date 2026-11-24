using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Platform-admin management of the PLATFORM model catalog (rows with a null organization; docs/design/model-catalog.md
/// §6): provider accounts → models → capabilities → prices, connectivity tests, the consolidation report, plus the
/// member-facing <c>selectable</c> / <c>status</c> reads. Deliberately routed under <c>api/v1/inference-providers</c>
/// rather than <c>api/v1/admin/*</c> — nginx routes <c>/api/v1/admin/*</c> to the Go API, so this controller would 404
/// there. An organization's own (bring-your-own) providers live on <see cref="OrgInferenceProvidersController"/>, which
/// has no price or consolidation routes; the logic both share is <see cref="InferenceProviderAdminService"/>. This class
/// still hosts the static validation rules (kind/capability matrix, endpoint gate, key handling) the service, the
/// assistant's admin tools and tests call.
/// </summary>
[ApiController]
[Route("api/v1/inference-providers")]
[Authorize]
public class InferenceProvidersController : ControllerBase
{
    private static readonly ProviderScope Platform = ProviderScope.Platform;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly InferenceProviderAdminService _service;

    public InferenceProvidersController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IChatClientFactory chatClientFactory,
        ITranscriptionClientFactory transcriptionClientFactory,
        IDecisionClientFactory decisionClientFactory,
        IVideoGenerationClientFactory videoGenerationClientFactory,
        ISpeechSynthesisClientFactory speechSynthesisClientFactory,
        ISecretProtector secretProtector,
        ILogger<InferenceProvidersController> logger,
        IEmbeddingClientFactory? embeddingClientFactory = null,
        IInferenceProviderResolver? providerResolver = null,
        IOrganizationEntitlements? entitlements = null,
        IImageClientFactory? imageClientFactory = null,
        IModelPriceService? prices = null)
    {
        _db = db;
        _currentUser = currentUser;
        InferenceProviderTester tester = new(
            db, secretProtector, chatClientFactory, transcriptionClientFactory, embeddingClientFactory ?? new EmbeddingClientFactory(),
            decisionClientFactory, videoGenerationClientFactory, speechSynthesisClientFactory, logger,
            imageClientFactory);
        _service = new InferenceProviderAdminService(db, secretProtector, tester, providerResolver, entitlements, prices: prices);
    }

    // ------------------------------------------------------------------ member-facing reads

    /// <summary>
    /// The model capabilities the caller's active organization may choose in a step editor: every enabled
    /// platform-managed one plus the org's own (while its plan allows BYO), optionally filtered to one capability.
    /// Open to every member (Billing has no provider access) and deliberately minimal — id, display name, capability,
    /// model code, kind, the provider's name and whether the platform manages it; never an endpoint, key or health detail.
    /// </summary>
    [HttpGet("selectable")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<SelectableProviderResponse>>> Selectable(
        [FromQuery] string? capability, CancellationToken ct)
    {
        if (_currentUser.OrganizationId is not { } orgId || _currentUser.OrgRole is null) return NotFound();
        if (_currentUser.OrgRole == OrganizationRole.Billing) return Forbid();

        InferenceProviderCapability? parsed = null;
        if (!string.IsNullOrWhiteSpace(capability))
        {
            if (!TryParseCapability(capability, out InferenceProviderCapability c) || !Enum.IsDefined(c))
            {
                return BadRequest(new { error = "capability must be one of: " + string.Join(", ", Enum.GetNames<InferenceProviderCapability>()) });
            }
            parsed = c;
        }

        return Ok(await _service.ListSelectableAsync(orgId, parsed, ct));
    }

    /// <summary>
    /// Why a submitted API key cannot be stored, or null when it can. A key travels in an HTTP header,
    /// which carries ASCII only: a key pasted from a shortened display (ending in "…") or containing a
    /// space or line break is refused here with a plain message, instead of failing every later call
    /// with "Request headers must contain only ASCII characters".
    /// </summary>
    internal static string? ApiKeyProblem(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        string key = apiKey.Trim();
        if (key.Contains('\u2026') || key.EndsWith("...", StringComparison.Ordinal))
            return "That API key looks shortened (it ends in an ellipsis). Copy the full key from the provider's dashboard and paste it again.";
        if (key.Any(c => c > 0x7E || c < 0x21))
            return "That API key contains spaces, line breaks or non-ASCII characters, which a key never has. Copy the full key again and paste it without anything around it.";
        return null;
    }

    /// <summary>
    /// Which provider a run would use for <paramref name="capability"/> right now, and whether it is
    /// healthy. NOT admin-only: the voiceover step settings show it to every customer so a broken
    /// voice service is visible before an hour-long run, not after. The body never carries an
    /// endpoint, model, key or raw error — only the display name, the last test's outcome and time,
    /// and a plain-language message. Speech synthesis applies the same health-aware choice the
    /// workflow engine makes (<see cref="ProviderHealthSelector"/>); every other capability reports
    /// its default row.
    /// </summary>
    [HttpGet("status")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<InferenceProviderStatusResponse>> Status(
        [FromQuery] string? capability, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(capability) ||
            !Enum.TryParse(capability, ignoreCase: true, out InferenceProviderCapability parsed) ||
            !Enum.IsDefined(parsed))
        {
            return BadRequest(new { error = "capability must be one of: " + string.Join(", ", Enum.GetNames<InferenceProviderCapability>()) });
        }

        List<ModelCapabilityView> enabled = await _db.ModelCapabilities
            .AsNoTracking()
            .Where(c => c.IsEnabled && c.Model.IsEnabled && c.Model.Provider.IsEnabled && c.Capability == parsed)
            .Select(ModelCapabilityView.Projection)
            .ToListAsync(ct);

        ProviderSelection selection = parsed == InferenceProviderCapability.SpeechSynthesis
            ? ProviderHealthSelector.Select(enabled, parsed, explicitProviderId: null)
            : DefaultOnly(enabled);

        return Ok(BuildStatus(parsed, selection));
    }

    private static ProviderSelection DefaultOnly(IReadOnlyList<ModelCapabilityView> enabled)
    {
        ModelCapabilityView? configuredDefault = enabled.FirstOrDefault(p => p.IsDefault);
        return configuredDefault is null
            ? new ProviderSelection(null, ProviderSelectionReason.None, null)
            : new ProviderSelection(
                configuredDefault,
                configuredDefault.LastTestOk == false ? ProviderSelectionReason.UnhealthyDefault : ProviderSelectionReason.Default,
                configuredDefault);
    }

    internal static InferenceProviderStatusResponse BuildStatus(InferenceProviderCapability capability, ProviderSelection selection)
    {
        string noun = capability switch
        {
            InferenceProviderCapability.SpeechSynthesis => "voice service",
            InferenceProviderCapability.Transcription => "speech-to-text service",
            InferenceProviderCapability.Vision => "image understanding service",
            InferenceProviderCapability.VideoGeneration => "video generation service",
            InferenceProviderCapability.Embedding => "search indexing service",
            InferenceProviderCapability.Decision => "decision service",
            InferenceProviderCapability.ImageGeneration => "image generation service",
            InferenceProviderCapability.ImageEdit => "image editing service",
            InferenceProviderCapability.Segmentation => "subject detection service",
            InferenceProviderCapability.Depth => "depth service",
            InferenceProviderCapability.ImageErase => "object removal service",
            InferenceProviderCapability.ImageFill => "fill-in service",
            InferenceProviderCapability.ImageExpand => "picture expansion service",
            InferenceProviderCapability.ImageBackgroundReplace => "background replacement service",
            InferenceProviderCapability.ImageRelight => "relighting service",
            InferenceProviderCapability.ImageUpscale => "upscaling service",
            InferenceProviderCapability.MusicGeneration => "music generation service",
            InferenceProviderCapability.SoundEffectGeneration => "sound effect service",
            InferenceProviderCapability.AudioIsolation => "audio cleanup service",
            InferenceProviderCapability.VideoUpscale => "video upscaling service",
            InferenceProviderCapability.FrameInterpolation => "frame interpolation service",
            InferenceProviderCapability.VideoOutpaint => "video extension service",
            InferenceProviderCapability.LipSync => "lip-sync service",
            InferenceProviderCapability.ContentModeration => "moderation service",
            InferenceProviderCapability.Rerank => "relevance ranking service",
            InferenceProviderCapability.WebSearch => "web search service",
            _ => "AI service"
        };

        ModelCapabilityView? provider = selection.Provider;
        bool healthy = provider is not null && provider.LastTestOk != false;
        // Chat is the one capability with a deployment-level fallback (the AzureOpenAI:* settings),
        // so "no row" is not "nothing works" there — say so rather than alarm anyone.
        string? message = provider switch
        {
            null when capability == InferenceProviderCapability.Chat =>
                "No AI service is set up in the admin settings, so the deployment's built-in connection is used.",
            null => $"No {noun} is set up yet. Ask your administrator to add one on the AI providers page.",
            _ when selection.UsedFallback && selection.ConfiguredDefault is not null =>
                $"Your default {noun} failed its last connection check, so another {noun} that is working will be used instead.",
            _ when provider.LastTestOk == false =>
                $"The {noun} failed its last connection check, so steps that need it will probably fail. Ask your administrator to re-test it on the AI providers page.",
            _ => null
        };

        return new InferenceProviderStatusResponse(
            capability.ToString(),
            provider?.Name,
            provider?.LastTestOk,
            provider?.LastTestAt,
            Healthy: provider is null ? capability == InferenceProviderCapability.Chat : healthy,
            UsingFallback: selection.UsedFallback && selection.ConfiguredDefault is not null,
            message);
    }



    // ------------------------------------------------------------------ provider accounts

    [HttpGet]
    public async Task<ActionResult<List<ProviderResponse>>> List(CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Ok(await _service.ListAsync(Platform, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProviderResponse>> Get(Guid id, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Map(await _service.GetAsync(Platform, id, ct));
    }

    [HttpPost]
    public async Task<ActionResult<ProviderResponse>> Create([FromBody] CreateProviderRequest request, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        AdminOutcome<ProviderResponse> outcome = await _service.CreateProviderAsync(Platform, request, ct);
        return outcome.Status == AdminStatus.Created
            ? CreatedAtAction(nameof(Get), new { id = outcome.Value!.Id }, outcome.Value)
            : Map(outcome);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProviderResponse>> Update(Guid id, [FromBody] UpdateProviderRequest request, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Map(await _service.UpdateProviderAsync(Platform, id, request, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return NoContentOr(await _service.DeleteProviderAsync(Platform, id, ct));
    }

    /// <summary>
    /// Under a fal.ai provider, adds a model for every picture-editor job that has no fal.ai capability yet, each
    /// becoming its job's default only where there is none. See <see cref="InferenceProviderAdminService.CreateImageToolsAsync"/>.
    /// </summary>
    [HttpPost("{id:guid}/image-tools")]
    public async Task<ActionResult<List<ModelResponse>>> CreateImageTools(Guid id, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Map(await _service.CreateImageToolsAsync(Platform, id, ct));
    }

    [HttpPost("test")]
    public async Task<ActionResult<TestInferenceProviderResponse>> TestUnsaved([FromBody] TestProviderRequest request, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Map(await _service.TestUnsavedAsync(Platform, request, ct));
    }

    /// <summary>Which platform providers share one account and would be merged (docs/design/model-catalog.md §4.2). Read only.</summary>
    [HttpGet("consolidation-report")]
    public async Task<ActionResult<ConsolidationReportResponse>> GetConsolidationReport(
        [FromServices] ModelCatalogUpgrader upgrader, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        ConsolidationReport report = await upgrader.PlanConsolidationAsync(ct);
        return Ok(new ConsolidationReportResponse([.. report.Groups.Select(g =>
            new ConsolidationGroupResponse(g.SurvivorId, g.SurvivorName, [.. g.MergedProviderIds], g.MergedModelCount))]));
    }

    // ------------------------------------------------------------------ models

    [HttpPost("{id:guid}/models")]
    public async Task<ActionResult<ModelResponse>> CreateModel(Guid id, [FromBody] CreateModelRequest request, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Map(await _service.CreateModelAsync(Platform, id, request, ct));
    }

    [HttpPut("{id:guid}/models/{modelId:guid}")]
    public async Task<ActionResult<ModelResponse>> UpdateModel(
        Guid id, Guid modelId, [FromBody] UpdateModelRequest request, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Map(await _service.UpdateModelAsync(Platform, id, modelId, request, ct));
    }

    [HttpDelete("{id:guid}/models/{modelId:guid}")]
    public async Task<IActionResult> DeleteModel(Guid id, Guid modelId, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return NoContentOr(await _service.DeleteModelAsync(Platform, id, modelId, ct));
    }

    // ------------------------------------------------------------------ capabilities

    /// <summary>Adds a capability to a model; a new platform capability is pre-priced from the price book.</summary>
    [HttpPost("{id:guid}/models/{modelId:guid}/capabilities")]
    public async Task<ActionResult<CapabilityResponse>> CreateCapability(
        Guid id, Guid modelId, [FromBody] CreateCapabilityRequest request, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Map(await _service.CreateCapabilityAsync(Platform, id, modelId, request, ct));
    }

    [HttpPut("{id:guid}/models/{modelId:guid}/capabilities/{capId:guid}")]
    public async Task<ActionResult<CapabilityResponse>> UpdateCapability(
        Guid id, Guid modelId, Guid capId, [FromBody] UpdateCapabilityRequest request, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Map(await _service.UpdateCapabilityAsync(Platform, id, modelId, capId, request, ct));
    }

    [HttpDelete("{id:guid}/models/{modelId:guid}/capabilities/{capId:guid}")]
    public async Task<IActionResult> DeleteCapability(Guid id, Guid modelId, Guid capId, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return NoContentOr(await _service.DeleteCapabilityAsync(Platform, id, modelId, capId, ct));
    }

    /// <summary>Tests a saved capability with its provider's stored key and records the result on the capability.</summary>
    [HttpPost("{id:guid}/models/{modelId:guid}/capabilities/{capId:guid}/test")]
    public async Task<ActionResult<TestInferenceProviderResponse>> TestCapability(
        Guid id, Guid modelId, Guid capId, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        AdminOutcome<ProviderTestOutcome> outcome = await _service.TestCapabilityAsync(Platform, id, modelId, capId, ct);
        if (outcome.Status != AdminStatus.Ok) return MapStatus(outcome.Status, outcome.Body);
        return outcome.Value!.Rejection is { } rejection
            ? BadRequest(new { error = rejection })
            : Ok(outcome.Value.Result);
    }

    // ------------------------------------------------------------------ prices (platform only)

    /// <summary>Adds a price row; it closes the open row of the same unit and variant.</summary>
    [HttpPost("{id:guid}/models/{modelId:guid}/capabilities/{capId:guid}/prices")]
    public async Task<ActionResult<PriceResponse>> CreatePrice(
        Guid id, Guid modelId, Guid capId, [FromBody] CreatePriceRequest request, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Map(await _service.CreatePriceAsync(id, modelId, capId, request, ct));
    }

    /// <summary>Ends a price row at <paramref name="at"/> (now when omitted).</summary>
    [HttpPost("{id:guid}/models/{modelId:guid}/capabilities/{capId:guid}/prices/{priceId:guid}/retire")]
    public async Task<ActionResult<PriceResponse>> RetirePrice(
        Guid id, Guid modelId, Guid capId, Guid priceId, [FromQuery] DateTime? at, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        return Map(await _service.RetirePriceAsync(id, modelId, capId, priceId, at, ct));
    }

    private IActionResult NoContentOr(AdminOutcome<object> outcome) =>
        outcome.Status == AdminStatus.Ok ? NoContent() : MapStatus(outcome.Status, outcome.Body);

    private ActionResult<T> Map<T>(AdminOutcome<T> outcome) => outcome.Status switch
    {
        AdminStatus.Ok => Ok(outcome.Value),
        _ => MapStatus(outcome.Status, outcome.Body)
    };

    private ActionResult MapStatus(AdminStatus status, object? body) => OrgAdminGate.MapStatus(this, status, body);

    /// <remarks>
    /// The one place a submitted API key becomes a stored one, so the assistant's provider
    /// create/update tools (T-006) cannot drift from this endpoint's null/empty/replace semantics.
    /// </remarks>
    internal static void ApplyApiKey(InferenceProvider entity, string? apiKey, ISecretProtector secretProtector)
    {
        if (apiKey == null)
        {
            // Omitted/null: leave the stored key untouched.
            return;
        }

        // Whitespace-only counts as clearing, not as storing. Storing it would show the admin a
        // confident `hasApiKey: true` with a last-four, while every consumer trims the value back
        // to empty and behaves as though no key were configured at all.
        if (apiKey.Trim().Length == 0)
        {
            // Explicit empty string: clear the key.
            entity.ApiKeyEncrypted = null;
            entity.ApiKeyLastFour = null;
            return;
        }

        // Trimmed before storage so the stored value matches what consumers actually use, and so
        // a stray pasted space cannot produce a second, redundant client-cache entry.
        apiKey = apiKey.Trim();
        entity.ApiKeyEncrypted = secretProtector.Protect(apiKey);
        entity.ApiKeyLastFour = apiKey.Length <= 4 ? apiKey : apiKey[^4..];
    }

    /// <summary>
    /// Decides whether an admin-supplied provider endpoint must be refused because it does not
    /// point at a public host. A configured provider is a URL the server will call on demand, so
    /// without this an admin (or anyone holding an admin token) could aim it at the metadata
    /// service, another container, or the Docker bridge and use the server as an SSRF relay. An
    /// endpoint that is not an absolute http(s) URL, or that points at a
    /// loopback/private/link-local (or unresolvable) address, is refused.
    /// <para>
    /// <see cref="InferenceProviderKind.OpenJev"/> is the one exception, and only for the address
    /// classification — see the kind-aware overload below, which is what the Create and Update
    /// sites call.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Public rather than private so the address rules below can be unit tested directly: what a
    /// hostname resolves to is not deterministic inside a test, and asserting only through
    /// <c>http://127.0.0.1</c>-style literals would leave the IPv6, CGNAT and IPv4-mapped branches
    /// unexercised.
    /// </remarks>
    public static bool IsDisallowedEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return true;
        }

        // Uri.Host keeps the brackets around an IPv6 literal ("[::1]"), which IPAddress.TryParse
        // does not accept — without stripping them an IPv6 literal would fall through to DNS,
        // fail, and be rejected for the wrong reason (and a public IPv6 literal wrongly with it).
        string host = uri.Host;
        if (host.Length > 1 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return IsDisallowedAddress(literal);
        }

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch
        {
            // A name we cannot resolve is a name we cannot vouch for.
            return true;
        }

        return IsDisallowedAddressSet(addresses);
    }

    /// <summary>
    /// Rejects an endpoint that is not an absolute http(s) URL, or that points at a
    /// loopback/private/link-local (or unresolvable) address — which is what stops an admin from
    /// aiming a provider row at an internal service (SSRF).
    /// <para>
    /// <see cref="InferenceProviderKind.OpenJev"/> is the one exception, and only for the address
    /// classification: OpenJev is a SELF-HOSTED System One backend by construction, so the
    /// addresses it legitimately lives at — <c>127.0.0.1:8791</c> (the compose service's own
    /// bind), a docker service name such as <c>http://decision:8791</c>, or
    /// <c>host.docker.internal</c> — all resolve private, loopback, or not at all from the
    /// WorkflowEngine container. Classifying them was therefore rejecting the only endpoints the
    /// kind exists for. The absolute-http(s)-URL requirement is NOT relaxed: the scheme check runs
    /// first here, before the exemption, and only the address classification is skipped. The
    /// justification for the exception: every write on this controller is <c>[Authorize]</c> and
    /// admin-only, so the operator supplying the endpoint is the same party an SSRF would target —
    /// there is no privilege boundary being crossed. The same exemption now covers
    /// <c>FishAudio</c> and <c>OpenAICompatible</c> (locally hosted vLLM/whisper endpoints); the
    /// cloud-only kinds (AzureOpenAI, Anthropic, Gemini, DeepSeek, MiniMax, TypeSafe) stay blocked.
    /// </para>
    /// <para>
    /// <c>internal</c> rather than <c>private</c> so the assistant's admin tool surface
    /// (<see cref="Agents.Tools.AssistantAdminTools"/>) reuses the exact same endpoint rule instead
    /// of restating it — the REST endpoint and the assistant must not drift on what a valid
    /// provider endpoint is.
    /// </para>
    /// </summary>
    internal static bool IsDisallowedEndpoint(string endpoint, InferenceProviderKind kind)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return true;
        }

        // OpenAICompatible (vLLM, Ollama, whisper servers on a LAN/tailnet) and FishAudio join OpenJev: a local fish-speech server (host.docker.internal / LAN / docker
        // service name) is a first-class target, not just api.fish.audio. Same admin-only rationale.
        // ComfyUI joins them: a self-hosted GPU box (the `comfyui` compose service, a LAN host) IS the
        // normal address for that kind, and it has no vendor host at all.
        if (kind == InferenceProviderKind.OpenJev || kind == InferenceProviderKind.FishAudio ||
            kind == InferenceProviderKind.OpenAICompatible || kind == InferenceProviderKind.ComfyUI)
        {
            // Skipped entirely, including the DNS lookup the delegated method would perform: an
            // unresolvable docker service name is the normal, correct address for this kind, and
            // resolving it here would both reject it and make an admin-only CRUD write depend on
            // the resolver.
            return false;
        }

        return IsDisallowedEndpoint(endpoint);
    }

    /// <summary>
    /// Applies <see cref="IsDisallowedAddress"/> to every address a host resolved to. Every one
    /// has to be public: a name carrying one public and one private record (a split-horizon DNS
    /// entry, or a deliberately poisoned one) must be refused, because which record a given
    /// connect actually uses is not something the caller controls.
    /// </summary>
    public static bool IsDisallowedAddressSet(IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        if (addresses.Count == 0) return true;

        foreach (IPAddress address in addresses)
        {
            if (IsDisallowedAddress(address)) return true;
        }

        return false;
    }

    /// <summary>
    /// True when a single address is not a public internet address. Delegates to
    /// <see cref="GeneratedClipUrlGuard.IsPrivate"/>, the same deny-by-default classifier the connect-time
    /// guard uses, so the save-time rule and the dial-time rule cannot drift apart (loopback, unspecified,
    /// link-local, RFC 1918, CGNAT, 0/8, reserved and documentation ranges, multicast, unique-local, NAT64, 6to4).
    /// </summary>
    public static bool IsDisallowedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // ::ffff:a.b.c.d is an IPv4 address wearing an IPv6 costume; classify it as its IPv4 self.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return GeneratedClipUrlGuard.IsPrivate(address);
    }

    /// <summary>
    /// Rendered from the enum rather than hand-listed, so adding a provider kind cannot leave an
    /// error message quietly advertising a stale set of valid values.
    /// </summary>
    internal static readonly string KnownKinds =
        string.Join(", ", Enum.GetNames<InferenceProviderKind>().Select(n => $"'{n}'"));

    /// <summary>
    /// Rejects kind/capability combinations that cannot ever work, at the boundary where the row
    /// is written rather than hours later inside a workflow step. Today that is:
    /// Anthropic, Gemini, and DeepSeek expose no speech-to-text API, so they can never serve Transcription. Chat and
    /// Vision both resolve through <c>IChatClientFactory</c> and are fine.
    /// TypeSafe cannot serve Chat, Transcription, or Vision.
    /// Anthropic and Gemini cannot serve Decision capability.
    /// MiniMax may serve exactly THREE capabilities — VideoGeneration, ImageGeneration and
    /// ImageEdit — and is rejected for everything else, because one vendor kind carries one API key
    /// and one host and MiniMax publishes exactly those three media endpoints.
    /// ImageGeneration and ImageEdit are POSITIVE ALLOWLISTS over (MiniMax, OpenAICompatible,
    /// BlackForestLabs, FalAi) for the same reason as Embedding: this method's fallthrough is ALLOW, and a
    /// kind-by-kind denylist would silently admit every kind added later.
    /// All other kinds cannot serve VideoGeneration.
    /// OpenJev can only serve Decision (a VideoGeneration OpenJev row is already caught by the
    /// MiniMax rule above, which rejects that capability for every other kind).
    /// FishAudio can only serve SpeechSynthesis and Transcription — Fish Audio publishes a
    /// text-to-speech (<c>POST /v1/tts</c>) and a speech-to-text (<c>POST /v1/asr</c>) endpoint and
    /// nothing else.
    /// </summary>
    internal static bool IsUnsupportedCombination(
        InferenceProviderKind kind,
        InferenceProviderCapability capability,
        string? endpoint,
        out string error)
    {
        if (IsUnsupportedCombination(kind, capability, out error)) return true;

        // WebSearch through an OpenAICompatible row is OpenAI's Responses web_search tool only: any other host
        // (LiteLLM, vLLM, a proxy) has no such tool, so the row's host must be exactly api.openai.com. The
        // kind-level answer above cannot see the endpoint; this is the endpoint-aware half.
        if (capability == InferenceProviderCapability.WebSearch
            && kind == InferenceProviderKind.OpenAICompatible
            && !IsOpenAiWebSearchHost(endpoint))
        {
            error = "WebSearch on an OpenAICompatible provider is supported only for OpenAI itself: " +
                    "the endpoint host must be api.openai.com.";
            return true;
        }

        return false;
    }

    /// <summary>True for an empty endpoint (the OpenAI default) or an https URL on exactly api.openai.com.</summary>
    internal static bool IsOpenAiWebSearchHost(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return true;
        return Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsUnsupportedCombination(
        InferenceProviderKind kind,
        InferenceProviderCapability capability,
        out string error)
    {
        // Compute is served only by the system compute provider (docs/design/model-catalog.md §2.3);
        // no admin path creates, tests or edits a Compute capability.
        if (capability == InferenceProviderCapability.Compute)
        {
            error = "The Compute capability is served only by the platform compute provider and cannot be configured.";
            return true;
        }

        // the direct ElevenLabs route is dormant. The enum member stays (append-only), but no
        // capability is accepted for it, so no row can be created or updated to it.
        if (kind == InferenceProviderKind.ElevenLabs)
        {
            error = "The ElevenLabs provider kind is not offered: music and sound effects are generated through fal only. " +
                    "Add a FalAi provider with the MusicGeneration or SoundEffectGeneration capability instead.";
            return true;
        }

        // LocalRunner (a model on the user's own machine, relayed through their desktop runner) serves exactly Chat, Vision
        // and Embedding: the relay forwards OpenAI chat completions and embeddings and nothing else. An explicit allowlist, because this
        // method's fallthrough is ALLOW and a denylist would admit every capability added later.
        if (kind == InferenceProviderKind.LocalRunner
            && capability is not (InferenceProviderCapability.Chat or InferenceProviderCapability.Vision or InferenceProviderCapability.Embedding))
        {
            error = $"LocalRunner providers can only serve the Chat, Vision and Embedding capabilities; '{kind}' cannot serve '{capability}'. " +
                    "A model running on your own computer is relayed as chat completions and embeddings and nothing else.";
            return true;
        }

        // Music and sound-effect generation: an EXPLICIT allowlist, never a catalogue-wide default, because this
        // method's fallthrough is ALLOW. fal's hosted ElevenLabs/MMAudio endpoints are the one route (decision 7).
        // Hosted voice isolation (AudioIsolation) follows the same rule: FalAi only (the owner allows the ElevenLabs-based
        // isolator ON FAL; the ElevenLabs kind is refused above for every capability).
        if (capability == InferenceProviderCapability.AudioIsolation && kind != InferenceProviderKind.FalAi)
        {
            error = $"Only FalAi providers can serve the {capability} capability; '{kind}' cannot isolate audio.";
            return true;
        }

        // Talking-head lip-sync: FalAi only (the talking-head models are fal queue endpoints of FalAiVideoCatalog);
        // an explicit allowlist because this method's fallthrough is ALLOW.
        if (capability == InferenceProviderCapability.LipSync && kind != InferenceProviderKind.FalAi)
        {
            error = $"Only FalAi providers can serve the {capability} capability; '{kind}' cannot animate a talking head.";
            return true;
        }

        if (AudioModelCatalog.KindOf(capability) is not null && kind != InferenceProviderKind.FalAi)
        {
            error = $"Only FalAi providers can serve the {capability} capability; '{kind}' cannot " +
                    "generate music or sound effects.";
            return true;
        }

        // Hosted video enhancement (VideoUpscale, FrameInterpolation): FalAi only, an explicit allowlist because this
        // method's fallthrough is ALLOW (the fal Topaz/bytedance upscalers and RIFE/FILM interpolators are the one route).
        if (capability is InferenceProviderCapability.VideoUpscale or InferenceProviderCapability.FrameInterpolation
                or InferenceProviderCapability.VideoOutpaint
            && kind != InferenceProviderKind.FalAi)
        {
            error = $"Only FalAi providers can serve the {capability} capability; '{kind}' cannot enhance video.";
            return true;
        }

        // TypeSafe restrictions
        if (kind == InferenceProviderKind.TypeSafe && capability == InferenceProviderCapability.Chat)
        {
            error = "TypeSafe providers cannot serve the Chat capability. Use a provider of kind 'AzureOpenAI', " +
                    "'OpenAICompatible', 'Anthropic', 'Gemini', or 'DeepSeek' for chat.";
            return true;
        }

        if (kind == InferenceProviderKind.TypeSafe && capability == InferenceProviderCapability.Transcription)
        {
            error = "TypeSafe providers cannot serve the Transcription capability. Use a provider of kind 'AzureOpenAI', " +
                    "'OpenAICompatible', or 'FishAudio' for transcription.";
            return true;
        }

        if (kind == InferenceProviderKind.TypeSafe && capability == InferenceProviderCapability.Vision)
        {
            error = "TypeSafe providers cannot serve the Vision capability. Use a provider of kind 'AzureOpenAI', " +
                    "'OpenAICompatible', 'Anthropic', or 'Gemini' for vision.";
            return true;
        }

        // Transcription restrictions
        if (kind == InferenceProviderKind.Anthropic && capability == InferenceProviderCapability.Transcription)
        {
            error = "Anthropic providers cannot serve the Transcription capability — Anthropic " +
                    "exposes no speech-to-text API. Use a provider of kind 'AzureOpenAI', " +
                    "'OpenAICompatible', or 'FishAudio' for transcription.";
            return true;
        }

        if (kind == InferenceProviderKind.Gemini && capability == InferenceProviderCapability.Transcription)
        {
            error = "Google Gemini providers cannot serve the Transcription capability — Google Gemini " +
                    "does not expose an OpenAI-compatible speech-to-text API. Use a provider of kind " +
                    "'AzureOpenAI', 'OpenAICompatible', or 'FishAudio' for transcription.";
            return true;
        }

        if (kind == InferenceProviderKind.DeepSeek && capability == InferenceProviderCapability.Transcription)
        {
            error = "DeepSeek providers cannot serve the Transcription capability — DeepSeek " +
                    "does not expose an OpenAI-compatible speech-to-text API. Use a provider of kind " +
                    "'AzureOpenAI', 'OpenAICompatible', or 'FishAudio' for transcription.";
            return true;
        }

        // Decision restrictions
        if (kind == InferenceProviderKind.Anthropic && capability == InferenceProviderCapability.Decision)
        {
            error = "Anthropic providers cannot serve the Decision capability — Anthropic " +
                    "does not expose a decision/reasoning API. Use a provider of kind 'AzureOpenAI' or " +
                    "'OpenAICompatible' for decisions.";
            return true;
        }

        if (kind == InferenceProviderKind.Gemini && capability == InferenceProviderCapability.Decision)
        {
            error = "Google Gemini providers cannot serve the Decision capability — Google Gemini's " +
                    "OpenAI-compatible endpoint does not accept logprobs or top_logprobs: it rejects them as " +
                    "unknown fields at the request-schema level. Use a provider of kind 'AzureOpenAI' or " +
                    "'OpenAICompatible' for decisions.";
            return true;
        }

        // Embedding restrictions. Positive allowlist (AzureOpenAI, OpenAICompatible, LocalRunner), because the
        // fallthrough of this method is ALLOW and a kind-by-kind denylist would silently admit every
        // kind added later.
        if (capability == InferenceProviderCapability.Embedding &&
            kind != InferenceProviderKind.AzureOpenAI && kind != InferenceProviderKind.OpenAICompatible &&
            kind != InferenceProviderKind.LocalRunner)
        {
            string why = kind switch
            {
                InferenceProviderKind.DeepSeek =>
                    "DeepSeek's API documents only chat models (api-docs.deepseek.com) and has no embeddings endpoint.",
                InferenceProviderKind.Anthropic =>
                    "Anthropic exposes no embeddings API.",
                InferenceProviderKind.Gemini =>
                    "Gemini embeddings are not wired in yet.",
                _ => "this provider kind does not serve embeddings."
            };
            error = $"{kind} providers cannot serve the Embedding capability — {why} " +
                    "Use a provider of kind 'AzureOpenAI' or 'OpenAICompatible' for embeddings.";
            return true;
        }

        // VideoGeneration restrictions. An EXPLICIT allowlist, not a "not a chat kind" test:
        // a negative test would silently admit every chat kind added to the enum later.
        //
        // FalAi joined this list when the image-to-video wave landed: fal hosts video models behind the
        // same queue contract its images already use (FalAiVideoCatalog), so it is the same kind, the
        // same credential and the same client factory arm. The list stays explicit on purpose.
        if (capability == InferenceProviderCapability.VideoGeneration
            && kind is not (InferenceProviderKind.MiniMax or InferenceProviderKind.Higgsfield or InferenceProviderKind.ComfyUI
                or InferenceProviderKind.FalAi))
        {
            error = $"Only MiniMax, Higgsfield, ComfyUI and FalAi providers can serve the VideoGeneration capability. " +
                    $"'{kind}' cannot generate videos.";
            return true;
        }

        // Higgsfield and ComfyUI (issue #93 phases 3 and 5) each serve exactly ONE capability —
        // VideoGeneration — and are rejected for everything else, written as a negative test for the
        // same fallthrough-is-ALLOW reason as the MiniMax rule below.
        if (kind is InferenceProviderKind.Higgsfield or InferenceProviderKind.ComfyUI
            && capability != InferenceProviderCapability.VideoGeneration)
        {
            error = $"{kind} providers can only serve the VideoGeneration capability; '{kind}' cannot serve '{capability}'. " +
                    "Use 'AzureOpenAI', 'OpenAICompatible', 'Anthropic', 'Gemini' or 'DeepSeek' for chat and vision, " +
                    "'AzureOpenAI', 'OpenAICompatible' or 'FishAudio' for transcription, 'FishAudio' or " +
                    "'OpenAICompatible' for speech synthesis, and 'TypeSafe' or 'OpenJev' for decisions.";
            return true;
        }

        // MiniMax's THREE capabilities. Written as one negative test against them rather than as a
        // positive rejection per other capability, for the same reason as OpenJev and FishAudio
        // below: this method's fallthrough is ALLOW, so the positive form would silently admit
        // every capability added to the enum later. Before ImageGeneration/ImageEdit existed this
        // compared against VideoGeneration alone, which made a MiniMax image row IMPOSSIBLE to
        // create — MiniMax publishes image-01 at POST /v1/image_generation on the very same host
        // and the very same Bearer credential (platform.minimax.io/docs/guides/image-generation).
        if (kind == InferenceProviderKind.MiniMax &&
            capability != InferenceProviderCapability.VideoGeneration &&
            capability != InferenceProviderCapability.ImageGeneration &&
            capability != InferenceProviderCapability.ImageEdit)
        {
            error = "MiniMax providers can serve the VideoGeneration, ImageGeneration and ImageEdit " +
                    "capabilities and nothing else — MiniMax publishes /v1/video_generation and " +
                    "/v1/image_generation (model 'image-01') on api.minimax.io. Use 'MiniMax' for those " +
                    "three, 'AzureOpenAI' or 'OpenAICompatible' or 'Anthropic' or 'Gemini' or 'DeepSeek' " +
                    "for chat and vision, 'AzureOpenAI' or 'OpenAICompatible' or 'FishAudio' for " +
                    "transcription, 'AzureOpenAI' or 'OpenAICompatible' for embeddings, 'FishAudio' or " +
                    "'OpenAICompatible' for speech synthesis, and 'TypeSafe' or 'OpenJev' for decisions.";
            return true;
        }

        // Segmentation, Depth and VideoSegmentation are served by an explicit, positive allowlist over
        // FalAi and nothing else. A positive allowlist rather than a denylist, because this method's
        // fallthrough is ALLOW — the trap the Embedding block above records — and because the vendor-side
        // basis is narrow: fal.ai is the one hosted provider whose segmentation and depth endpoints were
        // READ (each endpoint's own OpenAPI schema at
        // fal.ai/api/openapi/queue/openapi.json?endpoint_id={id}), and an operator-hosted open-weight
        // segmenter reached through the runner is not wired yet. Adding a second kind here is a
        // deliberate act with an endpoint schema behind it, never a default.
        //
        // VideoSegmentation is the TEXT-PROMPTED VIDEO endpoint (fal-ai/sam-3-1/video-rle), whose schema
        // was read at fal.ai/models/fal-ai/sam-3-1/video-rle on 2026-10-18. It is not a mode of
        // Segmentation: it takes a video_url, returns a per-frame RLE track and bills per 16 frames of
        // input, so an image-only segmentation row must never be selected for it.
        if ((capability == InferenceProviderCapability.Segmentation
             || capability == InferenceProviderCapability.Depth
             || capability == InferenceProviderCapability.VideoSegmentation)
            && kind != InferenceProviderKind.FalAi)
        {
            error = kind + " providers cannot serve the " + capability + " capability. Use a provider of " +
                    "kind 'FalAi' — fal.ai hosts promptable image segmentation (fal-ai/evf-sam, " +
                    "fal-ai/sam2/image), background removal and matting (fal-ai/birefnet/v2), depth " +
                    "estimation (fal-ai/imageutils/depth, fal-ai/imageutils/marigold-depth) and " +
                    "text-prompted VIDEO segmentation (fal-ai/sam-3-1/video-rle) behind one queue contract.";
            return true;
        }

        // FalAi restrictions. Same "serves exactly these" shape as MiniMax, OpenJev and FishAudio, and
        // it is not optional: Chat, Vision, Transcription and Decision have no per-kind rule of their
        // own because this method falls through to ALLOW, so without this block a FalAi CHAT row would
        // be accepted at the API boundary while the capability table said the kind serves segmentation
        // and depth only. That exact hole was found by
        // PriceCoverageTests.Capability_table_agrees_with_the_admin_boundary_for_every_pair when
        // BlackForestLabs landed; this rule exists so the same hole cannot open twice.
        if (kind == InferenceProviderKind.FalAi &&
            capability != InferenceProviderCapability.Segmentation &&
            capability != InferenceProviderCapability.Depth &&
            capability != InferenceProviderCapability.VideoSegmentation &&
            capability != InferenceProviderCapability.VideoGeneration &&
            AudioModelCatalog.KindOf(capability) is null &&
            capability != InferenceProviderCapability.AudioIsolation &&
            capability != InferenceProviderCapability.VideoUpscale &&
            capability != InferenceProviderCapability.FrameInterpolation &&
            capability != InferenceProviderCapability.VideoOutpaint &&
            capability != InferenceProviderCapability.LipSync &&
            !ImageJobCapabilities.IsImageJob(capability))
        {
            error = "FalAi providers can serve Segmentation, Depth, VideoSegmentation, VideoGeneration, MusicGeneration, " +
                    "SoundEffectGeneration, AudioIsolation, VideoUpscale, FrameInterpolation, VideoOutpaint, LipSync and the image capabilities " +
                    "(ImageGeneration, ImageEdit, ImageErase, ImageFill, ImageExpand, ImageBackgroundReplace, " +
                    "ImageRelight, ImageUpscale) and nothing else - fal.ai's queue hosts segmentation, depth, " +
                    "text-prompted video segmentation, image generation / editing and video generation " +
                    "endpoints (FalAiVideoCatalog, fal-ai/sam-3-1/video-rle), and this " +
                    "platform configures no chat, vision, transcription, decision, embedding or speech endpoint " +
                    "through it. Use 'FalAi' " +
                    "for those, 'AzureOpenAI', 'OpenAICompatible', 'Anthropic', 'Gemini' or " +
                    "'DeepSeek' for chat and vision, 'AzureOpenAI', 'OpenAICompatible' or 'FishAudio' for " +
                    "transcription, 'AzureOpenAI' or 'OpenAICompatible' for embeddings and decisions, " +
                    "'MiniMax', 'Higgsfield' or 'ComfyUI' for video generation, and 'FishAudio' or " +
                    "'OpenAICompatible' for speech " +
                    "synthesis.";
            return true;
        }

        // BlackForestLabs restrictions. Same shape as the MiniMax and OpenJev rules, and added
        // because a test caught the hole rather than a reviewer: BlackForestLabs serves EXACTLY the two
        // image capabilities, and every OTHER capability it does not serve is either covered by its own
        // positive allowlist (Embedding, SpeechSynthesis, VideoGeneration, and the two image
        // capabilities) or — Chat, Vision, Transcription, Decision — has NO rule at all, because this
        // method's fallthrough is ALLOW. Without this block a BlackForestLabs CHAT row was accepted at
        // the API boundary while ProviderCapabilityUsage said the kind serves only images, which is
        // precisely the disagreement
        // PriceCoverageTests.Capability_table_agrees_with_the_admin_boundary_for_every_pair reports.
        if (kind == InferenceProviderKind.BlackForestLabs &&
            capability != InferenceProviderCapability.ImageGeneration &&
            capability != InferenceProviderCapability.ImageEdit &&
            capability != InferenceProviderCapability.ImageErase &&
            capability != InferenceProviderCapability.ImageFill)
        {
            error = "BlackForestLabs providers can serve the ImageGeneration, ImageEdit, ImageErase and ImageFill " +
                    "capabilities and nothing else — Black Forest Labs publishes the FLUX image endpoints " +
                    "(POST https://api.bfl.ai/v1/flux-*, async submit then poll) and no chat, " +
                    "transcription, vision, decision, embedding or speech API. Use 'MiniMax', " +
                    "'OpenAICompatible' or 'BlackForestLabs' for images, 'AzureOpenAI', 'OpenAICompatible', " +
                    "'Anthropic', 'Gemini' or 'DeepSeek' for chat and vision, 'AzureOpenAI', " +
                    "'OpenAICompatible' or 'FishAudio' for transcription, 'AzureOpenAI' or " +
                    "'OpenAICompatible' for embeddings and decisions, 'MiniMax' for video generation, and " +
                    "'FishAudio' or 'OpenAICompatible' for speech synthesis.";
            return true;
        }

        // OpenJev restrictions. Written as one negative test against the single capability the
        // kind DOES serve, not as four positive rejections, because this method's fallthrough is
        // ALLOW: a rule shaped "kind == OpenJev && capability == X" for each other capability the
        // enum happens to have today would silently admit every capability added to it later.
        if (kind == InferenceProviderKind.OpenJev && capability != InferenceProviderCapability.Decision)
        {
            error = "OpenJev providers can only serve the Decision capability — a self-hosted " +
                    "System One endpoint (Open-Jev, Kev, vllm-jev) answers the decision contract " +
                    "and nothing else. Use a provider of kind 'AzureOpenAI', 'OpenAICompatible', " +
                    "'Anthropic', 'Gemini', or 'DeepSeek' for chat and vision, 'AzureOpenAI', " +
                    "'OpenAICompatible', or 'FishAudio' for transcription, 'MiniMax' for video " +
                    "generation, and 'FishAudio' or 'OpenAICompatible' for speech synthesis.";
            return true;
        }

        // FishAudio restrictions. Fish Audio serves TWO capabilities and nothing else: text-to-speech
        // (POST /v1/tts) and speech-to-text (POST /v1/asr). Written as one negative test against those
        // two, not as a positive rejection per other capability, because this method's fallthrough is
        // ALLOW and the positive form would silently admit every capability added to the enum later.
        // The /v1/asr half is verified against the vendor's own OpenAPI document at
        // https://api.fish.audio/openapi.json (path /v1/asr, operation post, summary "Speech to
        // Text") — see docs/voiceover.md "Fish Audio transcription".
        if (kind == InferenceProviderKind.FishAudio &&
            capability != InferenceProviderCapability.SpeechSynthesis &&
            capability != InferenceProviderCapability.Transcription)
        {
            error = "FishAudio providers can only serve the SpeechSynthesis and Transcription " +
                    "capabilities — Fish Audio publishes text-to-speech (POST /v1/tts) and " +
                    "speech-to-text (POST /v1/asr) and nothing else. Use 'FishAudio' for those two, " +
                    "'AzureOpenAI' or 'OpenAICompatible' for chat, vision, embeddings, and their own " +
                    "transcription and speech endpoints, 'Anthropic', 'Gemini', or 'DeepSeek' for chat " +
                    "and vision, 'MiniMax' for video generation, and 'TypeSafe' or 'OpenJev' for decisions.";
            return true;
        }

        // SpeechSynthesis is served by FishAudio (hosted or self-hosted fish-speech) and by
        // OpenAICompatible (the bundled speaches container, or any /v1/audio/speech server — see
        // docs/voiceover.md "A local OpenAI-compatible TTS server"). A positive allowlist, because
        // this method's fallthrough is ALLOW.
        if (capability == InferenceProviderCapability.SpeechSynthesis &&
            kind != InferenceProviderKind.FishAudio && kind != InferenceProviderKind.OpenAICompatible)
        {
            error = kind + " providers cannot serve the SpeechSynthesis capability. Use a provider of kind 'FishAudio' " +
                    "or 'OpenAICompatible' for text-to-speech.";
            return true;
        }

        // ContentModeration is served by OpenAICompatible ONLY. A positive allowlist (not a
        // kind-by-kind denylist), because this method's fallthrough is ALLOW: OpenAI publishes
        // POST /moderations and every compatible gateway speaks that shape, while no other kind
        // has a wired moderation route. A chat row must never be selected for a brief.
        if (capability == InferenceProviderCapability.ContentModeration
            && kind != InferenceProviderKind.OpenAICompatible)
        {
            error = "Only OpenAICompatible providers can serve the ContentModeration capability.";
            return true;
        }

        // Rerank is served by OpenAICompatible ONLY, again a positive allowlist (this method's
        // fallthrough is ALLOW): POST {base}/rerank is the Hugging Face text-embeddings-inference
        // route that hosted rerankers also expose, and no other kind has a wired rerank route.
        if (capability == InferenceProviderCapability.Rerank
            && kind != InferenceProviderKind.OpenAICompatible)
        {
            error = "Only OpenAICompatible providers can serve the Rerank capability.";
            return true;
        }

        // WebSearch is served by an explicit, positive allowlist (this method's fallthrough is ALLOW): DeepSeek
        // (POST /v1/tools/search), Gemini (native generateContent + google_search) and OpenAICompatible - the last
        // only when the endpoint host is api.openai.com, which the endpoint-aware overload above enforces.
        if (capability == InferenceProviderCapability.WebSearch
            && kind != InferenceProviderKind.DeepSeek
            && kind != InferenceProviderKind.Gemini
            && kind != InferenceProviderKind.OpenAICompatible)
        {
            error = kind + " providers cannot serve the WebSearch capability. Use a provider of kind 'DeepSeek', " +
                    "'Gemini', or 'OpenAICompatible' pointing at api.openai.com.";
            return true;
        }

        // ImageGeneration is served by an explicit, positive allowlist: MiniMax (image-01),
        // OpenAICompatible (the OpenAI Images shape, which is also whatever a compatible endpoint
        // speaks), BlackForestLabs (the FLUX endpoints) and FalAi (fal's hosted text-to-image and
        // editing endpoints, FalAiImageClient.Catalog). A positive allowlist rather than a
        // denylist, because this method's fallthrough is ALLOW — the comment on the Embedding block
        // above records the trap, and image generation is exactly the kind of capability a future
        // provider kind would be wrongly assumed to serve.
        //
        // The vendor-side basis for each arm, so a reader can check rather than trust:
        //   MiniMax            POST https://api.minimax.io/v1/image_generation, model image-01
        //                      (platform.minimax.io/docs/api-reference/image-generation-t2i)
        //   OpenAICompatible   POST {base}/images/generations
        //                      (developers.openai.com/api/reference/resources/images)
        //   BlackForestLabs    POST https://api.bfl.ai/v1/flux-* (docs.bfl.ml/quick_start/generating_images)
        //   FalAi              POST https://queue.fal.run/{endpoint id}, one OpenAPI schema per endpoint
        //                      (fal.ai/api/openapi/queue/openapi.json?endpoint_id={id}, docs/fal-ai-models.md)
        if (capability == InferenceProviderCapability.ImageGeneration &&
            kind != InferenceProviderKind.MiniMax &&
            kind != InferenceProviderKind.OpenAICompatible &&
            kind != InferenceProviderKind.BlackForestLabs &&
            kind != InferenceProviderKind.FalAi)
        {
            error = kind + " providers cannot serve the ImageGeneration capability. Use a provider of " +
                    "kind 'MiniMax' (image-01), 'OpenAICompatible' (the OpenAI Images shape), " +
                    "'BlackForestLabs' (FLUX) or 'FalAi' (Ideogram, Nano Banana, FLUX.3, Seedream) to " +
                    "generate images.";
            return true;
        }

        // ImageEdit is the same allowlist. Instruction editing and mask inpainting are one
        // capability with two operations (see InferenceProviderCapability.ImageEdit), and each of
        // these three vendors offers at least one of them; a vendor that offers neither is not on
        // this list, and adding one is a deliberate edit here, not a default.
        if (capability == InferenceProviderCapability.ImageEdit &&
            kind != InferenceProviderKind.MiniMax &&
            kind != InferenceProviderKind.OpenAICompatible &&
            kind != InferenceProviderKind.BlackForestLabs &&
            kind != InferenceProviderKind.FalAi)
        {
            error = kind + " providers cannot serve the ImageEdit capability. Use a provider of " +
                    "kind 'MiniMax', 'OpenAICompatible', 'BlackForestLabs' or 'FalAi' to edit an image.";
            return true;
        }

        // The two MASK jobs split out of ImageEdit. Same positive-allowlist shape: every kind whose image
        // client implements InpaintAsync (the arm both jobs have always gone through) - fal.ai, the
        // OpenAI Images edit route and BFL's fill model. MiniMax documents no mask, so it is not here.
        if ((capability == InferenceProviderCapability.ImageErase || capability == InferenceProviderCapability.ImageFill) &&
            kind != InferenceProviderKind.OpenAICompatible &&
            kind != InferenceProviderKind.BlackForestLabs &&
            kind != InferenceProviderKind.FalAi)
        {
            error = kind + " providers cannot serve the " + capability + " capability: it paints through a mask, " +
                    "and only 'FalAi', 'OpenAICompatible' and 'BlackForestLabs' document a masked edit.";
            return true;
        }

        // Expand, background replacement, relighting and upscaling: fal.ai alone. FalAiImageClient is the
        // only image client that implements TransformAsync (each job an endpoint read from fal's own
        // schema); every other client throws NotSupportedException for it, so admitting another kind
        // here would only move the refusal from the admin page to the moment a user clicks the tool.
        if ((capability is InferenceProviderCapability.ImageExpand
                or InferenceProviderCapability.ImageBackgroundReplace
                or InferenceProviderCapability.ImageRelight
                or InferenceProviderCapability.ImageUpscale) &&
            kind != InferenceProviderKind.FalAi)
        {
            error = kind + " providers cannot serve the " + capability + " capability. Only 'FalAi' is wired " +
                    "for it (see docs/fal-ai-models.md for the endpoints).";
            return true;
        }

        error = string.Empty;
        return false;
    }

    /// <summary>
    /// Rejects a model that cannot serve the row's capability, where the platform can know that before a
    /// request is sent. Today that is fal.ai's image rows: the model IS the endpoint id, every endpoint
    /// was transcribed from its own schema into <c>FalImageCatalog</c>, and each one serves exactly one
    /// editor job — so an <c>ImageUpscale</c> row pointing at a relighting endpoint is refused here rather
    /// than quietly ignored at run time (see <see cref="ImageJobCapabilities.FalModelProblem"/>). Every
    /// other kind and capability passes: their model namespaces are the vendor's, not ours.
    /// </summary>
    internal static bool IsUnsupportedModel(
        InferenceProviderKind kind,
        InferenceProviderCapability capability,
        string? modelName,
        out string error)
    {
        if (kind == InferenceProviderKind.FalAi && ImageJobCapabilities.FalModelProblem(capability, modelName) is { } problem)
        {
            error = problem;
            return true;
        }

        if (kind == InferenceProviderKind.FalAi
            && capability == InferenceProviderCapability.VideoGeneration
            && FalVideoModelProblem(modelName) is { } videoProblem)
        {
            error = videoProblem;
            return true;
        }

        // A lip-sync row must name a talking-head endpoint of the fal video catalog. A blank model is refused too: blank
        // means "the default image-to-video endpoint" to the video client, which is not a talking head.
        if (kind == InferenceProviderKind.FalAi
            && capability == InferenceProviderCapability.LipSync
            && FalAiVideoGenerationClient.Catalog.Find(modelName?.Trim())?.Job != FalVideoJob.TalkingHead)
        {
            error = $"'{modelName?.Trim()}' is not a fal.ai talking-head model this platform has a read schema for. " +
                    "A lip-sync row must name one of: " + FalAiVideoGenerationClient.Catalog.IdsFor(FalVideoJob.TalkingHead) + ".";
            return true;
        }

        // The inverse: a talking-head model belongs under Lip-sync, never under VideoGeneration (the default video
        // provider cannot serve it, so a run would fail at the vendor). Applies to new and edited rows only; a row
        // that already exists keeps working until it is edited.
        if (kind == InferenceProviderKind.FalAi
            && capability == InferenceProviderCapability.VideoGeneration
            && FalAiVideoGenerationClient.Catalog.Find(modelName?.Trim())?.Job == FalVideoJob.TalkingHead)
        {
            error = $"'{modelName?.Trim()}' is a talking-head (lip-sync) model. Add it under the Lip-sync capability, not " +
                    "Video generation.";
            return true;
        }

        // An audio row names a catalogued endpoint (fal) or model_id (ElevenLabs) of ITS job: a music row pointing
        // at a sound-effect endpoint, or at a model with no read schema and price, can never be used. Blank = the
        // job's default.
        if (kind is InferenceProviderKind.FalAi or InferenceProviderKind.ElevenLabs
            && AudioModelCatalog.KindOf(capability) is { } audioJob
            && AudioModelCatalog.Instance.ModelProblem(kind, audioJob, modelName) is { } audioProblem)
        {
            error = audioProblem;
            return true;
        }

        // A hosted-isolation row names a catalogued endpoint (blank = the default); an unlisted one has no read schema
        // and no price, so it can never be used or offered.
        if (kind == InferenceProviderKind.FalAi
            && capability == InferenceProviderCapability.AudioIsolation
            && AudioIsolationModelCatalog.Instance.Resolve(modelName) is null)
        {
            error = $"'{modelName?.Trim()}' is not a fal.ai audio-isolation endpoint this platform has a read schema and a " +
                    $"price for. Known: {AudioIsolationModelCatalog.Instance.Ids}.";
            return true;
        }

        // A hosted video-enhance row names a catalogued endpoint of ITS capability (blank = that capability's default);
        // an unlisted one has no read schema and no price, so it can never be used or offered.
        if (kind == InferenceProviderKind.FalAi
            && capability is InferenceProviderCapability.VideoUpscale or InferenceProviderCapability.FrameInterpolation
                or InferenceProviderCapability.VideoOutpaint
            && VideoEnhanceModelCatalog.Instance.Resolve(capability, modelName) is null)
        {
            error = $"'{modelName?.Trim()}' is not a fal.ai {capability} endpoint this platform has a read schema and a " +
                    $"price for. Known: {VideoEnhanceModelCatalog.Instance.IdsFor(capability)}.";
            return true;
        }

        error = string.Empty;
        return false;
    }

    /// <summary>
    /// A fal.ai video row names an endpoint id the same way its image rows do, and the platform can know
    /// before a request is sent whether that id is one it has read a schema for: a take naming an
    /// unread endpoint is refused by <c>FalAiVideoGenerationClient</c> anyway (fal silently ignores a
    /// field it does not define, so a plausible clip would come back for a request nobody made), and a
    /// row that can never be used should not be created. A BLANK model is allowed: it means the
    /// catalogue's default image-to-video endpoint, which is how the client reads it.
    /// </summary>
    private static string? FalVideoModelProblem(string? modelName)
    {
        string id = modelName?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            return null;
        }

        return FalAiVideoGenerationClient.Catalog.Find(id) is null
            ? "'" + id + "' is not a fal.ai video endpoint this platform has a read schema for, so it would " +
              "refuse to send it a request. Use one of: " + FalAiVideoGenerationClient.Catalog.AllIds + "."
            : null;
    }

    /// <summary>
    /// "Dev/OAuth (Personal use only)" only makes sense for a provider that actually talks to
    /// Anthropic — rejected at the boundary where the row is written, same discipline as
    /// <see cref="IsUnsupportedCombination"/>, rather than letting it sit inertly true on an Azure
    /// OpenAI or OpenAI-compatible row.
    /// </summary>
    internal static bool IsPersonalOAuthModeUnsupported(
        InferenceProviderKind kind,
        bool personalOAuthMode,
        out string error)
    {
        if (personalOAuthMode && kind != InferenceProviderKind.Anthropic)
        {
            error = "PersonalOAuthMode ('Dev/OAuth (Personal use only)') is only supported for " +
                    "providers of kind 'Anthropic'.";
            return true;
        }

        error = string.Empty;
        return false;
    }

    internal static bool TryParseKind(string? value, out InferenceProviderKind kind) =>
        Enum.TryParse(value, ignoreCase: true, out kind);

    internal static bool TryParseCapability(string? value, out InferenceProviderCapability capability) =>
        Enum.TryParse(value, ignoreCase: true, out capability);

}
