using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Color;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads design tokens out of CSS custom properties and Tailwind v4 <c>@theme</c> blocks and turns
/// them into a canonical <see cref="DesignDocument"/>.
///
/// <para>
/// Hand-written, single-pass, linear-time scanner: pure and total. It never touches IO or the
/// network, never resolves <c>@import</c> or <c>url()</c>, and reports hostile input as an
/// <see cref="ImportDiagnostic"/> rather than throwing.
/// </para>
/// </summary>
public sealed class CssThemeImporter : IDesignImporter
{
    /// <summary>Warn: a gradient cannot be represented as a single colour token.</summary>
    public const string GradientNotRepresentable = "GRADIENT_NOT_REPRESENTABLE";

    /// <summary>Warn: a <c>color-mix()</c> value could not be resolved to an sRGB hex.</summary>
    public const string MixUnresolved = "MIX_UNRESOLVED";

    /// <inheritdoc />
    public string Id => "css-theme";

    private const double Confidence = 0.95;
    private const int MaxTokens = 200000;
    private const int MaxEmittedTokens = 1500;
    private const int MaxDepth = 6;
    private const int MaxKeyLength = 48;
    private const int MaxLocLength = 200;

    private static readonly string[] SkipFragments =
    [
        "z-index", "scroll", "viewport", "safe-area", "header-height", "sidebar-width",
    ];

    private static readonly string[] GenericFontFamilies =
    [
        "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui",
        "ui-sans-serif", "ui-serif", "ui-monospace", "ui-rounded", "math", "emoji", "fangsong",
    ];

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            return false;

        if (head.Length == 0)
            return false;

        int limit = Math.Min(head.Length, ImportLimits.HeadBytes);
        ReadOnlySpan<byte> window = head[..limit];

