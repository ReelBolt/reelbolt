using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Brand;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.DesignSystems;

/// <summary>
/// One brand image the design system references under <c>assets.*</c> (a slot's base file or one of its
/// variants): whose logo it is, its shape, and which backgrounds it is readable on.
/// </summary>
public sealed record BrandAssetEntry(
    string Path,
    string Slot,
    string? VariantName,
    string FileRef,
    Guid? FileId,
    string? FileName,
    string Subject,
    string Form,
    string? Tone,
    IReadOnlyList<string> ReadableOn,
    double? Luminance,
    string Source)
{
    public const string SourcePersisted = "persisted";
    public const string SourceMeasured = "measured";
    public const string SourceUnknown = "unknown";

    /// <summary>Whether a tone is known (persisted or measured).</summary>
    public bool HasTone => Tone is not null;

    /// <summary>The background this mark suits: <c>dark</c>, <c>light</c> or <c>any</c>; null when its tone is unknown.</summary>
    public string? BestOn => BrandAssetCatalog.BestOn(Tone, ReadableOn);

    /// <summary>One plain sentence telling an authoring agent what this file is for.</summary>
    public string UseFor => BrandAssetCatalog.UseFor(Subject, Tone, ReadableOn);
}

/// <summary>
/// The brand images of the pinned design system, resolved once at run load. Persisted keys
/// (<c>file</c>, <c>subject</c>, <c>form</c>, <c>tone</c>, <c>readableOn</c>, <c>luminance</c>) win; a legacy
/// document without them is measured from the project's own stored file. Best effort: any failure leaves the
/// entry without a tone and never fails the run.
/// </summary>
public sealed class BrandAssetCatalog
{
    public const int MaxEntries = 12;
    public const long MaxProfileBytes = 2L * 1024 * 1024;
    private const string ProjectFilePrefix = "project-file:";

    public static readonly BrandAssetCatalog Empty = new([]);

    public BrandAssetCatalog(IReadOnlyList<BrandAssetEntry> entries) => Entries = entries;

    public IReadOnlyList<BrandAssetEntry> Entries { get; }

    public BrandAssetEntry? ForFile(Guid fileId) => Entries.FirstOrDefault(e => e.FileId == fileId);

