using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.S3.Model;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Fonts;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// <c>StageFont</c> (docs/design/sandbox-customization.md 2.1): stage a font into <c>public/fonts/&lt;Family&gt;/</c> from
/// the checked-in Google Fonts catalog (never an agent-supplied URL) or from a project file, and hand back a ready
/// <c>loadFont</c> snippet (@remotion/fonts + staticFile). The family is then treated as declared by the font advisory.
/// </summary>
public partial class ReactRemotionSandboxTools
{
    /// <summary>A family name the tool accepts: plain characters only, so it can never be a path or a URL.</summary>
    private static readonly Regex FontFamilyNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9 &-]{0,63}$", RegexOptions.Compiled);

    private const int MaxStagedFontFiles = 8;

    [Description(
        "Make a font available to the composition. source \"google\" stages a family from the Google Fonts catalog (a family name only, " +
        "never a URL); any other value is a project font file reference (id, storage key or file name from ListProjectFiles; .ttf, .otf or " +
        ".woff2). Families already installed in the sandbox need no staging. The files land under public/fonts/<Family>/ and the result " +
        "carries a ready TypeScript snippet (loadFont from @remotion/fonts with staticFile), the family name and the staticFile paths.")]
    public async Task<string> StageFont(
        [Description("CSS family name, e.g. \"Playfair Display\". For a project file this is the name you will use in fontFamily.")] string family,
        [Description("\"google\" (default) or a project font file reference")] string? source = null,
        [Description("Google source: weights wanted, e.g. [400, 700]. Default [400]. Project source: the weight of the file (default 400).")] int[]? weights = null,
        [Description("Also stage the italic style")] bool italic = false,
        CancellationToken cancellationToken = default)
    {
        WorkflowExecutionContext ctx = RequireContext();
        string name = (family ?? "").Trim();
        _logger.LogInformation("Tool call stage_font for execution {ExecutionId}: family={Family}, source={Source}", ctx.ExecutionId, LogSafe(name), LogSafe(source));

        if (!FontFamilyNamePattern.IsMatch(name))
            return FontError("family must be a plain font family name (letters, digits, spaces, - and & only)");

        bool google = string.IsNullOrWhiteSpace(source) || string.Equals(source.Trim(), "google", StringComparison.OrdinalIgnoreCase);
        try
        {
            return google
                ? await StageGoogleFontAsync(ctx, name, weights, italic, cancellationToken)
                : await StageProjectFontAsync(ctx, name, source!.Trim(), weights?.FirstOrDefault() is int w and > 0 ? w : 400, italic, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error in stage_font for execution {ExecutionId}", ctx.ExecutionId);
            return FontError("unexpected error while staging the font");
        }
    }

    private async Task<string> StageGoogleFontAsync(WorkflowExecutionContext ctx, string name, int[]? weights, bool italic, CancellationToken cancellationToken)
    {
        if (SandboxFonts.Find(name) is { } installed)
        {
            return JsonSerializer.Serialize(new
            {
                ok = true,
                family = installed.Family,
                installed = true,
                message = $"'{installed.Family}' is already installed in the sandbox: use fontFamily '{installed.Family}' directly, no staging needed."
            });
        }

        if (_googleFonts is null || !_googleFonts.Enabled)
        {
            return FontError("Google Fonts staging is disabled on this deployment. Use one of the installed families: " +
                SandboxFonts.FamilyList + ", or stage a project font file with StageFont(family, source: <file reference>).");
        }

        GoogleFontFamily? entry = GoogleFontCatalog.Find(name);
        if (entry is null)
        {
            IReadOnlyList<string> similar = GoogleFontCatalog.Suggest(name);
            return FontError($"'{name}' is not in the Google Fonts catalog." +
                (similar.Count > 0 ? " Did you mean: " + string.Join(", ", similar) + "?" : "") +
                " Installed families: " + SandboxFonts.FamilyList + ".");
        }

        int[] wanted = weights is { Length: > 0 } ? [.. weights.Where(w => w is >= 1 and <= 1000).Distinct().Take(MaxStagedFontFiles)] : [400];
        if (wanted.Length == 0)
            return FontError("weights must be between 1 and 1000");

        List<(GoogleFontFile File, string Style)> picks = [];
        foreach (string style in italic ? new[] { "normal", "italic" } : new[] { "normal" })
        {
            foreach (int weight in wanted)
            {
                GoogleFontFile? file = entry.Files.FirstOrDefault(f => f.Style == style && f.Covers(weight));
                if (file is null)
                    return FontError($"'{entry.Family}' has no {style} style at weight {weight}. Available: " +
                        string.Join(", ", entry.Files.Select(f => $"{f.Style} {f.Weight}" + (f.IsVariable ? $"-{f.WeightMax}" : ""))));
                if (!picks.Any(p => p.File.Sha256 == file.Sha256))
                    picks.Add((file, style));
            }
        }

        if (picks.Count > MaxStagedFontFiles)
            return FontError($"at most {MaxStagedFontFiles} font files can be staged at once");

        ISandboxSession session = _sandboxSessions.Create(ctx.ExecutionId);
        string dir = FontDirectoryName(entry.Family);
        var loads = new StringBuilder();
        List<string> paths = [];
        foreach ((GoogleFontFile file, string style) in picks)
        {
            GoogleFontFetchResult fetched = await _googleFonts.FetchAsync(entry, file, cancellationToken);
            if (!fetched.Ok)
                return FontError(fetched.Error ?? "the font could not be fetched");

            string ext = Path.GetExtension(file.File).ToLowerInvariant();
            string path = $"public/fonts/{dir}/{dir}-{style}-{file.Weight}{(file.IsVariable ? "-" + file.WeightMax : "")}{ext}";
            await session.WriteBytesAsync(path, fetched.Bytes!, cancellationToken);
            paths.Add(path);
            loads.Append(LoadFontCall(entry.Family, path, style, file.IsVariable ? $"{file.Weight} {file.WeightMax}" : file.Weight.ToString()));
        }

        DeclareFontFamily(ctx.ExecutionId, entry.Family);
        return StagedFontResult(entry.Family, paths, loads.ToString());
    }

    private async Task<string> StageProjectFontAsync(WorkflowExecutionContext ctx, string name, string reference, int weight, bool italic, CancellationToken cancellationToken)
    {
        IReadOnlyList<Services.Storage.ProjectWorkspaceFile> files = await _projectFileWorkspace.ListFilesAsync(ctx.ProjectId, cancellationToken);
        Services.Storage.ProjectWorkspaceFile? file = ResolveProjectFile(files, reference);
        if (file is null)
            return FontError("no project file matches that reference; call ListProjectFiles and pass a listed id or file name");

        string ext = Path.GetExtension(file.OriginalFileName ?? "").ToLowerInvariant();
        if (ext is not (".ttf" or ".otf" or ".woff2"))
            return FontError("only .ttf, .otf and .woff2 font files can be staged with StageFont");
        if (file.SizeBytes > _sourceMaxBytes || file.SizeBytes > GoogleFontFetcher.MaxBytes)
            return FontError($"the file is {file.SizeBytes} bytes, over the font staging limit");
        if (!file.StorageKey.StartsWith($"projects/{ctx.ProjectId}/", StringComparison.Ordinal))
            return FontError("the file is outside this project's storage");

        byte[] bytes;
        try
        {
            using GetObjectResponse stored = await _s3Client.GetObjectAsync(_bucketName, file.StorageKey, cancellationToken);
            if (stored.ContentLength > GoogleFontFetcher.MaxBytes)
                return FontError("the file is over the font staging limit");
            using MemoryStream buffer = new();
            await stored.ResponseStream.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > GoogleFontFetcher.MaxBytes)
                return FontError("the file is over the font staging limit");
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (IsObjectMissing(ex))
        {
            return FontError("the file's stored object is missing");
        }

        if (!HasFontMagic(bytes, ext))
            return FontError($"the file does not look like a real {ext} font (wrong file signature)");

        string dir = FontDirectoryName(name);
        string style = italic ? "italic" : "normal";
        string path = $"public/fonts/{dir}/{dir}-{style}-{weight}{ext}";
        await _sandboxSessions.Create(ctx.ExecutionId).WriteBytesAsync(path, bytes, cancellationToken);
        DeclareFontFamily(ctx.ExecutionId, name);
        return StagedFontResult(name, [path], LoadFontCall(name, path, style, weight.ToString()));
    }

    /// <summary>Whether <paramref name="bytes"/> start with the signature of a font of type <paramref name="ext"/>.</summary>
    internal static bool HasFontMagic(byte[] bytes, string ext)
    {
        if (bytes.Length < 12)
            return false;
        uint magic = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return ext switch
        {
            ".woff2" => magic == 0x774F4632, // wOF2
            ".otf" => magic == 0x4F54544F, // OTTO
            ".ttf" => magic is 0x00010000 or 0x74727565, // 0x00010000 or "true"
            _ => false,
        };
    }

    private static string FontDirectoryName(string family) => Regex.Replace(family, @"[^A-Za-z0-9]+", "");

    /// <summary>One <c>loadFont</c> call for a staged file (family and path are validated: no quote can occur).</summary>
    private static string LoadFontCall(string family, string path, string style, string weight) =>
        $"loadFont({{ family: '{family}', url: staticFile('{path["public/".Length..]}'), weight: '{weight}', style: '{style}' }});\n";

    /// <summary>A value for a log line: control characters removed, length capped.</summary>
    internal static string LogSafe(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        string clean = new([.. value.Where(c => !char.IsControl(c))]);
        return clean.Length > 80 ? clean[..80] + "..." : clean;
    }

    /// <summary>The families declared for an execution (staged or written as @font-face); for tests.</summary>
    internal IReadOnlyCollection<string> DeclaredFamiliesFor(Guid executionId)
    {
        AdvisoryState state = StateFor(executionId);
        lock (state)
            return [.. state.DeclaredFamilies];
    }

    private void DeclareFontFamily(Guid executionId, string family)
    {
        AdvisoryState state = StateFor(executionId);
        lock (state)
            state.DeclaredFamilies.Add(family);
    }

    private static string StagedFontResult(string family, List<string> paths, string loads) => JsonSerializer.Serialize(new
    {
        ok = true,
        family,
        files = paths,
        staticFiles = paths.Select(p => p["public/".Length..]),
        snippet = "import {staticFile} from 'remotion';\nimport {loadFont} from '@remotion/fonts';\n\n" + loads,
        usage = $"Paste the snippet at the top level of the module that uses the font (outside any component), then use fontFamily '{family}'. " +
            "Do not declare these files in an @font-face url(): relative paths do not resolve inside the Remotion bundle. " +
            "loadFont returns a promise but calls delayRender itself and releases it once the font is loaded (cancelRender on failure), " +
            "so the render waits for it; you do not need to await it or add your own delayRender. Each call registers one file: " +
            "the family, weight (a range such as '100 900' for a variable font) and style must match the call."
    });

    private static string FontError(string error) => JsonSerializer.Serialize(new { ok = false, error });
}
