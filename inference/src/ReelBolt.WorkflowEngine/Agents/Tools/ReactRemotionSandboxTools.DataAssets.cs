using System.Text.Json;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using ReelBolt.WorkflowEngine.Execution.Guardrails;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// Data-asset staging (design sandbox-customization 2.4): <c>.json</c> is stageable only while tool
/// screening is Off, after a parse check and an 8 MB cap. Code extensions stay refused.
/// </summary>
public partial class ReactRemotionSandboxTools
{
    /// <summary>Largest JSON data asset <see cref="StageProjectFile"/> will stage.</summary>
    internal const long JsonStagingMaxBytes = 8L * 1024 * 1024;

    /// <summary>Whether guardrail tool screening is On (read-only view of <c>WorkflowEngine:Guardrails:ToolScreening</c>).</summary>
    private bool _toolScreeningOn;

    private static bool ReadToolScreeningOn(IConfiguration configuration) =>
        Enum.TryParse(configuration[GuardrailConfig.SectionName + ":ToolScreening"], ignoreCase: true, out GuardrailMode mode)
        && mode == GuardrailMode.On;

    private static bool IsJsonName(string? name) =>
        !string.IsNullOrEmpty(name) && string.Equals(Path.GetExtension(name.Trim()), ".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this staging is a JSON data asset that bypasses the code-extension refusal.</summary>
    private bool IsStageableJson(ProjectWorkspaceFile file, string destination) =>
        !_toolScreeningOn
        && (IsJsonName(file.OriginalFileName) || IsJsonName(file.OriginalPath) || IsJsonName(destination))
        && !(IsUnstageableExceptJson(file.OriginalFileName) || IsUnstageableExceptJson(file.OriginalPath) || IsUnstageableExceptJson(destination));

    private static bool IsUnstageableExceptJson(string? name) => IsUnstageable(name) && !IsJsonName(name);

    /// <summary>The refusal for a code file (null when it may be staged).</summary>
    private string? StagingRefusal(ProjectWorkspaceFile file, string destination)
    {
        if (!(IsUnstageable(file.OriginalFileName) || IsUnstageable(file.OriginalPath) || IsUnstageable(destination)))
            return null;
        if (IsStageableJson(file, destination))
            return null;

        return _toolScreeningOn
            ? "code and data files (.js, .ts, .tsx, .json, .wasm, .html, ...) cannot be staged; read the file with " +
              "ReadProjectFile and write it with WriteSandboxFile instead"
            : "code files (.js, .jsx, .ts, .tsx, .mjs, .cjs, .wasm, .html) cannot be staged; read the file with " +
              "ReadProjectFile and write it with WriteSandboxFile instead. JSON data files (.json) can be staged.";
    }

    /// <summary>Downloads, parse-checks and writes a JSON asset; returns an error message, or null on success.</summary>
    private async Task<string?> StageJsonAsync(
        ISandboxSession session, ProjectWorkspaceFile file, string destination, CancellationToken cancellationToken)
    {
        if (file.SizeBytes > JsonStagingMaxBytes)
            return $"the JSON file is {file.SizeBytes} bytes, over the {JsonStagingMaxBytes}-byte (8 MB) limit for staging data files";

        using GetObjectResponse stored = await _s3Client.GetObjectAsync(_bucketName, file.StorageKey, cancellationToken);
        // Bounded read: stop at the cap + 1 byte so an object larger than its metadata says is never buffered whole.
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stored.ResponseStream.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > JsonStagingMaxBytes)
                return $"the JSON file is over the {JsonStagingMaxBytes}-byte (8 MB) limit for staging data files";
        }

        byte[] bytes = buffer.ToArray();
        // JsonDocument.Parse does not skip a UTF-8 BOM (JSON exported by Windows tools has one); drop it.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];
        try
        {
            using JsonDocument _ = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            return "the file is not valid JSON (" + ex.Message + "); fix it or write the data with WriteSandboxFile";
        }

        await session.WriteBytesAsync(destination, bytes, cancellationToken);
        return null;
    }

    /// <summary>The exec refusal text: the allowed set, so the agent does not retry other commands.</summary>
    internal const string AllowedCommandsGuidance =
        "allowed: npm run build|render|typecheck|compositions; npx remotion render|still|compositions; " +
        "use the CheckLayout and CheckLintAndTypeErrors tools for layout and type checks";
}