    public BrandAssetEntry? ForPath(string path) =>
        Entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal));

    /// <summary>Entries from the document's persisted keys only; no storage access.</summary>
    public static BrandAssetCatalog FromDocument(JsonObject? design) =>
        Compose(design, null, null);

    /// <summary>
    /// Entries for every <c>project-file:</c> reference under <c>assets.*</c>, measuring (when a workspace is
    /// available) the ones whose document carries no tone. Never throws except on cancellation.
    /// </summary>
    public static async Task<BrandAssetCatalog> BuildAsync(
        JsonObject? design,
        string? productName,
        IProjectFileWorkspace? workspace,
        Guid projectId,
        CancellationToken ct)
    {
        if (design is null)
            return Empty;

        try
        {
            Dictionary<Guid, ProjectWorkspaceFile> files = [];
            List<Draft> drafts = ReadDrafts(design);
            if (drafts.Count == 0)
                return Empty;

            if (workspace is not null)
            {
                try
                {
                    foreach (ProjectWorkspaceFile file in await workspace.ListFilesAsync(projectId, ct))
                        files[file.Id] = file;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    files.Clear();
                }
            }

            var measured = new Dictionary<Guid, LogoTone?>();
            if (workspace is not null)
            {
                foreach (Draft draft in drafts)
                {
                    if (draft.PersistedTone is not null || draft.FileId is not { } id || measured.ContainsKey(id))
                        continue;
                    if (!files.TryGetValue(id, out ProjectWorkspaceFile? file))
                        continue;

                    measured[id] = await ProfileFileAsync(workspace, projectId, file, ct);
                }
            }

            return Compose(design, productName, files, measured, drafts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// Measures a stored png/svg of at most <see cref="MaxProfileBytes"/>. Null when the file is not a profilable
    /// image, is too large, or anything goes wrong. Never throws except on cancellation.
    /// </summary>
    public static async Task<LogoTone?> ProfileFileAsync(
        IProjectFileWorkspace workspace,
        Guid projectId,
        ProjectWorkspaceFile file,
        CancellationToken ct)
    {
        string? mime = ProfilableMime(file.MimeType, file.OriginalFileName);
        if (mime is null || file.SizeBytes <= 0 || file.SizeBytes > MaxProfileBytes)
            return null;
        if (!file.StorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            return null;

        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "brand-logo-" + Guid.NewGuid().ToString("N"));
        try
        {
            await workspace.DownloadStorageKeyToFileAsync(projectId, file.StorageKey, temp, MaxProfileBytes, ct);
            byte[] bytes = await File.ReadAllBytesAsync(temp, ct);
            if (bytes.Length == 0 || bytes.Length > MaxProfileBytes)
                return null;

            return BrandLogoProfiler.Profile(bytes, mime);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // best effort
            }
        }
    }

    /// <summary>The mime the profiler understands for this file, or null for anything but png/svg.</summary>
    public static string? ProfilableMime(string? mimeType, string? fileName)
    {
        string mime = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
        if (mime == "image/png") return "image/png";
        if (mime is "image/svg+xml" or "image/svg") return "image/svg+xml";

        string ext = System.IO.Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (mime is "" or "application/octet-stream")
        {
            if (ext == ".png") return "image/png";
            if (ext == ".svg") return "image/svg+xml";
        }

        return null;
    }

    public static string? BestOn(string? tone, IReadOnlyList<string> readableOn)
    {
        bool onLight = readableOn.Contains(LogoTone.Light, StringComparer.Ordinal);
        bool onDark = readableOn.Contains(LogoTone.Dark, StringComparer.Ordinal);
        if (onLight && onDark) return "any";
        if (onLight) return "light";
        if (onDark) return "dark";

        return tone switch
        {
            LogoTone.Dark => "light",
            LogoTone.Light => "dark",
            LogoTone.Mid => "any",
            _ => null,
        };
    }

    public static string UseFor(string subject, string? tone, IReadOnlyList<string> readableOn)
    {
        string who = subject switch
        {
            BrandLogoClassifier.SubjectProduct => "product logo",
            BrandLogoClassifier.SubjectPublisher =>
                "company mark, not the product logo; only if the brief asks, as a secondary 'by <company>' element",
            _ => "unidentified mark, not confirmed as the product logo",
        };

        string? best = BestOn(tone, readableOn);
        string where = best switch
        {
            "any" => "readable on light and dark backgrounds",
            "dark" => "readable on dark backgrounds only",
            "light" => "readable on light backgrounds only",
            _ => "tone unmeasured: check it against the actual background",
        };

        return who + "; " + where;
    }

    // ---------------------------------------------------------------------------------------------

    private sealed record Draft(
        string Path,
        string Slot,
        string? VariantName,
        string FileRef,
        Guid? FileId,
        string? PersistedFile,
        string? PersistedSubject,
        string? PersistedForm,
        string? PersistedTone,
        IReadOnlyList<string>? PersistedReadableOn,
        double? PersistedLuminance);

    private static BrandAssetCatalog Compose(
        JsonObject? design,
        string? productName,
        IReadOnlyDictionary<Guid, ProjectWorkspaceFile>? files,
        IReadOnlyDictionary<Guid, LogoTone?>? measured = null,
        List<Draft>? drafts = null)
    {
        if (design is null)
            return Empty;

        try
        {
            drafts ??= ReadDrafts(design);
            string? slug = productName ?? BrandLogoNames.ProductSlugFrom(StringOf(design["name"]));
            string prose = string.Join(
                "\n",
                new[] { StringOf((design["voice"] as JsonObject)?["summary"]), StringOf(design["name"]) }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            IReadOnlyList<string> publishers = BrandLogoNames.PublisherNamesFrom(prose);

            var entries = new List<BrandAssetEntry>();
            foreach (Draft draft in drafts)
            {
                string? fileName = DesignText.Sanitize(draft.PersistedFile, 200);
                if (fileName is null && draft.FileId is { } id && files is not null && files.TryGetValue(id, out ProjectWorkspaceFile? file))
                    fileName = DesignText.Sanitize(file.OriginalFileName, 200);

                string subject = draft.PersistedSubject ?? BrandLogoClassifier.SubjectUnknown;
                string form = draft.PersistedForm ?? BrandLogoClassifier.FormMark;
                if (draft.PersistedSubject is null || draft.PersistedForm is null)
                {
                    if (fileName is not null)
                    {
                        (string classifiedSubject, string classifiedForm, _) =
                            BrandLogoClassifier.Classify(fileName, slug, publishers);
                        subject = draft.PersistedSubject ?? classifiedSubject;
                        form = draft.PersistedForm ?? classifiedForm;
                    }
                }

                string? tone = draft.PersistedTone;
                IReadOnlyList<string> readableOn = draft.PersistedReadableOn ?? [];
                double? luminance = draft.PersistedLuminance;
                string source = tone is null ? BrandAssetEntry.SourceUnknown : BrandAssetEntry.SourcePersisted;

                if (tone is null && draft.FileId is { } fid && measured is not null
                    && measured.TryGetValue(fid, out LogoTone? profile) && profile is not null)
                {
                    tone = profile.Tone;
                    luminance = profile.Luminance;
                    var readable = new List<string>();
                    if (profile.ReadableOnLight) readable.Add(LogoTone.Light);
                    if (profile.ReadableOnDark) readable.Add(LogoTone.Dark);
                    readableOn = readable;
                    source = BrandAssetEntry.SourceMeasured;
                }

                entries.Add(new BrandAssetEntry(
                    draft.Path, draft.Slot, draft.VariantName, draft.FileRef, draft.FileId, fileName,
                    subject, form, tone, readableOn, luminance, source));
            }

            return new BrandAssetCatalog(entries);
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    private static List<Draft> ReadDrafts(JsonObject design)
    {
        var drafts = new List<Draft>();
        if (design["assets"] is not JsonObject assets)
            return drafts;

        foreach (KeyValuePair<string, JsonNode?> asset in assets)
        {
            string? slot = DesignText.Sanitize(asset.Key, 60);
            if (slot is null || asset.Value is not JsonObject value)
                continue;

            AddDraft(drafts, "assets." + slot, slot, null, value);
            if (value["variants"] is JsonArray variants)
            {
                for (int i = 0; i < variants.Count; i++)
                {
                    if (variants[i] is JsonObject variant)
                    {
                        string? name = DesignText.Sanitize(StringOf(variant["name"]), 60);
                        AddDraft(drafts, $"assets.{slot}.variants[{i}]", slot, name, variant);
                    }
                }
            }

            if (drafts.Count >= MaxEntries)
                break;
        }

        return drafts.Count > MaxEntries ? drafts.GetRange(0, MaxEntries) : drafts;
    }

    private static void AddDraft(List<Draft> drafts, string path, string slot, string? variantName, JsonObject node)
    {
        string? fileRef = StringOf(node["fileRef"]);
        if (fileRef is null || !fileRef.StartsWith(ProjectFilePrefix, StringComparison.Ordinal))
            return;

        Guid? id = Guid.TryParse(fileRef[ProjectFilePrefix.Length..], out Guid parsed) ? parsed : null;

        string? tone = StringOf(node["tone"]);
        if (tone is not (LogoTone.Dark or LogoTone.Light or LogoTone.Mid))
            tone = null;

        string? subject = StringOf(node["subject"]);
        if (subject is not (BrandLogoClassifier.SubjectProduct or BrandLogoClassifier.SubjectPublisher or BrandLogoClassifier.SubjectUnknown))
            subject = null;

        string? form = StringOf(node["form"]);
        if (form is not (BrandLogoClassifier.FormMark or BrandLogoClassifier.FormWordmark or BrandLogoClassifier.FormLockup))
            form = null;

        List<string>? readable = null;
        if (node["readableOn"] is JsonArray array)
        {
            readable = [];
            foreach (JsonNode? item in array)
            {
                string? s = StringOf(item);
                if (s is LogoTone.Light or LogoTone.Dark && !readable.Contains(s))
                    readable.Add(s);
            }
        }

        double? luminance = null;
        if (node["luminance"] is JsonValue lum && lum.TryGetValue(out double l) && double.IsFinite(l))
            luminance = Math.Round(Math.Clamp(l, 0, 1), 3);

        drafts.Add(new Draft(
            path, slot, variantName, DesignText.Sanitize(fileRef, 400) ?? fileRef, id,
            StringOf(node["file"]), subject, form, tone, tone is null ? null : readable ?? [], luminance));
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    internal static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
