using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Client for a self-hosted ComfyUI server (issue #93 phase 5), over plain <see cref="HttpClient"/>
/// + <see cref="System.Text.Json"/> — no vendor SDK, no new NuGet package, exactly like
/// <see cref="MiniMaxVideoGenerationClient"/>.
/// <para>
/// Wire facts and their primary sources (ComfyUI's own server code and docs, fetched 2026-10-04):
/// </para>
/// <list type="bullet">
/// <item><c>POST /prompt</c> with <c>{"prompt": &lt;graph&gt;, "client_id": …, "prompt_id": …}</c>
/// answers <c>{"prompt_id", "number", "node_errors"}</c>, or 400 with <c>{"error": {…}, "node_errors"}</c>
/// (server.py; script_examples/websockets_api_example.py). A caller-chosen <c>prompt_id</c> must be
/// a UUID.</item>
/// <item><c>GET /history/{prompt_id}</c> answers <c>{}</c> while the prompt is queued/running and
/// <c>{ "&lt;id&gt;": { "outputs": { "&lt;node&gt;": { "images"|"videos"|"gifs": [{filename, subfolder, type}] } }, "status": { "status_str", "completed", "messages" } } }</c>
/// once finished (server.py; the example script's <c>['outputs'][node_id]['images']</c> traversal).
/// <c>SaveVideo</c> reports its file through the same <c>{filename, subfolder, type}</c> shape
/// (comfy_extras/nodes_video.py, <c>ui.SavedResult</c>).</item>
/// <item><c>GET /view?filename=…&amp;subfolder=…&amp;type=output</c> streams a saved file; it rejects
/// paths starting with <c>/</c> or containing <c>..</c> (server.py).</item>
/// <item><c>POST /upload/image</c> is multipart with fields <c>image</c>, <c>overwrite</c>, <c>type</c>,
/// <c>subfolder</c> and answers <c>{"name", "subfolder", "type"}</c> (server.py).</item>
/// <item><c>GET /system_stats</c> answers <c>{"system": {…}, "devices": […]}</c>;
/// <c>GET /object_info/{class}</c> describes one node class (docs.comfy.org comms_routes; server.py).</item>
/// </list>
/// <para>
/// <b>Vetted graphs only.</b> The graph that reaches <c>POST /prompt</c> is always
/// <see cref="ComfyUiWorkflowTemplates.Build"/>'s output for an enum-validated template; the
/// request's <c>Model</c> is parsed with <see cref="ComfyUiWorkflowTemplates.TryParse"/> and
/// anything else is rejected before any network call. There is no code path that takes a graph,
/// a node, a class name or a template file from a caller.
/// </para>
/// <para>
/// <b>No cost, GPU time.</b> <see cref="Describe"/> returns GPU-SECONDS per output second rather
/// than USD; the executor budgets it in GPU-seconds through the shared gate. <see cref="ProbeAsync"/>
/// never queues a prompt: it reads <c>/system_stats</c> and then confirms every node class the
/// templates need exists via <c>/object_info/{class}</c>.
/// </para>
/// <para>
/// <b>Self-hosted means private.</b> The endpoint is whatever the admin registered — a docker
/// service name, a LAN or tailnet address — so, unlike the public vendors, the download URL is
/// not run through <see cref="GeneratedClipUrlGuard"/>'s public-host rule. It is instead required
/// to be a URL THIS client built: same scheme, host and port as the configured endpoint and the
/// <c>/view</c> path, which is exactly what <see cref="PollAsync"/> produces.
/// </para>
/// </summary>
public class ComfyUiVideoGenerationClient : IVideoGenerationClient, IVideoImageInputSupport
{
    /// <summary>Default bound on <see cref="ProbeAsync"/> so an unreachable server cannot hang an admin request.</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The <c>client_id</c> every submission carries. Constant: ReelBolt never opens the websocket it would address.</summary>
    public const string ClientId = "reelbolt-workflow-engine";

    /// <summary>The input-image subfolder uploads land in, so a server shared with a human operator keeps its own inputs apart.</summary>
    public const string UploadSubfolder = "reelbolt";

    /// <summary>Hard ceiling on one downloaded clip — same figure as MiniMax's.</summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

    private const int MaxVendorMessageChars = 160;

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly TimeSpan _probeTimeout;
    private readonly Func<long> _seedSource;

    /// <param name="httpClient">A client whose <see cref="HttpClient.BaseAddress"/> is the server root (e.g. <c>http://comfyui:8188</c>).</param>
    /// <param name="apiKey">Optional bearer token for a reverse proxy in front of the server; blank sends no Authorization header. Never read from the environment.</param>
    /// <param name="probeTimeout">Bound on the probe; defaults to <see cref="DefaultProbeTimeout"/>.</param>
    /// <param name="seedSource">Seed generator seam for tests; defaults to a random non-negative 32-bit value.</param>
    public ComfyUiVideoGenerationClient(HttpClient httpClient, string apiKey, TimeSpan? probeTimeout = null, Func<long>? seedSource = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.BaseAddress is null)
        {
            throw new ArgumentException("A ComfyUI client needs the server root as BaseAddress; a self-hosted server has no vendor default.", nameof(httpClient));
        }

        _apiKey = (apiKey ?? string.Empty).Trim();
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        _seedSource = seedSource ?? (() => Random.Shared.Next(0, int.MaxValue));
    }

    public async Task<ExternalJobHandle> SubmitAsync(VideoGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // No reference mode is declared for this client (IVideoReferenceInputSupport): a person's reference material is
        // refused rather than silently dropped, so a step asked to show someone never shows nobody.
        VideoReferenceInput.EnsureNoReferences(request, nameof(request));

        // The ONLY way a graph is chosen: an exact template name. A path, a URL, JSON or any other
        // string is refused here, before anything leaves the process.
        if (!ComfyUiWorkflowTemplates.TryParse(request.Model, out ComfyUiWorkflowTemplate template))
        {
            throw new NotSupportedException(
                $"'{request.Model}' is not a vetted ComfyUI workflow template. Only {string.Join(", ", ComfyUiWorkflowTemplates.Names)} may be submitted; " +
                "free-form graphs are never accepted because custom nodes execute arbitrary code.");
        }

        // What the GRAPH can carry decides, and it decides BEFORE the upload: the text-to-video
        // template has no image node to fill, so an image offered to it is a refusal that names that
        // fact rather than a frame silently dropped on the floor; the image-to-video template has one
        // start-image slot and no last-frame node, so a second (last-frame) image is refused with the
        // same explicitness. Nothing is uploaded until both the template and the image count agree.
        VideoImageInput.Validate(
            ComfyUiWorkflowTemplates.ImageInputSupport(template),
            request.Model ?? string.Empty,
            request.Images?.Count ?? 0);

        (int width, int height) = ComfyUiWorkflowTemplates.Dimensions(request.Resolution, request.Ratio);
        int lengthFrames = ComfyUiWorkflowTemplates.LengthFrames(request.DurationSeconds);

        string? startImageName = null;
        if (template == ComfyUiWorkflowTemplate.Wan22Ti2v5bImageToVideo)
        {
            VideoGenerationImageInput? first = request.Images is { Count: > 0 } ? request.Images[0] : null;
            if (first is null)
            {
                throw new ArgumentException("The image-to-video template requires one input image; the request carries none.", nameof(request));
            }

            startImageName = await UploadInputImageAsync(first, ct);
        }

        // A caller-supplied idempotency token becomes the prompt_id when it is a UUID (the server
        // rejects any other shape with invalid_prompt_id); otherwise a fresh UUID. Either way the
        // handle IS the id the server will file the history under.
        string promptId = Guid.TryParse(request.IdempotencyKey, out Guid parsed) ? parsed.ToString("D") : Guid.NewGuid().ToString("D");

        var slots = new ComfyUiWorkflowSlots(
            Prompt: request.PromptText,
            Seed: _seedSource(),
            Width: width,
            Height: height,
            LengthFrames: lengthFrames,
            Fps: ComfyUiWorkflowTemplates.OutputFps,
            FilenamePrefix: promptId.Replace("-", string.Empty),
            StartImageName: startImageName);

        var body = new JsonObject
        {
            ["prompt"] = ComfyUiWorkflowTemplates.Build(template, slots),
            ["client_id"] = ClientId,
            ["prompt_id"] = promptId
        };

        using var httpRequest = CreateRequest(HttpMethod.Post, "prompt");
        httpRequest.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, ct);
        await ThrowOnFailureAsync(response, ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("prompt_id", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            throw new InvalidOperationException("ComfyUI accepted the prompt but the response carries no non-empty 'prompt_id'.");
        }

        return new ExternalJobHandle(idElement.GetString()!);
    }

    public async Task<ExternalJobStatus> PollAsync(ExternalJobHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!Guid.TryParse(handle.RemoteJobId, out Guid promptId))
        {
            throw new ArgumentException("A ComfyUI job handle is the UUID prompt_id the server issued.", nameof(handle));
        }

        using var request = CreateRequest(HttpMethod.Get, $"history/{promptId:D}");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
        await ThrowOnFailureAsync(response, ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // An empty object means "not in history yet": still queued or running. ComfyUI exposes no
        // percent for a single prompt without the websocket, so Running is the honest state.
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty(promptId.ToString("D"), out JsonElement entry)
            || entry.ValueKind != JsonValueKind.Object)
        {
            return new ExternalJobStatus(ExternalJobState.Running, null, null);
        }

        string? statusStr = null;
        bool completed = false;
        if (entry.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.Object)
        {
            if (status.TryGetProperty("status_str", out JsonElement s) && s.ValueKind == JsonValueKind.String)
            {
                statusStr = s.GetString();
            }

            if (status.TryGetProperty("completed", out JsonElement c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                completed = c.GetBoolean();
            }
        }

        if (string.Equals(statusStr, "error", StringComparison.OrdinalIgnoreCase))
        {
            return new ExternalJobStatus(ExternalJobState.Failed, null, Bounded(ReadErrorMessage(entry) ?? "ComfyUI reported an execution error."));
        }

        (string Filename, string Subfolder, string Type)? output = FindVideoOutput(entry);
        if (output is null)
        {
            return completed || string.Equals(statusStr, "success", StringComparison.OrdinalIgnoreCase)
                ? new ExternalJobStatus(ExternalJobState.Failed, null, "ComfyUI finished the prompt but saved no video output.")
                : new ExternalJobStatus(ExternalJobState.Running, null, null);
        }

        return new ExternalJobStatus(ExternalJobState.Succeeded, BuildViewUrl(output.Value), null);
    }

    public async Task<VideoGenerationProbeResult> ProbeAsync(CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_probeTimeout);

        try
        {
            using var statsRequest = CreateRequest(HttpMethod.Get, "system_stats");
            using HttpResponseMessage stats = await _httpClient.SendAsync(statsRequest, timeoutCts.Token);
            int status = (int)stats.StatusCode;
            if (status is 401 or 403)
            {
                return new VideoGenerationProbeResult(false, true, $"Server rejected authentication (HTTP {status}).");
            }

            if (!stats.IsSuccessStatusCode)
            {
                return NotOk($"GET /system_stats answered HTTP {status}; this does not look like a ComfyUI server.");
            }

            string body = await stats.Content.ReadAsStringAsync(timeoutCts.Token);
            string? version;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("system", out JsonElement system)
                    || system.ValueKind != JsonValueKind.Object)
                {
                    return NotOk("GET /system_stats returned JSON without a 'system' object; this does not look like a ComfyUI server.");
                }

                version = system.TryGetProperty("comfyui_version", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch (JsonException)
            {
                return NotOk($"GET /system_stats returned a non-JSON body (HTTP {status}).");
            }

            // Every node class the vetted templates instantiate must exist on THIS server; a
            // missing one would otherwise surface as a failed submission after GPU time was reserved.
            var required = new SortedSet<string>(StringComparer.Ordinal);
            foreach (ComfyUiWorkflowTemplate template in Enum.GetValues<ComfyUiWorkflowTemplate>())
            {
                required.UnionWith(ComfyUiWorkflowTemplates.RequiredNodeClasses(template));
            }

            var missing = new List<string>();
            foreach (string nodeClass in required)
            {
                using var infoRequest = CreateRequest(HttpMethod.Get, $"object_info/{Uri.EscapeDataString(nodeClass)}");
                using HttpResponseMessage info = await _httpClient.SendAsync(infoRequest, timeoutCts.Token);
                if (!info.IsSuccessStatusCode)
                {
                    missing.Add(nodeClass);
                    continue;
                }

                string infoBody = await info.Content.ReadAsStringAsync(timeoutCts.Token);
                try
                {
                    using JsonDocument infoDoc = JsonDocument.Parse(infoBody);
                    if (infoDoc.RootElement.ValueKind != JsonValueKind.Object
                        || !infoDoc.RootElement.TryGetProperty(nodeClass, out JsonElement cls)
                        || cls.ValueKind != JsonValueKind.Object)
                    {
                        missing.Add(nodeClass);
                    }
                }
                catch (JsonException)
                {
                    missing.Add(nodeClass);
                }
            }

            if (missing.Count > 0)
            {
                return NotOk($"ComfyUI{VersionSuffix(version)} is reachable but lacks node class(es) the vetted templates need: {string.Join(", ", missing)}.");
            }

            return new VideoGenerationProbeResult(true, false, $"ComfyUI{VersionSuffix(version)} answered /system_stats and exposes every template node class ({required.Count} checked). No prompt was queued.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return NotOk($"Server did not respond within {_probeTimeout.TotalSeconds:0.#}s.");
        }
        catch (HttpRequestException ex)
        {
            return NotOk($"Network error reaching server: {Bounded(ex.Message)}");
        }
    }

    public async Task<Stream> DownloadAsync(string downloadUrl, CancellationToken ct)
    {
        EnsureOwnViewUrl(downloadUrl);

        using var request = CreateRequest(HttpMethod.Get, downloadUrl);
        HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null
                && !mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unexpected content type '{Bounded(mediaType)}' for a generated clip.");
            }

            long? declared = response.Content.Headers.ContentLength;
            if (declared > MaxDownloadBytes)
            {
                throw new InvalidOperationException($"Generated clip is {declared} bytes, over the {MaxDownloadBytes}-byte limit.");
            }

            Stream inner = await response.Content.ReadAsStreamAsync(ct);
            return new LimitedReadStream(inner, MaxDownloadBytes, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// GPU-seconds per output second (NOT USD — see the class remarks) for a vetted template; an
    /// unknown name throws, never prices at zero.
    /// </summary>
    public VideoGenerationCapabilities Describe(string model) =>
        ComfyUiWorkflowTemplates.Describe(ParseTemplate(model));

    /// <summary>
    /// What the named template's graph carries: the image-to-video template seeds one start image, the
    /// text-to-video graph carries none. Answered offline from the template enum — the same enum
    /// <see cref="Describe"/> prices — so a caller can refuse before the budget gate reserves anything.
    /// </summary>
    public VideoImageInputSupport ImageInputSupport(string model) =>
        ComfyUiWorkflowTemplates.ImageInputSupport(ParseTemplate(model));

    /// <summary>
    /// The one place a model name becomes a template, shared by <see cref="Describe"/> and
    /// <see cref="ImageInputSupport"/> so the two can never disagree about which names exist.
    /// </summary>
    private static ComfyUiWorkflowTemplate ParseTemplate(string? model)
    {
        if (!ComfyUiWorkflowTemplates.TryParse(model, out ComfyUiWorkflowTemplate template))
        {
            throw new NotSupportedException(
                $"'{model}' is not a vetted ComfyUI workflow template (allowed: {string.Join(", ", ComfyUiWorkflowTemplates.Names)}).");
        }

        return template;
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<string> UploadInputImageAsync(VideoGenerationImageInput image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Bytes is not { Length: > 0 })
        {
            throw new ArgumentException("An input image carries no bytes.", nameof(image));
        }

        string extension = image.ContentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/jpeg" or "image/jpg" => ".jpg",
            _ => throw new ArgumentException($"Unsupported input image type '{image.ContentType}'; use image/png, image/jpeg or image/webp.", nameof(image))
        };

        string fileName = $"keyframe-{Guid.NewGuid():N}{extension}";
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(image.Bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        form.Add(fileContent, "image", fileName);
        form.Add(new StringContent("true"), "overwrite");
        form.Add(new StringContent("input"), "type");
        form.Add(new StringContent(UploadSubfolder), "subfolder");

        using var request = CreateRequest(HttpMethod.Post, "upload/image");
        request.Content = form;
        using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
        await ThrowOnFailureAsync(response, ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("name", out JsonElement name)
            || name.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(name.GetString()))
        {
            throw new InvalidOperationException("ComfyUI accepted the image upload but returned no 'name'.");
        }

        string subfolder = doc.RootElement.TryGetProperty("subfolder", out JsonElement sf) && sf.ValueKind == JsonValueKind.String
            ? sf.GetString() ?? string.Empty
            : string.Empty;

        // LoadImage addresses an input as "<subfolder>/<name>" when it sits in a subfolder.
        return string.IsNullOrEmpty(subfolder) ? name.GetString()! : $"{subfolder}/{name.GetString()}";
    }

    private static (string Filename, string Subfolder, string Type)? FindVideoOutput(JsonElement entry)
    {
        if (!entry.TryGetProperty("outputs", out JsonElement outputs) || outputs.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty node in outputs.EnumerateObject())
        {
            if (node.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (string key in new[] { "videos", "gifs", "images" })
            {
                if (!node.Value.TryGetProperty(key, out JsonElement files) || files.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement file in files.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object
                        || !file.TryGetProperty("filename", out JsonElement fn) || fn.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string filename = fn.GetString() ?? string.Empty;
                    string subfolder = file.TryGetProperty("subfolder", out JsonElement sf) && sf.ValueKind == JsonValueKind.String ? sf.GetString() ?? string.Empty : string.Empty;
                    string type = file.TryGetProperty("type", out JsonElement ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() ?? "output" : "output";
                    if (!IsSafeViewComponent(filename) || !IsSafeViewComponent(subfolder) || type is not ("output" or "temp"))
                    {
                        continue;
                    }

                    // Only a clearly video-typed file is a clip. A PNG preview the sampler also
                    // emitted is NOT accepted: delivering it would fail normalization after GPU
                    // time was spent.
                    string ext = Path.GetExtension(filename).ToLowerInvariant();
                    if (ext is ".mp4" or ".webm" or ".mov" or ".mkv")
                    {
                        return (filename, subfolder, type);
                    }
                }
            }
        }

        return null;
    }

    private static bool IsSafeViewComponent(string value) =>
        !value.StartsWith('/') && !value.StartsWith('\\') && !value.Contains("..", StringComparison.Ordinal) && !value.Contains('\0');

    private string BuildViewUrl((string Filename, string Subfolder, string Type) output)
    {
        var builder = new UriBuilder(_httpClient.BaseAddress!)
        {
            Path = "/view",
            Query = $"filename={Uri.EscapeDataString(output.Filename)}&subfolder={Uri.EscapeDataString(output.Subfolder)}&type={Uri.EscapeDataString(output.Type)}"
        };
        return builder.Uri.ToString();
    }

    /// <summary>
    /// Only a URL this client itself built is downloadable: same scheme/host/port as the configured
    /// server and the <c>/view</c> path. A vendor-supplied or model-supplied URL can never reach
    /// the socket through this method.
    /// </summary>
    internal void EnsureOwnViewUrl(string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException("Download URL is not an absolute URL.");
        }

        Uri root = _httpClient.BaseAddress!;
        if (!string.Equals(uri.Scheme, root.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, root.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != root.Port
            || !string.Equals(uri.AbsolutePath, "/view", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Download URL is not this ComfyUI server's own /view endpoint.");
        }
    }

    private static string? ReadErrorMessage(JsonElement entry)
    {
        if (!entry.TryGetProperty("status", out JsonElement status) || status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement message in messages.EnumerateArray())
        {
            // Each message is ["event_name", {…}]; an execution_error carries exception_message.
            if (message.ValueKind == JsonValueKind.Array && message.GetArrayLength() >= 2
                && message[0].ValueKind == JsonValueKind.String
                && string.Equals(message[0].GetString(), "execution_error", StringComparison.Ordinal)
                && message[1].ValueKind == JsonValueKind.Object
                && message[1].TryGetProperty("exception_message", out JsonElement em) && em.ValueKind == JsonValueKind.String)
            {
                return em.GetString();
            }
        }

        return null;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (_apiKey.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    private async Task ThrowOnFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException("Unauthorized: server rejected the token", inner: null, statusCode: response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException("Forbidden: server denied access", inner: null, statusCode: response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // ComfyUI's validation answer names the offending node; keep it, bounded, so an
            // operator can see WHICH template input the server rejected.
            string body = await response.Content.ReadAsStringAsync(ct);
            string detail = body;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out JsonElement error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String)
                {
                    detail = m.GetString() ?? body;
                }
            }
            catch (JsonException)
            {
                // raw body stands
            }

            throw new HttpRequestException($"ComfyUI rejected the request (HTTP 400): {Bounded(detail)}", inner: null, statusCode: response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
    }

    private static VideoGenerationProbeResult NotOk(string reason) => new(false, false, reason);

    private static string VersionSuffix(string? version) => string.IsNullOrWhiteSpace(version) ? string.Empty : $" {version}";

    private string Bounded(string text)
    {
        if (_apiKey.Length > 0)
        {
            text = text.Replace(_apiKey, "***", StringComparison.Ordinal);
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= MaxVendorMessageChars ? text : text[..MaxVendorMessageChars] + "…";
    }
}