        return ContainsAscii(window, "--"u8) || ContainsAsciiIgnoreCase(window, "@theme"u8);
    }

    /// <summary>
    /// Selectors that open a mode block, mapped to the mode name written under <c>modes</c>.
    /// <c>.theme-dark</c> is the selector a Claude Design export uses.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DefaultThemeSelectors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".dark"] = "dark",
            ["[data-theme=dark]"] = "dark",
            [".theme-dark"] = "dark",
        };

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files) =>
        Import(files, new CssImportOptions(DefaultThemeSelectors));

    internal DesignImportResult Import(IReadOnlyList<ImportFile> files, CssImportOptions? options)
    {
        options ??= new CssImportOptions(DefaultThemeSelectors);

        var design = DesignDocument.New();
        var sources = new List<ImportSourceRef>();
        var diagnostics = new List<ImportDiagnostic>();
        var declarations = new List<CssDeclaration>();

        if (files is not null)
        {
            foreach (ImportFile? file in files)
            {
                if (file is null)
                    continue;

                try
                {
                    ImportOne(file, sources, diagnostics, declarations, options);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(Error("IMPORT_INTERNAL", file.Path ?? string.Empty, exception.GetType().Name));
                }
            }
        }

        try
        {
            Emit(design, declarations, options, diagnostics);
        }
        catch (Exception exception)
        {
            diagnostics.Add(Error("IMPORT_INTERNAL", string.Empty, exception.GetType().Name));
        }

        return new DesignImportResult(design, null, sources, diagnostics);
    }

    private static void ImportOne(
        ImportFile file,
        List<ImportSourceRef> sources,
        List<ImportDiagnostic> diagnostics,
        List<CssDeclaration> declarations,
        CssImportOptions options)
    {
        string path = file.Path ?? string.Empty;
        sources.Add(new ImportSourceRef(path, "css-theme", Sha256(file.Content.Span), file.ProjectFileId));

        if (file.Content.Length > ImportLimits.CssMaxBytes)
        {
            diagnostics.Add(Warn("TRUNCATED", path, $"file is {file.Content.Length} bytes, over the {ImportLimits.CssMaxBytes} byte limit"));
            return;
        }

        if (file.Content.Length == 0)
            return;

        string text = Encoding.UTF8.GetString(file.Content.Span);
        Scan(text, path, declarations, diagnostics, options);
    }

    private static void Scan(
        string text,
        string path,
        List<CssDeclaration> declarations,
        List<ImportDiagnostic> diagnostics,
        CssImportOptions options)
    {
        var stack = new List<Block>();
        var sb = new StringBuilder();
        bool inComment = false;
        char stringQuote = '\0';
        int parenDepth = 0;
        int line = 1;
        int pendingLine = 1;
        bool pendingSet = false;
        bool truncated = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inComment)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    inComment = false;
                    i++;
                }

                continue;
            }

            if (stringQuote != '\0')
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    Append(sb, c, line, ref pendingLine, ref pendingSet);
                    char escaped = text[i + 1];
                    if (escaped == '\n')
                        line++;
                    Append(sb, escaped, line, ref pendingLine, ref pendingSet);
                    i++;
                    continue;
                }

                if (c == stringQuote)
                    stringQuote = '\0';
                if (c == '\n')
                    line++;
                Append(sb, c, line, ref pendingLine, ref pendingSet);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                inComment = true;
                i++;
                if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]))
                    sb.Append(' ');
                continue;
            }

            if (c == '\n')
                line++;

            if (c == '"' || c == '\'')
            {
                stringQuote = c;
                Append(sb, c, line, ref pendingLine, ref pendingSet);
                continue;
            }

            if (c == '(')
                parenDepth++;
            else if (c == ')')
                parenDepth = parenDepth > 0 ? parenDepth - 1 : 0;

            if (parenDepth == 0 && c == '{')
            {
                stack.Add(Classify(NormalizeSelector(sb.ToString()), options.ThemeSelectors));
                ResetAccumulator(sb, ref pendingSet);
                continue;
            }

            if (parenDepth == 0 && c == '}')
            {
                if (sb.Length > 0 && stack.Count > 0 && stack[^1].Kind != BlockKind.Container)
                    Process(sb.ToString(), pendingLine, path, stack[^1], declarations, diagnostics, ref truncated);

                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);

                ResetAccumulator(sb, ref pendingSet);
                continue;
            }

            if (parenDepth == 0 && c == ';')
            {
                if (sb.Length > 0 && stack.Count > 0 && stack[^1].Kind != BlockKind.Container)
                    Process(sb.ToString(), pendingLine, path, stack[^1], declarations, diagnostics, ref truncated);

                ResetAccumulator(sb, ref pendingSet);
                continue;
            }

            Append(sb, c, line, ref pendingLine, ref pendingSet);
        }
    }

    private static void Process(
        string raw,
        int line,
        string path,
        Block block,
        List<CssDeclaration> declarations,
        List<ImportDiagnostic> diagnostics,
        ref bool truncated)
    {
        if (truncated)
            return;

        int colon = raw.IndexOf(':');
        if (colon <= 0)
            return;

        string name = raw[..colon].Trim();
        if (name.Length <= 2 || !name.StartsWith("--", StringComparison.Ordinal))
            return;

        string value = StripImportant(raw[(colon + 1)..].Trim());
        if (value.Length == 0)
            return;

        if (declarations.Count >= MaxTokens)
        {
            diagnostics.Add(Warn("TRUNCATED", path, $"more than {MaxTokens} custom properties; the rest were dropped"));
            truncated = true;
            return;
        }

        declarations.Add(new CssDeclaration(path, line, name.ToLowerInvariant(), value, block.Mode));
    }

    private static void Emit(
        JsonObject design,
        List<CssDeclaration> declarations,
        CssImportOptions options,
        List<ImportDiagnostic> diagnostics)
    {
        var varToPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var mixDeclarations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CssDeclaration declaration in declarations)
        {
            if (declaration.Mode is null)
                mixDeclarations.TryAdd(declaration.VarName[2..], declaration.Value);
        }

        var pendingAliases = new List<PendingAlias>();
        var modeDeclarations = new List<CssDeclaration>();
        var modeOrder = new List<string>();
        var seenModes = new HashSet<string>(StringComparer.Ordinal);

        bool capped = false;
        foreach (CssDeclaration declaration in declarations)
        {
            if (declaration.Mode is not null)
            {
                modeDeclarations.Add(declaration);
                if (seenModes.Add(declaration.Mode))
                    modeOrder.Add(declaration.Mode);
                continue;
            }

            if (varToPath.Count >= MaxEmittedTokens)
            {
                capped = true;
                continue;
            }

            EmitBase(design, declaration, varToPath, pendingAliases, mixDeclarations, diagnostics, options);
        }

        // Alias chains may point at each other in any order. A worklist keyed by the referenced variable
        // resolves each alias once its target exists, so a chain of n aliases costs O(n) whatever its order.
        var waiting = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var queue = new Queue<int>();
        for (int i = pendingAliases.Count - 1; i >= 0; i--)
        {
            string target = pendingAliases[i].TargetVar;
            if (varToPath.ContainsKey(target))
            {
                queue.Enqueue(i);
            }
            else
            {
                if (!waiting.TryGetValue(target, out List<int>? list))
                {
                    list = new List<int>();
                    waiting[target] = list;
                }

                list.Add(i);
            }
        }

        var settled = new bool[pendingAliases.Count];
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            if (varToPath.Count >= MaxEmittedTokens)
            {
                capped = true;
                break;
            }

            PendingAlias alias = pendingAliases[index];
            AliasOutcome outcome = EmitAlias(design, alias, varToPath, diagnostics, options);
            if (outcome == AliasOutcome.Pending)
                continue;

            settled[index] = true;
            if (outcome == AliasOutcome.Resolved
                && waiting.Remove(alias.Declaration.VarName, out List<int>? dependents))
            {
                foreach (int dependent in dependents)
                    queue.Enqueue(dependent);
            }
        }

        if (capped)
        {
            diagnostics.Add(Warn("TOKEN_CAP", string.Empty, $"more than {MaxEmittedTokens} tokens; the rest is ignored"));
        }
        else
        {
            for (int i = 0; i < pendingAliases.Count; i++)
            {
                if (settled[i])
                    continue;

                PendingAlias alias = pendingAliases[i];
                diagnostics.Add(Warn(
                    "UNRESOLVED_ALIAS",
                    alias.Declaration.FilePath,
                    $"'{alias.Declaration.VarName}' references '{DesignKeys.Clip(alias.TargetVar)}', which is not a produced token"));
            }
        }

        // Base declarations (and their aliases) are settled before any mode pass, so a mode
        // override always has a base token to attach to, and modes follow their first-seen order.
        foreach (string mode in modeOrder)
        {
            foreach (CssDeclaration declaration in modeDeclarations)
            {
                if (string.Equals(declaration.Mode, mode, StringComparison.Ordinal))
                    EmitMode(design, mode, declaration, varToPath, mixDeclarations, diagnostics, options);
            }
        }
    }

    private static void EmitBase(
        JsonObject design,
        CssDeclaration declaration,
        Dictionary<string, string> varToPath,
        List<PendingAlias> pendingAliases,
        IReadOnlyDictionary<string, string> mixDeclarations,
        List<ImportDiagnostic> diagnostics,
        CssImportOptions options)
    {
        string name = declaration.VarName;
        string bare = name[2..];
        if (ShouldSkip(bare))
            return;

        Mapping mapping = MapByPrefix(bare);
        if (mapping.Skip)
            return;

        string value = declaration.Value;

        if (IsVarReference(value, out string? targetVar))
        {
            pendingAliases.Add(new PendingAlias(declaration, targetVar!));
            return;
        }

        if (IsColorMixValue(value))
        {
            if (CssColorMix.TryResolve(value, mixDeclarations, out string? mixHex))
            {
                IReadOnlyList<string> mixPaths = options.PathsFor is null
                    ? (mapping.Found && string.Equals(mapping.Type, "color", StringComparison.Ordinal)
                        ? mapping.Paths
                        : ValuePaths(bare, "color"))
                    : options.PathsFor(bare, "color");

                if (mixPaths.Count == 0 || TooDeep(mixPaths))
                    return;

                Store(design, mixPaths, "color", Color(mixHex!), null, declaration, varToPath, name);
                return;
            }

            ReportUnresolvedMix(name, declaration.FilePath, diagnostics);
            return;
        }

        if (ContainsUnsupportedFunction(value))
        {
            ReportUnsupported(name, value, declaration.FilePath, diagnostics);
            return;
        }

        if (mapping.Found)
        {
            if (TooDeep(mapping.Paths))
            {
                diagnostics.Add(Warn("TOO_DEEP", declaration.FilePath, $"'{name}' maps below the {MaxDepth}-segment limit"));
                return;
            }

            if (!TryBuild(value, mapping.Type!, out JsonNode? node, out ImportDiagnostic? diagnostic, out string? original))
            {
                diagnostics.Add(Warn("INVALID_VALUE", declaration.FilePath, $"'{name}': {diagnostic?.Message ?? "value could not be normalized"}"));
                return;
            }

            IReadOnlyList<string> mappingPaths = options.PathsFor is null
                ? mapping.Paths
                : options.PathsFor(bare, mapping.Type!);
            if (mappingPaths.Count == 0 || TooDeep(mappingPaths))
                return;

            Store(design, mappingPaths, mapping.Type!, node, original, declaration, varToPath, name);
            return;
        }

        if (TryDetectFor(bare, value, options, out string? type, out JsonNode? detected, out string? detectedOriginal))
        {
            IReadOnlyList<string> paths = options.PathsFor is null
                ? ValuePaths(bare, type!)
                : options.PathsFor(bare, type!);
            if (paths.Count == 0 || TooDeep(paths))
                return;

            Store(design, paths, type!, detected, detectedOriginal, declaration, varToPath, name);
        }
    }

    /// <summary>
    /// Detection with the options seam applied: a non-null <see cref="CssImportOptions.TypeFor"/>
    /// overrides what the value looks like, and the value is still normalized as that type.
    /// </summary>
    private static bool TryDetectFor(
        string bare,
        string value,
        CssImportOptions options,
        out string? type,
        out JsonNode? node,
        out string? original)
    {
        node = null;
        original = null;

        if (options.TypeFor?.Invoke(bare) is string forcedType)
        {
            type = forcedType;
            if (!TryBuild(value, forcedType, out node, out _, out original))
            {
                type = null;
                return false;
            }

            return true;
        }

        return TryDetect(value, out type, out node, out original);
    }

    private static AliasOutcome EmitAlias(
        JsonObject design,
        PendingAlias alias,
        Dictionary<string, string> varToPath,
        List<ImportDiagnostic> diagnostics,
        CssImportOptions options)
    {
        CssDeclaration declaration = alias.Declaration;
        string name = declaration.VarName;
        string bare = name[2..];

        if (!varToPath.TryGetValue(alias.TargetVar, out string? targetPath))
            return AliasOutcome.Pending;

        string? targetType = TokenType(design, targetPath);
        if (targetType is null)
        {
            diagnostics.Add(Warn("UNRESOLVED_ALIAS", declaration.FilePath, $"'{name}' references '{alias.TargetVar}', which has no type"));
            return AliasOutcome.Failed;
        }

        Mapping mapping = MapByPrefix(bare);
        if (mapping.Skip)
            return AliasOutcome.Failed;

        IReadOnlyList<string> paths;
        if (options.PathsFor is not null)
        {
            paths = options.PathsFor(bare, targetType);
            if (paths.Count == 0 || TooDeep(paths))
                return AliasOutcome.Failed;
        }
        else if (mapping.Found)
        {
            if (TooDeep(mapping.Paths))
            {
                diagnostics.Add(Warn("TOO_DEEP", declaration.FilePath, $"'{name}' maps below the {MaxDepth}-segment limit"));
                return AliasOutcome.Failed;
            }

            if (!string.Equals(mapping.Type, targetType, StringComparison.Ordinal))
            {
                diagnostics.Add(Warn("INVALID_VALUE", declaration.FilePath, $"'{name}' aliases a {targetType} token but looks like {mapping.Type}"));
                return AliasOutcome.Failed;
            }

            paths = mapping.Paths;
        }
        else
        {
            paths = ValuePaths(bare, targetType);
            if (paths.Count == 0 || TooDeep(paths))
                return AliasOutcome.Failed;
        }

        // A Tailwind v4 theme declares both the raw variable and its theme alias, and both map to the
        // same token path (`--background` and `--color-background` -> color.background). Storing the
        // alias at its own target would replace the concrete value with a `{color.background}` self
        // reference, which DesignDocumentValidator rejects (DS_V12) and Compose fails closed on.
        // Drop only that path; a genuine alias to a different token is still emitted.
        var emitPaths = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (!string.Equals(path, targetPath, StringComparison.Ordinal))
                emitPaths.Add(path);
        }

        if (emitPaths.Count == 0)
        {
            // The variable still resolves for later aliases (including alias chains).
            if (!varToPath.ContainsKey(name))
                varToPath[name] = targetPath;
            return AliasOutcome.Resolved;
        }

        Store(design, emitPaths, targetType, JsonValue.Create("{" + targetPath + "}"), null, declaration, varToPath, name);
        return AliasOutcome.Resolved;
    }

    private static void EmitMode(
        JsonObject design,
        string mode,
        CssDeclaration declaration,
        Dictionary<string, string> varToPath,
        IReadOnlyDictionary<string, string> mixDeclarations,
        List<ImportDiagnostic> diagnostics,
        CssImportOptions options)
    {
        string name = declaration.VarName;
        string bare = name[2..];
        if (ShouldSkip(bare))
            return;

        Mapping mapping = MapByPrefix(bare);
        if (mapping.Skip)
            return;

        string value = declaration.Value;

        if (IsVarReference(value, out string? targetVar))
        {
            if (!varToPath.TryGetValue(targetVar!, out string? targetPath))
            {
                diagnostics.Add(Warn("UNRESOLVED_ALIAS", declaration.FilePath, $"'{name}' references '{targetVar}', which is not a produced token"));
                return;
            }

            string? targetType = TokenType(design, targetPath);
            if (targetType is null)
            {
                diagnostics.Add(Warn("UNRESOLVED_ALIAS", declaration.FilePath, $"'{name}' references '{targetVar}', which has no type"));
                return;
            }

            IReadOnlyList<string> aliasPaths;
            if (options.PathsFor is not null)
            {
                aliasPaths = options.PathsFor(bare, targetType);
                if (aliasPaths.Count == 0)
                    return;
            }
            else if (mapping.Found)
            {
                if (!string.Equals(mapping.Type, targetType, StringComparison.Ordinal))
                {
                    diagnostics.Add(Warn("INVALID_VALUE", declaration.FilePath, $"'{name}' aliases a {targetType} token but looks like {mapping.Type}"));
                    return;
                }

                aliasPaths = mapping.Paths;
            }
            else
            {
                aliasPaths = ValuePaths(bare, targetType);
            }

            var aliasNode = JsonValue.Create("{" + targetPath + "}");
            foreach (string path in aliasPaths)
            {
                // Same self-alias guard as EmitAlias: a dark alias must not overwrite the dark
                // value at its own path with a self reference.
                if (string.Equals(path, targetPath, StringComparison.Ordinal))
                    continue;
                if (BaseToken(design, path) is not null)
                    SetMode(design, mode, path, aliasNode);
            }

            return;
        }

        if (IsColorMixValue(value))
        {
            if (CssColorMix.TryResolve(value, mixDeclarations, out string? mixHex))
            {
                IReadOnlyList<string> mixPaths = options.PathsFor is null
                    ? (mapping.Found && string.Equals(mapping.Type, "color", StringComparison.Ordinal)
                        ? mapping.Paths
                        : ValuePaths(bare, "color"))
                    : options.PathsFor(bare, "color");

                if (mixPaths.Count == 0 || TooDeep(mixPaths))
                    return;

                JsonNode? mixNode = Color(mixHex!);
                foreach (string path in mixPaths)
                {
                    if (BaseToken(design, path) is not null)
                        SetMode(design, mode, path, mixNode);
                }

                return;
            }

            ReportUnresolvedMix(name, declaration.FilePath, diagnostics);
            return;
        }

        if (ContainsUnsupportedFunction(value))
        {
            ReportUnsupported(name, value, declaration.FilePath, diagnostics);
            return;
        }

        string? type;
        JsonNode? node;
        string? original = null;
        IReadOnlyList<string> builtPaths;

        if (mapping.Found)
        {
            if (TooDeep(mapping.Paths))
                return;

            if (!TryBuild(value, mapping.Type!, out node, out ImportDiagnostic? diagnostic, out original))
            {
                diagnostics.Add(Warn("INVALID_VALUE", declaration.FilePath, $"'{name}': {diagnostic?.Message ?? "value could not be normalized"}"));
                return;
            }

            type = mapping.Type!;
            builtPaths = options.PathsFor is null ? mapping.Paths : options.PathsFor(bare, type);
            if (builtPaths.Count == 0 || TooDeep(builtPaths))
                return;
        }
        else
        {
            if (!TryDetectFor(bare, value, options, out type, out node, out original))
                return;

            builtPaths = options.PathsFor is null ? ValuePaths(bare, type!) : options.PathsFor(bare, type!);
            if (builtPaths.Count == 0 || TooDeep(builtPaths))
                return;
        }

        foreach (string path in builtPaths)
        {
            if (BaseToken(design, path) is not null)
                SetMode(design, mode, path, node);
        }
    }

    private static void Store(
        JsonObject design,
        IReadOnlyList<string> paths,
        string type,
        JsonNode? node,
        string? original,
        CssDeclaration declaration,
        Dictionary<string, string> varToPath,
        string name)
    {
        foreach (string path in paths)
        {
            SetTokenPreservingChildren(design, path, type, node, Provenance(declaration), Confidence, original);
            if (!varToPath.ContainsKey(name))
                varToPath[name] = path;
        }
    }

    private static void SetTokenPreservingChildren(
        JsonObject design,
        string path,
        string type,
        JsonNode? value,
        TokenProvenance provenance,
        double confidence,
        string? original)
    {
        // A token and a group may share a path (Tailwind's bare `--spacing` beside `--spacing-4`).
        // DesignDocument.SetToken replaces the leaf, so keep any existing children alive.
        var children = new List<KeyValuePair<string, JsonNode?>>();
        JsonObject? existing = DesignDocument.FindToken(design, path);
        if (existing is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> property in existing)
            {
                if (property.Key.Length > 0 && property.Key[0] != '$')
                    children.Add(new KeyValuePair<string, JsonNode?>(property.Key, property.Value?.DeepClone()));
            }
        }

        DesignDocument.SetToken(design, path, type, value, provenance, confidence, original: original);

        if (children.Count == 0)
            return;

        JsonObject? token = DesignDocument.FindToken(design, path);
        if (token is null)
            return;

        foreach (KeyValuePair<string, JsonNode?> child in children)
            token[child.Key] = child.Value;
    }

    private static void SetMode(JsonObject design, string mode, string path, JsonNode? value)
    {
        if (design["modes"] is not JsonObject modes)
        {
            modes = new JsonObject();
            design["modes"] = modes;
        }

        if (modes[mode] is not JsonObject overrides)
        {
            overrides = new JsonObject();
            modes[mode] = overrides;
        }

        overrides[path] = value?.DeepClone();
    }

    private static Mapping MapByPrefix(string bare)
    {
        // shadcn/ui's semantic names, mapped before any prefix rule.
        if (bare == "background")
            return Mapping.Of("color", "color.background");
        if (bare == "foreground")
            return Mapping.Of("color", "color.foreground", "color.text");
        if (bare == "primary")
            return Mapping.Of("color", "color.primary");
        if (bare == "radius")
            return Mapping.Of("dimension", "radius.base");
        if (bare == "spacing")
            return Mapping.Of("dimension", "spacing");

        if (bare == "animate" || bare.StartsWith("animate-", StringComparison.Ordinal))
            return Mapping.SkipName;

        if (bare.StartsWith("color-", StringComparison.Ordinal))
            return Child("color", "color", bare[6..]);
        if (bare.StartsWith("font-", StringComparison.Ordinal))
            return Child("fontFamily", "typography.family", bare[5..]);
        if (bare.StartsWith("text-", StringComparison.Ordinal))
            return Child("dimension", "typography.size", bare[5..]);
        if (bare.StartsWith("spacing-", StringComparison.Ordinal))
            return Child("dimension", "spacing", bare[8..]);
        if (bare.StartsWith("radius-", StringComparison.Ordinal))
            return Child("dimension", "radius", bare[7..]);
        if (bare.StartsWith("shadow-", StringComparison.Ordinal))
            return Child("shadow", "shadow", bare[7..]);
        if (bare.StartsWith("ease-", StringComparison.Ordinal))
            return Child("cubicBezier", "motion.easing", bare[5..]);

        return Mapping.Unknown;
    }

    private static Mapping Child(string type, string prefix, string rest)
    {
        string suffix = Seg(rest);
        return suffix.Length == 0 ? Mapping.SkipName : Mapping.Of(type, prefix + "." + suffix);
    }

    private static List<string> ValuePaths(string bare, string type)
    {
        string suffix = Seg(bare);
        if (suffix.Length == 0)
            return [];

        return type switch
        {
            "color" => ["color." + suffix],
            "fontFamily" => ["typography.family." + suffix],
            "dimension" => ["spacing." + suffix],
            "shadow" => ["shadow." + suffix],
            "duration" => ["motion.duration." + suffix],
            "cubicBezier" => ["motion.easing." + suffix],
            _ => [],
        };
    }

    private static bool TryBuild(string value, string type, out JsonNode? node, out ImportDiagnostic? diagnostic, out string? original)
    {
        node = null;
        diagnostic = null;
        original = null;

        switch (type)
        {
            case "color":
                if (ColorConvert.TryToSrgbHex(value, out string hex, out string? fromFunction))
                {
                    node = Color(hex);
                    original = fromFunction;
                    return true;
                }

                diagnostic = Warn("COLOR_INVALID", string.Empty, "colour string could not be converted to sRGB");
                return false;

            case "fontFamily":
                node = TokenNormalizer.NormalizeFontFamily(JsonValue.Create(value), out diagnostic);
                return node is not null;

            case "dimension":
                node = TokenNormalizer.NormalizeDimension(JsonValue.Create(value), out diagnostic);
                return node is not null;

            case "duration":
                node = TokenNormalizer.NormalizeDuration(JsonValue.Create(value), out diagnostic);
                return node is not null;

            case "shadow":
                node = TokenNormalizer.NormalizeShadow(JsonValue.Create(value), out diagnostic);
                return node is not null;

            case "cubicBezier":
                node = TryParseCubicBezier(value, out diagnostic);
                return node is not null;

            case "number":
                // The scanner hands over CSS text, so a numeric token has to be parsed before the
                // normalizer (which only accepts a JSON number) sees it.
                if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    || !double.IsFinite(number))
                {
                    diagnostic = Warn("INVALID_VALUE", string.Empty, "number must be a finite number");
                    return false;
                }

                node = TokenNormalizer.Normalize("number", JsonValue.Create(number), out diagnostic);
                return node is not null;

            case "fontWeight":
            case "typography":
            case "keyword":
                node = TokenNormalizer.Normalize(type, JsonValue.Create(value), out diagnostic);
                return node is not null;

            default:
                diagnostic = Warn("INVALID_VALUE", string.Empty, $"unsupported token type '{type}'");
                return false;
        }
    }

    private static bool TryDetect(string value, out string? type, out JsonNode? node, out string? original)
    {
        type = null;
        node = null;
        original = null;

        if (ColorConvert.TryToSrgbHex(value, out string hex, out string? fromFunction))
        {
            type = "color";
            node = Color(hex);
            original = fromFunction;
            return true;
        }

        if (value.Trim().StartsWith("cubic-bezier(", StringComparison.OrdinalIgnoreCase))
        {
            JsonNode? parsed = TryParseCubicBezier(value, out _);
            if (parsed is not null)
            {
                type = "cubicBezier";
                node = parsed;
                return true;
            }
        }

        if (LooksLikeFontFamily(value))
        {
            JsonNode? families = TokenNormalizer.NormalizeFontFamily(JsonValue.Create(value), out _);
            if (families is not null)
            {
                type = "fontFamily";
                node = families;
                return true;
            }
        }

        if (LooksLikeDuration(value))
        {
            JsonNode? duration = TokenNormalizer.NormalizeDuration(JsonValue.Create(value), out _);
            if (duration is not null)
            {
                type = "duration";
                node = duration;
                return true;
            }
        }

        if (LooksLikeDimension(value))
        {
            JsonNode? dimension = TokenNormalizer.NormalizeDimension(JsonValue.Create(value), out _);
            if (dimension is not null)
            {
                type = "dimension";
                node = dimension;
                return true;
            }
        }

        return false;
    }

    private static JsonNode? TryParseCubicBezier(string value, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;
        const string prefix = "cubic-bezier(";

        string trimmed = value.Trim();
        if (trimmed.Length <= prefix.Length
            || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || trimmed[^1] != ')')
        {
            diagnostic = Warn("CUBIC_BEZIER_INVALID", string.Empty, "easing must be a cubic-bezier(...) value");
            return null;
        }

        string[] pieces = trimmed[prefix.Length..^1].Split(',');
        if (pieces.Length != 4)
        {
            diagnostic = Warn("CUBIC_BEZIER_INVALID", string.Empty, "cubic bezier must hold exactly four numbers");
            return null;
        }

        var array = new JsonArray();
        foreach (string piece in pieces)
        {
            if (!double.TryParse(piece.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                diagnostic = Warn("CUBIC_BEZIER_INVALID", string.Empty, "cubic bezier entries must be finite numbers");
                return null;
            }

            array.Add(JsonValue.Create(number));
        }

        return TokenNormalizer.NormalizeCubicBezier(array, out diagnostic);
    }

    private static bool LooksLikeFontFamily(string value)
    {
        if (value.Contains('('))
            return false;

        if (value.Contains(',') || value.Contains('"') || value.Contains('\''))
            return true;

        string trimmed = value.Trim().Trim('"', '\'').ToLowerInvariant();
        foreach (string generic in GenericFontFamilies)
        {
            if (string.Equals(trimmed, generic, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool LooksLikeDuration(string value)
    {
        string trimmed = value.Trim().ToLowerInvariant();
        if (trimmed.EndsWith("ms", StringComparison.Ordinal))
            return IsNumber(trimmed[..^2]);
        if (trimmed.EndsWith("s", StringComparison.Ordinal))
            return IsNumber(trimmed[..^1]);

        return false;
    }

    private static bool LooksLikeDimension(string value)
    {
        string trimmed = value.Trim().ToLowerInvariant();
        if (trimmed.EndsWith("px", StringComparison.Ordinal))
            return IsNumber(trimmed[..^2]);
        if (trimmed.EndsWith("rem", StringComparison.Ordinal))
            return IsNumber(trimmed[..^3]);
        if (trimmed.EndsWith("%", StringComparison.Ordinal))
            return IsNumber(trimmed[..^1]);
        if (trimmed.EndsWith("em", StringComparison.Ordinal))
            return IsNumber(trimmed[..^2]);

        return false;
    }

    private static bool IsNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number);

    private static bool IsColorMixValue(string value) =>
        value.TrimStart().StartsWith("color-mix(", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsUnsupportedFunction(string value) =>
        value.Contains("calc(", StringComparison.OrdinalIgnoreCase)
        || value.Contains("clamp(", StringComparison.OrdinalIgnoreCase)
        || value.Contains("linear-gradient(", StringComparison.OrdinalIgnoreCase);

    private static void ReportUnresolvedMix(string name, string path, List<ImportDiagnostic> diagnostics)
    {
        string message = $"'{name}' uses color-mix(), which could not be resolved to an sRGB hex";
        diagnostics.Add(Warn("UNSUPPORTED_VALUE", path, message) with { Axis = DesignImportAxis.Tokens });
        diagnostics.Add(Warn(MixUnresolved, path, message) with { Axis = DesignImportAxis.DerivedTokens });
    }

    private static void ReportUnsupported(string name, string value, string path, List<ImportDiagnostic> diagnostics)
    {
        diagnostics.Add(Warn("UNSUPPORTED_VALUE", path, UnsupportedMessage(name))
            with
        { Axis = DesignImportAxis.Tokens });

        if (value.Contains("linear-gradient(", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Warn(
                GradientNotRepresentable,
                path,
                $"'{name}' is a gradient, which cannot be represented as a single colour token")
                with
            { Axis = DesignImportAxis.DerivedTokens });
        }
    }

    private static string UnsupportedMessage(string name) =>
        $"'{name}' uses calc()/clamp()/linear-gradient(), which is not supported";

    private static bool IsVarReference(string value, out string? targetVar)
    {
        targetVar = null;

        string trimmed = value.Trim();
        if (trimmed.Length < 5
            || !trimmed.StartsWith("var(", StringComparison.OrdinalIgnoreCase)
            || trimmed[^1] != ')')
        {
            return false;
        }

        string inner = trimmed[4..^1].Trim();
        int comma = inner.IndexOf(',');
        if (comma >= 0)
            inner = inner[..comma].Trim();

        if (inner.Length <= 2 || !inner.StartsWith("--", StringComparison.Ordinal))
            return false;

        targetVar = inner.ToLowerInvariant();
        return true;
    }

    private static bool ShouldSkip(string bare)
    {
        string lower = bare.ToLowerInvariant();
        foreach (string fragment in SkipFragments)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TooDeep(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (path.Split('.').Length > MaxDepth)
                return true;
        }

        return false;
    }

    private static string? TokenType(JsonObject design, string path)
    {
        JsonObject? token = BaseToken(design, path);
        return token?["$type"] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : null;
    }

    private static JsonObject? BaseToken(JsonObject design, string path)
    {
        JsonObject? token = DesignDocument.FindToken(design, path);
        return token is not null && token.ContainsKey("$value") ? token : null;
    }

    private static string Seg(string raw)
    {
        var parts = new List<string>();
        foreach (string piece in raw.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            string key = NormalizeKey(piece);
            if (key.Length > 0)
                parts.Add(key);
        }

        return string.Join('.', parts);
    }

    private static string NormalizeKey(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            char lower = char.ToLowerInvariant(c);
            builder.Append(IsKeyCharacter(lower) ? lower : '-');
        }

        int start = 0;
        while (start < builder.Length && !IsKeyStart(builder[start]))
            start++;

        if (start >= builder.Length)
            return string.Empty;

        string result = builder.ToString(start, builder.Length - start);
        return result.Length > MaxKeyLength ? result[..MaxKeyLength] : result;
    }

    private static bool IsKeyCharacter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '_' or '-';

    private static bool IsKeyStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

    private static string NormalizeSelector(string raw)
    {
        string[] parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts).ToLowerInvariant();
    }

    private static Block Classify(string selector, IReadOnlyDictionary<string, string> themeSelectors)
    {
        if (selector.Length == 0)
            return Block.Container;

        foreach (string part in selector.Split(','))
        {
            if (FindMode(part.Trim(), themeSelectors) is string mode)
                return new Block(BlockKind.Theme, mode);
        }

        foreach (string part in selector.Split(','))
        {
            string candidate = part.Trim();
            if (candidate is ":root" or "html" or ".light" or "@theme" or "@theme inline")
                return Block.Base;
        }

        return Block.Container;
    }

    private static string? FindMode(string selector, IReadOnlyDictionary<string, string> themeSelectors)
    {
        if (themeSelectors.TryGetValue(selector, out string? mode))
            return mode;

        // Keep the historical tolerance for quoting and spacing: `[data-theme="dark"]` still names
        // the same selector as `[data-theme=dark]`.
        string compact = selector.Replace("\"", string.Empty).Replace("'", string.Empty).Replace(" ", string.Empty);
        return themeSelectors.TryGetValue(compact, out mode) ? mode : null;
    }

    private static string StripImportant(string value)
    {
        const string marker = "!important";
        int index = value.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || value[(index + marker.Length)..].Trim().Length != 0)
            return value;

        return value[..index].Trim();
    }

    private static void Append(StringBuilder sb, char c, int line, ref int pendingLine, ref bool pendingSet)
    {
        if (!pendingSet && !char.IsWhiteSpace(c))
        {
            pendingLine = line;
            pendingSet = true;
        }

        sb.Append(c);
    }

    private static void ResetAccumulator(StringBuilder sb, ref bool pendingSet)
    {
        sb.Clear();
        pendingSet = false;
    }

    private static JsonObject Color(string hex) =>
        new() { ["hex"] = hex, ["colorSpace"] = "srgb" };

    private static TokenProvenance Provenance(CssDeclaration declaration)
    {
        string loc = declaration.FilePath + ":" + declaration.Line.ToString(CultureInfo.InvariantCulture);
        if (loc.Length > MaxLocLength)
            loc = loc[(loc.Length - MaxLocLength)..];

        return new TokenProvenance("static", "css-theme", null, loc);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool ContainsAscii(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;

        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return true;
        }

        return false;
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;

        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                byte a = Lower(haystack[i + j]);
                byte b = Lower(needle[j]);
                if (a != b)
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }

    private static byte Lower(byte value) =>
        value >= (byte)'A' && value <= (byte)'Z' ? (byte)(value + 32) : value;

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);

    private static ImportDiagnostic Error(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Error, code, path, message);

    private sealed record CssDeclaration(string FilePath, int Line, string VarName, string Value, string? Mode);

    private sealed record PendingAlias(CssDeclaration Declaration, string TargetVar);

    private enum AliasOutcome
    {
        Resolved,
        Pending,
        Failed,
    }

    private enum BlockKind
    {
        Container,
        Base,
        Theme,
    }

    private readonly record struct Block(BlockKind Kind, string? Mode = null)
    {
        public static readonly Block Container = new(BlockKind.Container);

        public static readonly Block Base = new(BlockKind.Base);
    }

    private readonly record struct Mapping(bool Found, bool Skip, string? Type, IReadOnlyList<string> Paths)
    {
        public static readonly Mapping Unknown = new(false, false, null, []);

        public static readonly Mapping SkipName = new(false, true, null, []);

        public static Mapping Of(string type, params string[] paths) => new(true, false, type, paths);
    }
}

/// <summary>
/// The seam that lets a vendor reader name the modes and the token paths the CSS scanner should
/// produce, without the scanner knowing which vendor it is reading.
/// </summary>
/// <param name="ThemeSelectors">Normalised selector (e.g. <c>.theme-dark</c>) to mode name.</param>
/// <param name="PathsFor">
/// Bare variable name (without the leading dashes) and detected type to the explicit dot paths,
/// or an empty list to skip the declaration. Null keeps the prefix/value mapping.
/// </param>
/// <param name="TypeFor">
/// Bare variable name to an explicit design-token type, or null to detect. Null falls back to
/// detection.
/// </param>
internal sealed record CssImportOptions(
    IReadOnlyDictionary<string, string> ThemeSelectors,
    Func<string, string, IReadOnlyList<string>>? PathsFor = null,
    Func<string, string?>? TypeFor = null);
