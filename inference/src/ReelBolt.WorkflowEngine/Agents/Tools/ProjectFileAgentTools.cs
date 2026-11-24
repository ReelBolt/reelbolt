using ReelBolt.Shared.Text;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

public class ProjectFileAgentTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string SemanticSearchFallbackHint = "Semantic index is not ready. Use list_project_files to identify relevant files and then read_project_file on the most relevant subset.";

    private readonly IProjectFileWorkspace _workspace;
    private readonly IWorkflowExecutionContextAccessor _executionContextAccessor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProjectFileAgentTools> _logger;

    public ProjectFileAgentTools(
        IProjectFileWorkspace workspace,
        IWorkflowExecutionContextAccessor executionContextAccessor,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ProjectFileAgentTools> logger)
    {
        _workspace = workspace;
        _executionContextAccessor = executionContextAccessor;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [Description("List files available for the current workflow project. Use this to identify candidate files first, then read only the smallest strictly necessary subset.")]
    public async Task<string> ListProjectFiles()
    {
        WorkflowExecutionContext context = RequireContext();
        _logger.LogInformation(
            "Tool call list_project_files (ExecutionId={ExecutionId}, ProjectId={ProjectId}, CorrelationId={CorrelationId})",
            context.ExecutionId,
            context.ProjectId,
            context.CorrelationId);
        IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(context.ProjectId, CancellationToken.None);
        _logger.LogInformation(
            "Tool result list_project_files returned {FileCount} file(s) for project {ProjectId}",
            files.Count,
            context.ProjectId);
        return JsonSerializer.Serialize(files);
    }

    /// <summary>
    /// The most characters <see cref="ReadProjectFile"/> returns in one call. A project can hold a
    /// lockfile, a generated bundle or a long design doc, and a tool result is replayed on every
    /// later turn of the agent's loop, so a whole 300 KB file costs its size again on each turn.
    /// 60,000 characters (roughly 15 K tokens) is more than any ordinary source file; past it the
    /// result says so and points at <see cref="ReadProjectFileLines"/>.
    /// </summary>
    public const int MaxReadProjectFileChars = 60_000;

    [Description(
        "Read a project file by file ID, storage key, or original filename. Only read files that are " +
        "strictly necessary for the current task/context; avoid broad or exhaustive reading. For a large " +
        "file prefer GetProjectFileOutline to locate what you need and ReadProjectFileLines to read just " +
        "that range; a file longer than 60,000 characters is cut off with a note saying how to continue.")]
    public async Task<string> ReadProjectFile(
        [Description("File ID, storage key, or original filename")] string fileReference)
    {
        WorkflowExecutionContext context = RequireContext();
        _logger.LogInformation(
            "Tool call read_project_file (ExecutionId={ExecutionId}, ProjectId={ProjectId}, Reference={FileReference})",
            context.ExecutionId,
            context.ProjectId,
            fileReference);

        (string? content, string? error) = await TryReadTextAsync(context.ProjectId, fileReference, "read_project_file");
        if (content is null)
            return error!;

        _logger.LogInformation(
            "Tool result read_project_file returned {ContentChars} chars for reference {FileReference}",
            content.Length,
            fileReference);

        return content.Length <= MaxReadProjectFileChars
            ? content
            : CapWholeFileRead(content);
    }

    /// <summary>
    /// Cuts an over-long file at a line boundary and appends the instruction for reading the rest.
    /// </summary>
    private static string CapWholeFileRead(string content)
    {
        int cut = content.LastIndexOf('\n', MaxReadProjectFileChars - 1);
        if (cut <= 0)
            cut = MaxReadProjectFileChars;

        string head = content[..cut];
        int shownLines = TextLineWindow.SplitLines(head).Length;
        int totalLines = TextLineWindow.SplitLines(content).Length;

        return head +
               $"\n\n[truncated: showing the first {head.Length:N0} of {content.Length:N0} characters " +
               $"(lines 1-{shownLines} of {totalLines}). Call ReadProjectFileLines with startLine={shownLines + 1} " +
               "to continue, or GetProjectFileOutline to find the part you need.]";
    }

    /// <summary>
    /// Reads a project file's text, turning the two ways that routinely fails into an ordinary
    /// tool-result string instead of an exception. Shared by <see cref="ReadProjectFile"/>,
    /// <see cref="ReadProjectFileLines"/>, <see cref="GetProjectFileOutline"/> and
    /// <see cref="EditProjectFile"/>, so every project-file tool refuses a binary file and an
    /// unresolvable reference in the same words.
    /// </summary>
    private async Task<(string? Content, string? Error)> TryReadTextAsync(Guid projectId, string fileReference, string toolName)
    {
        try
        {
            string content = await _workspace.ReadFileAsync(projectId, fileReference, CancellationToken.None);
            return (content, null);
        }
        catch (InvalidOperationException ex)
        {
            // ProjectFileWorkspace.ReadFileAsync deliberately throws rather than decoding a
            // binary file (music/SFX/video candidate, etc.) as UTF-8 text — a real guardrail
            // (see its own doc comment), not a bug. Left uncaught here, that exception propagated
            // all the way out of the agent run and failed the WHOLE step outright — observed live
            // producing a real promo video: VideoStoryEditor's own curiosity about an unrelated
            // file (a music-track candidate it never needed) took down a step that otherwise had
            // everything it needed. Returning the message AS THE TOOL RESULT (the same
            // catch-and-degrade discipline SearchProjectFiles above already applies) lets the
            // agent see the refusal and continue past it instead.
            _logger.LogInformation(
                "Tool result {Tool} for reference {FileReference} refused: {Message}", toolName, fileReference, ex.Message);
            return (null,
                $"Error: file '{fileReference}' is not a readable text file (binary or unsupported content). " +
                "Skip it and continue with other files; call ListProjectFiles to see what the project contains." +
                (fileReference.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? " For a PDF, the text-based part of its content is in that file's agentSummary in ListProjectFiles (the engine cannot open PDFs itself)."
                    : string.Empty));
        }
        catch (KeyNotFoundException ex)
        {
            // Same catch-and-degrade discipline as the binary-file guardrail above, for the other
            // way this tool routinely fails: the reference simply does not resolve to a
            // project_files row. Left uncaught this killed the entire step, and the trigger is not
            // hypothetical — a VideoAnalyze step's bounded view hands the agent
            // meta.artifactStorageKey ("projects/{id}/agentFiles/video-analysis/{execId}/step-N-
            // analysis.json"), which is an S3 object but deliberately NOT a project file, so an
            // agent that follows that key straight into this tool takes the step down with it.
            // Observed live twice: once failing a VideoStoryEditor step outright, and once as the
            // stated reason a SoundDesigner step gave up ("step-1-analysis.json not found in
            // project scope").
            //
            // The message tells the agent what to do instead, because the analysis view it was
            // already given in its prompt IS the authoritative, bounded form of that artifact —
            // without that hint an agent tends to retry the same unreadable key or, worse,
            // conclude it cannot do the task at all.
            _logger.LogInformation(
                "Tool result {Tool} for reference {FileReference} not found: {Message}", toolName, fileReference, ex.Message);
            return (null,
                $"Error: file '{fileReference}' was not found in this project. " +
                "Note: analysis artifacts (agentFiles/video-analysis/...) and step outputs are not project files " +
                "and cannot be read with this tool. The analysis view already included in your prompt is the " +
                "authoritative, bounded form of that data — use it directly rather than trying to open the artifact. " +
                "Call ListProjectFiles to see what this project actually contains.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else (storage NoSuchKey/AmazonS3Exception, UnauthorizedAccessException from the
            // project-scope check, IO/decoding faults) used to escape as an exception, which the
            // function-invoking client flattens to the detail-free "Error: Function failed." so the
            // agent could neither recover nor report why. Log it and hand back an actionable result.
            _logger.LogWarning(
                ex, "Tool {Tool} failed for reference {FileReference}: {ExceptionType}", toolName, fileReference, ex.GetType().Name);
            string reason = ex switch
            {
                UnauthorizedAccessException => "access to it was denied",
                IOException or System.Net.Http.HttpRequestException or TimeoutException => "storage is temporarily unavailable",
                _ => "its content could not be loaded"
            };
            return (null,
                $"Error: file '{fileReference}' could not be read ({reason}). " +
                "Pass the id, storage key or path exactly as returned by ListProjectFiles, skip this file and " +
                "continue with others, and say in your result that it could not be read.");
        }
    }

    // ──────────────────────────────────────────────
    // Targeted reads / edits — the project-file twins of the sandbox tools
    // ──────────────────────────────────────────────

    [Description(
        "Read a slice of lines (1-based) from a project file instead of paying for the whole file. " +
        "Returns the numbered lines plus totalLines/totalChars and `truncated` (true when more lines " +
        "follow). Use GetProjectFileOutline first on a large file to find which lines to request.")]
    public async Task<string> ReadProjectFileLines(
        [Description("File ID, storage key, or original filename")] string fileReference,
        [Description("1-based line number to start from (clamped to >= 1)")] int startLine = 1,
        [Description("Number of lines to return (clamped to 1-2000)")] int lineCount = 400)
    {
        WorkflowExecutionContext context = RequireContext();
        _logger.LogInformation(
            "Tool call read_project_file_lines (ExecutionId={ExecutionId}, ProjectId={ProjectId}, Reference={FileReference}, StartLine={StartLine}, LineCount={LineCount})",
            context.ExecutionId,
            context.ProjectId,
            fileReference,
            startLine,
            lineCount);

        (string? content, string? error) = await TryReadTextAsync(context.ProjectId, fileReference, "read_project_file_lines");
        if (content is null)
            return Failure(error!);

        TextLineSlice slice = TextLineWindow.Read(content, startLine, lineCount);

        _logger.LogInformation(
            "Tool result read_project_file_lines (ProjectId={ProjectId}, Reference={FileReference}, StartLine={StartLine}, EndLine={EndLine}, TotalLines={TotalLines}, Truncated={Truncated})",
            context.ProjectId,
            fileReference,
            slice.StartLine,
            slice.EndLine,
            slice.TotalLines,
            slice.Truncated);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            fileReference,
            startLine = slice.StartLine,
            endLine = slice.EndLine,
            totalLines = slice.TotalLines,
            totalChars = content.Length,
            truncated = slice.Truncated,
            content = slice.Content
        }, JsonOptions);
    }

    [Description(
        "Get a compact outline of a project file to locate code before reading it: for ts/tsx/js/jsx its " +
        "imports and exported components/functions/types/Composition ids with source lines; for markdown " +
        "its heading tree with line numbers; for anything else just its size. A heuristic locator " +
        "(per-line regexes), not a parser. Use it before ReadProjectFileLines on a large file.")]
    public async Task<string> GetProjectFileOutline(
        [Description("File ID, storage key, or original filename")] string fileReference)
    {
        WorkflowExecutionContext context = RequireContext();
        _logger.LogInformation(
            "Tool call get_project_file_outline (ExecutionId={ExecutionId}, ProjectId={ProjectId}, Reference={FileReference})",
            context.ExecutionId,
            context.ProjectId,
            fileReference);

        (string? content, string? error) = await TryReadTextAsync(context.ProjectId, fileReference, "get_project_file_outline");
        if (content is null)
            return Failure(error!);

        return ProjectFileOutline.Build(fileReference, content);
    }

    [Description(
        "Preferred over WriteProjectFile for changing an existing text project file: replace one exact " +
        "snippet with new text without re-sending the whole file. oldText must be copied verbatim from a " +
        "prior read (including indentation) and must be unique in the file unless replaceAll is set. The " +
        "edit is saved as a NEW version of the file under the same path (the original is left untouched), " +
        "so later reads by file name return the edited text. Fails cleanly with an explanatory `error` " +
        "(never throws) if oldText is missing or ambiguous, and then changes nothing.")]
    public async Task<string> EditProjectFile(
        [Description("File ID, storage key, or original filename of the file to edit")] string fileReference,
        [Description("Exact text to find, copied verbatim from a prior read")] string oldText,
        [Description("Replacement text")] string newText,
        [Description("Replace every occurrence instead of requiring oldText to be unique in the file")] bool replaceAll = false)
    {
        WorkflowExecutionContext context = RequireContext();
        _logger.LogInformation(
            "Tool call edit_project_file (ExecutionId={ExecutionId}, ProjectId={ProjectId}, Reference={FileReference}, OldTextChars={OldChars}, NewTextChars={NewChars}, ReplaceAll={ReplaceAll})",
            context.ExecutionId,
            context.ProjectId,
            fileReference,
            oldText?.Length ?? 0,
            newText?.Length ?? 0,
            replaceAll);

        ProjectWorkspaceFile? target = await ResolveFileAsync(context.ProjectId, fileReference);
        if (target is null)
        {
            return Failure(
                $"Project file '{fileReference}' was not found. Call ListProjectFiles to see what this project contains.");
        }

        (string? content, string? error) = await TryReadTextAsync(context.ProjectId, target.Id.ToString(), "edit_project_file");
        if (content is null)
            return Failure(error!);

        // The model's authoring tools get the narrower write set: a script, build or config file is
        // readable (a human edits it through the dashboard) but the agent must not author it. This
        // sits after the read so the existing binary/not-found refusal path above is untouched — a
        // binary still gets its own message, and only a readable-but-unwritable text file lands here.
        if (!ProjectFileTextPolicy.IsWritableText(target.OriginalFileName))
        {
            _logger.LogInformation(
                "Tool result edit_project_file for {FileReference} refused: {OriginalFileName} is not a writable text file",
                fileReference,
                target.OriginalFileName);
            return Failure(NotWritableMessage(target.OriginalFileName));
        }

        // The same all-or-nothing, ordinal, line-ending-tolerant engine EditSandboxFile uses.
        SandboxEditOutcome outcome = SandboxTextEditor.Apply(content, [new SandboxEdit(oldText ?? string.Empty, newText ?? string.Empty, replaceAll)]);
        if (!outcome.Ok)
        {
            _logger.LogInformation(
                "Tool result edit_project_file for {FileReference} rejected: {Error}", fileReference, outcome.Error);
            return Failure(outcome.Error!);
        }

        // A write never mutates the stored object: it creates a new project_files row under the same
        // path in `agentFiles`, exactly what WriteProjectFile does. That keeps a user's upload intact
        // (the edit lands beside it), and ResolveFileAsync's newest-first name lookup is what makes
        // the edited version the one a later read by name returns.
        ProjectWorkspaceFile saved = await _workspace.WriteTextFileAsync(
            context.ProjectId,
            target.OriginalFileName,
            outcome.Content!,
            target.MimeType,
            CancellationToken.None,
            category: "agentFiles",
            originalPath: target.OriginalPath ?? target.OriginalFileName);

        _logger.LogInformation(
            "Tool result edit_project_file wrote file {FileId} ({Path}) replacing {PreviousFileId}, {Replacements} replacement(s)",
            saved.Id,
            saved.OriginalPath ?? saved.OriginalFileName,
            target.Id,
            outcome.Replacements);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            replacements = outcome.Replacements,
            path = saved.OriginalPath ?? saved.OriginalFileName,
            fileId = saved.Id,
            previousFileId = target.Id,
            sizeBytes = saved.SizeBytes,
            note = "Saved as a new version; read it by file name or by the new fileId."
        }, JsonOptions);
    }

    /// <summary>
    /// Finds the file a reference names, with the same precedence <c>ProjectFileWorkspace</c> reads
    /// with: file id, then storage key, then — newest first — original name or path.
    /// </summary>
    private async Task<ProjectWorkspaceFile?> ResolveFileAsync(Guid projectId, string fileReference)
    {
        if (string.IsNullOrWhiteSpace(fileReference))
            return null;

        IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(projectId, CancellationToken.None);

        if (Guid.TryParse(fileReference, out Guid fileId))
        {
            ProjectWorkspaceFile? byId = files.FirstOrDefault(f => f.Id == fileId);
            if (byId is not null)
                return byId;
        }

        ProjectWorkspaceFile? byKey = files.FirstOrDefault(f => f.StorageKey == fileReference);
        if (byKey is not null)
            return byKey;

        return files
            .Where(f => f.OriginalFileName == fileReference || f.OriginalPath == fileReference)
            .OrderByDescending(f => f.UploadedAt)
            .FirstOrDefault();
    }

    private static string Failure(string error) =>
        JsonSerializer.Serialize(new { ok = false, error }, JsonOptions);

    /// <summary>
    /// The refusal for a script, build or config file the model's authoring tools must not write —
    /// the same words from <c>EditProjectFile</c> and <c>WriteProjectFile</c>, so a model sees one
    /// rule. Points at the ordinary way out (pick a different file), matching the other refusals here.
    /// </summary>
    private static string NotWritableMessage(string fileName) =>
        $"Project file '{fileName}' is a script, build or configuration file, which this tool does not author. " +
        "Only ordinary source and text files may be edited or created here. Call ListProjectFiles to choose a different file.";

    /// <summary>
    /// The refusal for a file name that is not a relative path (a traversal, or a name that reduces to
    /// nothing) — the same words the other project-file tools use for the same input.
    /// </summary>
    private const string InvalidPathMessage =
        "invalid path: fileName must be relative and must not contain '..'";

    [Description("Search project files semantically using vector index and return the most relevant file snippets for the current project.")]
    public async Task<string> SearchProjectFiles(
        [Description("Natural language query used for semantic file search")] string query,
        [Description("Maximum number of results to return, defaults to 5")] int limit = 5)
    {
        WorkflowExecutionContext context = RequireContext();
        _logger.LogInformation(
            "Tool call search_project_files (ExecutionId={ExecutionId}, ProjectId={ProjectId}, QueryLength={QueryLength}, Limit={Limit})",
            context.ExecutionId,
            context.ProjectId,
            query?.Length ?? 0,
            limit);
        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonSerializer.Serialize(new
            {
                results = Array.Empty<object>(),
                indexNotReady = false
            });
        }

        try
        {
            using HttpClient client = CreateInferenceApiClient();
            HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/v1/projects/{context.ProjectId}/files/search",
                new { query, limit },
                CancellationToken.None);

            string responseBody = await response.Content.ReadAsStringAsync(CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                return JsonSerializer.Serialize(new
                {
                    results = Array.Empty<object>(),
                    indexNotReady = true,
                    fallbackHint = SemanticSearchFallbackHint,
                    error = $"Inference API semantic search failed ({(int)response.StatusCode}): {responseBody}"
                });
            }

            JsonObject payload = ParseObjectResponse(responseBody);
            bool indexNotReady = payload["indexNotReady"]?.GetValue<bool>() ?? false;
            if (indexNotReady)
            {
                payload["fallbackHint"] = SemanticSearchFallbackHint;
                payload["fallbackFiles"] = JsonSerializer.SerializeToNode(
                    await BuildDeterministicFallbackCandidatesAsync(context.ProjectId, query, 12));
            }

            _logger.LogInformation(
                "Tool result search_project_files (ProjectId={ProjectId}, IndexNotReady={IndexNotReady})",
                context.ProjectId,
                indexNotReady);

            return payload.ToJsonString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Semantic project file search failed for project {ProjectId}", context.ProjectId);
            IReadOnlyList<object> fallbackFiles = await BuildDeterministicFallbackCandidatesAsync(context.ProjectId, query, 12);
            return JsonSerializer.Serialize(new
            {
                results = Array.Empty<object>(),
                indexNotReady = true,
                fallbackHint = SemanticSearchFallbackHint,
                fallbackFiles,
                error = "Semantic search is unavailable."
            });
        }
    }

    [Description("Get a deterministic ranked list of project files to read when semantic index is unavailable. Prioritizes remotion/composition entry points, TSX/TS source files, styles, and configuration.")]
    public async Task<string> GetDeterministicContextFiles(
        [Description("Optional focus query to bias ranking, e.g. 'composition timeline transitions' or 'brand theme colors'")] string? focusQuery = null,
        [Description("Maximum number of files to return, defaults to 12")] int maxFiles = 12)
    {
        WorkflowExecutionContext context = RequireContext();
        _logger.LogInformation(
            "Tool call get_deterministic_context_files (ExecutionId={ExecutionId}, ProjectId={ProjectId}, FocusQueryLength={FocusQueryLength}, MaxFiles={MaxFiles})",
            context.ExecutionId,
            context.ProjectId,
            focusQuery?.Length ?? 0,
            maxFiles);
        IReadOnlyList<object> candidates = await BuildDeterministicFallbackCandidatesAsync(context.ProjectId, focusQuery, maxFiles);
        _logger.LogInformation(
            "Tool result get_deterministic_context_files returned {CandidateCount} candidate file(s) for project {ProjectId}",
            candidates.Count,
            context.ProjectId);
        return JsonSerializer.Serialize(new
        {
            mode = "deterministic-fallback",
            maxFiles = NormalizeFallbackLimit(maxFiles),
            files = candidates
        });
    }

    [Description(
        "Create or add a new text file to the current workflow project. To change a file that already " +
        "exists, prefer EditProjectFile: it sends only the snippet that changes instead of the whole file.")]
    public async Task<string> WriteProjectFile(
        [Description("The new file name or relative path (e.g. scene-01.tsx or folder/scene-01.tsx)")] string fileName,
        [Description("File contents to store")] string content,
        [Description("MIME type, defaults to text/plain")] string? contentType = null)
    {
        if (string.IsNullOrEmpty(content))
            throw new InvalidOperationException("content is required.");
        string safeContent = content;

        WorkflowExecutionContext context = RequireContext();

        // The gate must inspect the name the writer will actually store. Normalise the path first —
        // refusing a traversal as a Failure rather than letting NormalizeRelativePath's exception
        // escape — then admit by the stored bare name's extension. The MIME type is not consulted, so
        // a caller-supplied text/plain cannot admit a script or build name.
        string normalizedPath;
        try
        {
            normalizedPath = ProjectFilePath.NormalizeRelativePath(fileName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Failure(InvalidPathMessage);
        }

        string storedName = ProjectFilePath.GetFileName(normalizedPath);
        if (!ProjectFileTextPolicy.IsWritableText(storedName))
        {
            _logger.LogInformation(
                "Tool result write_project_file (ExecutionId={ExecutionId}, ProjectId={ProjectId}) refused: {FileName} is not a writable text file",
                context.ExecutionId,
                context.ProjectId,
                fileName);
            return Failure(NotWritableMessage(storedName));
        }

        _logger.LogInformation(
            "Tool call write_project_file (ExecutionId={ExecutionId}, ProjectId={ProjectId}, FileName={FileName}, ContentChars={ContentChars}, ContentType={ContentType})",
            context.ExecutionId,
            context.ProjectId,
            fileName,
            safeContent.Length,
            contentType ?? "text/plain");
        // treat the provided name as the original path and basename (agentFiles category). Pass the
        // gated name as the file name and the normalized path as the original path, so the name gated
        // is byte-for-byte the name stored.
        ProjectWorkspaceFile file = await _workspace.WriteTextFileAsync(
            context.ProjectId,
            storedName,
            safeContent,
            // Issue 8: a model that calls its .ts a transport stream still writes text.
            TransportStreamMimeSniffer.Resolve(
                string.IsNullOrWhiteSpace(contentType) ? "text/plain" : contentType,
                storedName,
                System.Text.Encoding.UTF8.GetBytes(safeContent.Length > ProjectFileTextPolicy.SniffBytes ? safeContent[..ProjectFileTextPolicy.SniffBytes] : safeContent)) ?? "text/plain",
            CancellationToken.None,
            category: "agentFiles",
            originalPath: normalizedPath);

        _logger.LogInformation(
            "Tool result write_project_file wrote file {FileId} ({OriginalFileName}) with storage key {StorageKey}",
            file.Id,
            file.OriginalFileName,
            file.StorageKey);

        return JsonSerializer.Serialize(file);
    }

    private WorkflowExecutionContext RequireContext() =>
        _executionContextAccessor.Current
        ?? throw new InvalidOperationException("No workflow execution context is available for project file tools.");

    private HttpClient CreateInferenceApiClient()
    {
        // Named client: base address + on-behalf-of bearer come from its registration.
        return _httpClientFactory.CreateClient(Services.Inference.InferenceApiClient.Name);
    }

    private static JsonObject ParseObjectResponse(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new JsonObject
            {
                ["results"] = JsonSerializer.SerializeToNode(Array.Empty<object>()),
                ["indexNotReady"] = false
            };
        }

        JsonNode? parsed = JsonNode.Parse(responseBody);
        if (parsed is JsonObject obj)
            return obj;

        return new JsonObject
        {
            ["results"] = JsonSerializer.SerializeToNode(Array.Empty<object>()),
            ["indexNotReady"] = false,
            ["rawResponse"] = parsed
        };
    }

    private async Task<IReadOnlyList<object>> BuildDeterministicFallbackCandidatesAsync(Guid projectId, string? focusQuery, int maxFiles)
    {
        IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(projectId, CancellationToken.None);
        string[] focusTokens = Tokenize(focusQuery);
        int limit = NormalizeFallbackLimit(maxFiles);

        List<object> ranked = files
            .Select(file =>
            {
                string effectivePath = file.OriginalPath ?? file.OriginalFileName;
                int score = CalculateDeterministicScore(file, effectivePath, focusTokens);
                string reason = BuildReason(file, effectivePath, focusTokens);
                return new
                {
                    fileId = file.Id,
                    fileName = file.OriginalFileName,
                    filePath = effectivePath,
                    category = file.Category,
                    mimeType = file.MimeType,
                    score,
                    reason
                };
            })
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.filePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Cast<object>()
            .ToList();

        return ranked;
    }

    private static int NormalizeFallbackLimit(int maxFiles)
        => Math.Clamp(maxFiles <= 0 ? 12 : maxFiles, 1, 30);

    private static int CalculateDeterministicScore(ProjectWorkspaceFile file, string effectivePath, string[] focusTokens)
    {
        int score = 0;
        string path = effectivePath.ToLowerInvariant();
        string fileName = file.OriginalFileName.ToLowerInvariant();
        string ext = Path.GetExtension(fileName).ToLowerInvariant();

        score += file.Category switch
        {
            "userFiles" => 220,
            "agentFiles" => 140,
            "outputFiles" => 10,
            _ => 80
        };

        score += ext switch
        {
            ".tsx" => 320,
            ".ts" => 260,
            ".jsx" => 220,
            ".js" => 180,
            ".css" or ".scss" => 170,
            ".json" => 140,
            ".md" => 80,
            ".razor" or ".cshtml" => 300,
            ".csproj" => 260,
            ".cs" => 200,
            _ => 60
        };

        // .NET / Blazor project anchors (additive; React projects contain none of these names).
        if (fileName is "program.cs" or "app.razor" or "_imports.razor" or "routes.razor" or "mainlayout.razor")
            score += 200;

        if (fileName is "root.tsx" or "index.ts" or "index.tsx")
            score += 260;

        if (path.Contains("remotion", StringComparison.Ordinal) ||
            path.Contains("composition", StringComparison.Ordinal) ||
            path.Contains("timeline", StringComparison.Ordinal) ||
            path.Contains("sequence", StringComparison.Ordinal) ||
            path.Contains("transition", StringComparison.Ordinal))
            score += 220;

        if (path.Contains("src/", StringComparison.Ordinal) ||
            path.Contains("components", StringComparison.Ordinal) ||
            path.Contains("app/", StringComparison.Ordinal) ||
            path.Contains("pages", StringComparison.Ordinal))
            score += 150;

        if (path.Contains("style", StringComparison.Ordinal) ||
            path.Contains("theme", StringComparison.Ordinal) ||
            path.Contains("tailwind", StringComparison.Ordinal) ||
            path.Contains("color", StringComparison.Ordinal) ||
            path.Contains("font", StringComparison.Ordinal))
            score += 120;

        if (fileName is "package.json" or "tsconfig.json")
            score += 150;

        if (file.SizeBytes > 600_000)
            score -= 70;

        if (focusTokens.Length > 0)
        {
            string haystack = (effectivePath + " " + (file.AgentSummary ?? string.Empty)).ToLowerInvariant();
            foreach (string token in focusTokens)
            {
                if (haystack.Contains(token, StringComparison.Ordinal))
                    score += 55;
            }
        }

        return score;
    }

    private static string BuildReason(ProjectWorkspaceFile file, string effectivePath, string[] focusTokens)
    {
        List<string> reasons = [];
        string ext = Path.GetExtension(file.OriginalFileName).ToLowerInvariant();
        string path = effectivePath.ToLowerInvariant();

        if (ext is ".tsx" or ".ts") reasons.Add("source-code");
        if (ext is ".css" or ".scss") reasons.Add("styling");
        if (file.OriginalFileName.Equals("root.tsx", StringComparison.OrdinalIgnoreCase)) reasons.Add("timeline-entrypoint");
        if (path.Contains("remotion", StringComparison.Ordinal) || path.Contains("composition", StringComparison.Ordinal)) reasons.Add("remotion-related");
        if (path.Contains("theme", StringComparison.Ordinal) || path.Contains("style", StringComparison.Ordinal) || path.Contains("tailwind", StringComparison.Ordinal)) reasons.Add("brand-tokens");

        if (focusTokens.Length > 0)
        {
            string haystack = (effectivePath + " " + (file.AgentSummary ?? string.Empty)).ToLowerInvariant();
            if (focusTokens.Any(token => haystack.Contains(token, StringComparison.Ordinal)))
                reasons.Add("focus-query-match");
        }

        if (reasons.Count == 0)
            reasons.Add("deterministic-priority");

        return string.Join(", ", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string[] Tokenize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        char[] separators = [' ', '\t', '\r', '\n', ',', '.', ';', ':', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_', '"', '\''];
        return query
            .ToLowerInvariant()
            .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
    }
}
