using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Validates a design document against the closed rule set <c>DS_V01</c>..<c>DS_V22</c>. The
/// validator is pure: it reads the tree and the caller-supplied <see cref="DesignValidationContext"/>
/// only, accumulates <b>every</b> violation it finds, and never throws on malformed input — a
/// structure it cannot make sense of becomes a violation, not an exception.
/// </summary>
public static class DesignDocumentValidator
{
    /// <summary>The largest serialized document allowed, in UTF-8 bytes (V-1).</summary>
    public const int MaxSerializedBytes = 262144;

    /// <summary>The largest number of tokens allowed (V-7).</summary>
    public const int MaxTokens = 1500;

    /// <summary>The deepest dot path allowed, counted in segments (V-6).</summary>
    public const int MaxDepth = 6;

    /// <summary>The longest dot path allowed, in characters.</summary>
    public const int MaxPathLength = 160;

    /// <summary>The longest <c>$description</c> allowed (V-10).</summary>
    public const int MaxDescriptionLength = 200;

    /// <summary>The longest <c>assets.&lt;role&gt;.name</c> allowed (V-15).</summary>
    public const int MaxAssetNameLength = 200;

    /// <summary>The most <c>fonts.files</c> entries allowed (V-19).</summary>
    public const int MaxFontFiles = 20;

    /// <summary>The longest <c>fonts.files</c> family key allowed, in characters (V-19).</summary>
    public const int MaxFontKeyLength = 80;

    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.Ordinal)
    {
        "schema", "name", "color", "typography", "spacing", "radius", "shadow", "motion",
        "component", "z", "modes", "voice", "assets", "fonts",
    };

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "color", "dimension", "fontFamily", "fontWeight", "duration", "cubicBezier", "number",
        "shadow", "typography", "keyword",
    };

    private static readonly HashSet<string> ProvMethods = new(StringComparer.Ordinal)
    {
        "imported", "static", "llm", "manual",
    };

    private static readonly string[] ShadowDimensionKeys = ["offsetX", "offsetY", "blur", "spread"];

    /// <summary>Validates <paramref name="doc"/> and returns every violation found.</summary>
    public static DesignValidationResult Validate(JsonObject doc, DesignValidationContext ctx)
    {
        var state = new State(doc, ctx);

        try
        {
            ValidateCore(doc, state);
        }
        catch (Exception exception)
        {
            // "Never throw" outranks reporting: a structural surprise the walker did not anticipate
            // is recorded as a violation so the caller still gets a result rather than an exception.
            state.Add("DS_V00", string.Empty, "validation could not complete: " + exception.GetType().Name);
        }

        return new DesignValidationResult(state.Violations);
    }

    private static void ValidateCore(JsonObject doc, State state)
    {
        // V-1: serialized size in UTF-8 bytes.
        int bytes = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(doc));
        if (bytes > MaxSerializedBytes)
            state.Add("DS_V01", string.Empty, $"serialized document is {bytes} bytes, over the {MaxSerializedBytes} byte limit");

        // V-3: only the known top-level keys.
        foreach (KeyValuePair<string, JsonNode?> property in doc)
        {
            if (!TopLevelKeys.Contains(property.Key))
                state.Add("DS_V03", property.Key, "unknown top-level key");
        }

        // V-2: schema must be present and exact.
        if (!doc.TryGetPropertyValue("schema", out JsonNode? schemaNode))
            state.Add("DS_V02", "schema", "schema is missing");
        else if (!TryGetString(schemaNode, out string? schema) || !string.Equals(schema, DesignDocument.Schema, StringComparison.Ordinal))
            state.Add("DS_V02", "schema", $"schema must be '{DesignDocument.Schema}'");

        WalkGroups(doc, state);

        // V-7: token count.
        if (state.TokenCount > MaxTokens)
            state.Add("DS_V07", string.Empty, $"document has {state.TokenCount} tokens, over the {MaxTokens} limit");

        ValidateModes(doc, state);
        ValidateVoice(doc, state);
        ValidateAssets(doc, state);
        ValidateFonts(doc, state);

        // V-16 and V-20 are whole-tree scans.
        WalkStrings(doc, string.Empty, 0, state);
    }

    private static void WalkGroups(JsonObject root, State state)
    {
        foreach (string groupName in DesignDocument.TokenGroupNames)
        {
            if (!root.TryGetPropertyValue(groupName, out JsonNode? node))
                continue;

            if (node is not JsonObject group)
            {
                state.Add("DS_V08", groupName, "token group must be an object");
                continue;
            }

            WalkGroupObject(group, groupName, 1, state);
        }
    }

    private static void WalkGroupObject(JsonObject obj, string path, int depth, State state)
    {
        if (depth > MaxDepth)
            state.Add("DS_V06", path, $"nesting depth {depth} exceeds the {MaxDepth} segment limit");

        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            string key = property.Key;

            if (key.StartsWith('$'))
            {
                if (key is not ("$type" or "$value" or "$description" or "$extensions"))
                    state.Add("DS_V05", path + "." + key, "unknown key in a token group");
                continue;
            }

            if (!IsValidKey(key))
                state.Add("DS_V04", path + "." + key, "key does not match the allowed form");

            string childPath = path + "." + key;
            if (childPath.Length > MaxPathLength)
                state.Add("DS_V04", childPath, $"path is longer than {MaxPathLength} characters");

            if (property.Value is JsonObject child)
            {
                if (child.ContainsKey("$value"))
                    ValidateToken(child, childPath, depth + 1, state);
                else
                    WalkGroupObject(child, childPath, depth + 1, state);
            }
            else
            {
                state.Add("DS_V08", childPath, "token group value must be an object");
            }
        }
    }

    private static void ValidateToken(JsonObject token, string path, int depth, State state)
    {
        if (depth > MaxDepth)
            state.Add("DS_V06", path, $"nesting depth {depth} exceeds the {MaxDepth} segment limit");

        state.TokenCount++;

        if (!state.TokenPaths.Add(path))
            state.Add("DS_V22", path, "duplicate token path after case folding");

        string type = string.Empty;
        bool typeValid = false;

        if (!token.ContainsKey("$type"))
        {
            state.Add("DS_V08", path + ".$type", "token is missing $type");
        }
        else if (!TryGetString(token["$type"], out string? parsedType))
        {
            state.Add("DS_V08", path + ".$type", "$type must be a string");
        }
        else
        {
            type = parsedType!;
            if (KnownTypes.Contains(type))
            {
                typeValid = true;
            }
            else
            {
                state.Add("DS_V08", path + ".$type", $"unknown $type '{type}'");
            }
        }

        foreach (KeyValuePair<string, JsonNode?> property in token)
        {
            string key = property.Key;
            if (key is "$type" or "$value")
                continue;

            if (key == "$description")
            {
                ValidateDescription(property.Value, path, state);
            }
            else if (key == "$extensions")
            {
                ValidateExtensions(property.Value, path, state);
            }
            else if (key.StartsWith('$'))
            {
                state.Add("DS_V05", path + "." + key, "unknown key in a token");
            }
        }

        if (!token.TryGetPropertyValue("$value", out JsonNode? value))
            return;

        if (value is not null && TryGetString(value, out string? text) && DesignDocument.TryParseAlias(text, out string? target))
        {
            ValidateAlias(path, type, target!, state);
        }
        else if (typeValid)
        {
            if (!IsCanonicalValue(type, value))
            {
                state.Add("DS_V09", path + ".$value", $"$value does not match the canonical form for {type}");
            }
            else if (type == "fontFamily" && value is JsonArray array)
            {
                // V-21: the array, count and per-entry lengths are already canonical; only the
                // character set is checked here.
                for (int i = 0; i < array.Count; i++)
                {
                    if (TryGetString(array[i], out string? entry) && HasDisallowedFontCharacter(entry!))
                        state.Add("DS_V21", $"{path}.$value[{i}]", "font family contains a disallowed character");
                }
            }
            else if (type == "typography" && value is JsonObject composite && composite["fontFamily"] is JsonArray compositeFamilies)
            {
                // V-21 applies to the font family inside a composite typography token too.
                for (int i = 0; i < compositeFamilies.Count; i++)
                {
                    if (TryGetString(compositeFamilies[i], out string? entry) && HasDisallowedFontCharacter(entry!))
                        state.Add("DS_V21", $"{path}.$value.fontFamily[{i}]", "font family contains a disallowed character");
                }
            }
        }
    }

    private static void ValidateAlias(string path, string type, string target, State state)
    {
        JsonObject? targetToken = DesignDocument.FindToken(state.Root, target);
        bool targetValid = targetToken is not null
            && targetToken.ContainsKey("$value")
            && TryGetString(targetToken["$type"], out string? targetType)
            && string.Equals(targetType, type, StringComparison.Ordinal);

        if (!targetValid)
        {
            state.Add("DS_V12", path, $"alias '{target}' is missing, is not a token, or has a different $type");
            return;
        }

        if (!DesignDocument.ResolveAlias(state.Root, path, out _))
            state.Add("DS_V12", path, $"alias '{target}' cycles or exceeds depth {DesignDocument.MaxAliasDepth}");
    }

    private static void ValidateDescription(JsonNode? node, string path, State state)
    {
        bool valid = node is not null
            && TryGetString(node, out string? text)
            && string.Equals(DesignText.Sanitize(text, MaxDescriptionLength), text, StringComparison.Ordinal);

        if (!valid)
            state.Add("DS_V10", path + ".$description", "$description must be safe, non-empty and at most 200 characters");
    }

    private static void ValidateExtensions(JsonNode? node, string path, State state)
    {
        string extensionsPath = path + ".$extensions";
        if (node is not JsonObject extensions)
        {
            state.Add("DS_V11", extensionsPath, "$extensions must be an object");
            return;
        }

        foreach (KeyValuePair<string, JsonNode?> property in extensions)
        {
            if (!string.Equals(property.Key, "com.reelbolt", StringComparison.Ordinal))
            {
                state.Add("DS_V11", extensionsPath + "." + property.Key, "unknown extension namespace");
                continue;
            }

            if (property.Value is not JsonObject reelbolt)
            {
                state.Add("DS_V11", extensionsPath + ".com.reelbolt", "com.reelbolt must be an object");
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> inner in reelbolt)
            {
                switch (inner.Key)
                {
                    case "prov":
                        ValidateProvenance(inner.Value, path, state);
                        break;
                    case "conf":
                        ValidateConfidence(inner.Value, path, state);
                        break;
                    case "locked":
                    case "orig":
                        break;
                    default:
                        state.Add("DS_V11", extensionsPath + ".com.reelbolt." + inner.Key, "unknown com.reelbolt key");
                        break;
                }
            }
        }
    }

    private static void ValidateConfidence(JsonNode? node, string path, State state)
    {
        if (!CanonicalJson.TryGetNumber(node, out double value) || value < 0 || value > 1)
            state.Add("DS_V13", path + ".$extensions.com.reelbolt.conf", "conf must be a number between 0 and 1");
    }

    private static void ValidateProvenance(JsonNode? node, string path, State state)
    {
        string provPath = path + ".$extensions.com.reelbolt.prov";
        if (node is not JsonObject prov)
        {
            state.Add("DS_V14", provPath, "prov must be an object");
            return;
        }

        if (!TryGetString(prov["method"], out string? method) || !ProvMethods.Contains(method!))
            state.Add("DS_V14", provPath + ".method", "prov.method must be imported, static, llm or manual");

        if (prov.ContainsKey("loc") && (!TryGetString(prov["loc"], out string? loc) || loc!.Length > 200))
            state.Add("DS_V14", provPath + ".loc", "prov.loc must be a string of at most 200 characters");

        if (prov.ContainsKey("importer") && (!TryGetString(prov["importer"], out string? importer) || importer!.Length > 40))
            state.Add("DS_V14", provPath + ".importer", "prov.importer must be a string of at most 40 characters");
    }

    private static void ValidateModes(JsonObject root, State state)
    {
        if (!root.TryGetPropertyValue("modes", out JsonNode? node))
            return;

        if (node is not JsonObject modes)
        {
            state.Add("DS_V17", "modes", "modes must be an object");
            return;
        }

        if (modes.Count > 6)
            state.Add("DS_V17", "modes", "at most 6 modes are allowed");

        foreach (KeyValuePair<string, JsonNode?> mode in modes)
        {
            string modePath = "modes." + mode.Key;
            if (!IsValidModeName(mode.Key))
                state.Add("DS_V17", modePath, "mode name must match the allowed form");

            if (mode.Value is not JsonObject overrides)
            {
                state.Add("DS_V17", modePath, "a mode must be an object of overrides");
                continue;
            }

            if (overrides.Count > 500)
                state.Add("DS_V17", modePath, "at most 500 overrides are allowed per mode");

            foreach (KeyValuePair<string, JsonNode?> over in overrides)
            {
                string overridePath = modePath + "." + over.Key;
                JsonObject? target = DesignDocument.FindToken(root, over.Key);
                if (target is null || !target.ContainsKey("$value"))
                {
                    state.Add("DS_V17", overridePath, "override path is not an existing token");
                    continue;
                }

                if (!TryGetString(target["$type"], out string? targetType) || !KnownTypes.Contains(targetType!))
                {
                    state.Add("DS_V17", overridePath, "target token has no valid $type");
                    continue;
                }

                if (over.Value is not null && TryGetString(over.Value, out string? aliasText) && DesignDocument.TryParseAlias(aliasText, out string? aliasTarget))
                {
                    JsonObject? aliasToken = DesignDocument.FindToken(root, aliasTarget!);
                    bool aliasValid = aliasToken is not null
                        && aliasToken.ContainsKey("$value")
                        && TryGetString(aliasToken["$type"], out string? aliasType)
                        && string.Equals(aliasType, targetType, StringComparison.Ordinal)
                        && DesignDocument.ResolveAlias(root, over.Key, out _);

                    if (!aliasValid)
                        state.Add("DS_V17", overridePath, "alias override does not resolve to the token's $type");
                }
                else if (!IsCanonicalValue(targetType!, over.Value))
                {
                    state.Add("DS_V17", overridePath, "override value does not match the token's $type");
                }
            }
        }
    }

    private static void ValidateVoice(JsonObject root, State state)
    {
        if (!root.TryGetPropertyValue("voice", out JsonNode? node))
            return;

        if (node is not JsonObject voice)
        {
            state.Add("DS_V18", "voice", "voice must be an object");
            return;
        }

        ValidateVoiceString(voice, "summary", ProseLimits.VoiceSummary, state);
        ValidateVoiceList(voice, "tone", 12, ProseLimits.VoiceTone, state);
        ValidateVoiceList(voice, "do", 12, ProseLimits.VoiceDo, state);
        ValidateVoiceList(voice, "dont", 12, ProseLimits.VoiceDont, state);
        ValidateVoiceList(voice, "taglines", 10, ProseLimits.VoiceTagline, state);
        ValidateVoiceList(voice, "vocabulary", 30, ProseLimits.VoiceVocabulary, state);
        ValidateVoiceLocale(voice, state);
    }

    private static void ValidateVoiceLocale(JsonObject voice, State state)
    {
        if (!voice.TryGetPropertyValue("locale", out JsonNode? node))
            return;

        // The default language of a run built in this brand (docs/run-brief.md): a BCP-47 tag only,
        // never a language name or free text, because it is quoted into agent prompts.
        if (!TryGetString(node, out string? text)
            || StyleLanguageSanitizer.PrimaryTag(StyleLanguageSanitizer.Sanitize(text)) is null)
            state.Add("DS_V18", "voice.locale", "locale must be a BCP-47 language tag such as it or pt-BR");
    }

    private static void ValidateVoiceString(JsonObject voice, string key, int maxLength, State state)
    {
        if (!voice.TryGetPropertyValue(key, out JsonNode? node))
            return;

        if (!TryGetString(node, out string? text) || text!.Length > maxLength)
            state.Add("DS_V18", "voice." + key, $"{key} must be a string of at most {maxLength} characters");
    }

    private static void ValidateVoiceList(JsonObject voice, string key, int maxItems, int maxLength, State state)
    {
        if (!voice.TryGetPropertyValue(key, out JsonNode? node))
            return;

        string path = "voice." + key;
        if (node is not JsonArray array)
        {
            state.Add("DS_V18", path, $"{key} must be an array");
            return;
        }

        if (array.Count > maxItems)
            state.Add("DS_V18", path, $"{key} allows at most {maxItems} entries");

        for (int i = 0; i < array.Count; i++)
        {
            if (!TryGetString(array[i], out string? text) || text!.Length > maxLength)
                state.Add("DS_V18", $"{path}[{i}]", $"{key} entries must be strings of at most {maxLength} characters");
        }
    }

    private static void ValidateAssets(JsonObject root, State state)
    {
        if (!root.TryGetPropertyValue("assets", out JsonNode? node))
            return;

        if (node is not JsonObject assets)
        {
            state.Add("DS_V15", "assets", "assets must be an object");
            return;
        }

        foreach (KeyValuePair<string, JsonNode?> asset in assets)
        {
            if (asset.Key is not ("logo" or "icon" or "preview"))
            {
                state.Add("DS_V15", "assets." + asset.Key, "only logo, icon and preview assets are allowed");
                continue;
            }

            ValidateAsset(asset.Value, "assets." + asset.Key, state);
        }
    }

    private static void ValidateAsset(JsonNode? node, string path, State state)
    {
        if (node is not JsonObject asset)
        {
            state.Add("DS_V15", path, "an asset must be an object");
            return;
        }

        if (asset.ContainsKey("fileRef"))
            ValidateFileRef(asset["fileRef"], path + ".fileRef", state);
        else
            state.Add("DS_V15", path, "an asset must carry a fileRef");

        // An optional display name of the base file. It is sanitized (no markup, control or hidden
        // runes) and bounded so a client-supplied name can never smuggle instructions or a header.
        if (asset.ContainsKey("name")
            && (!TryGetString(asset["name"], out string? assetName) || !IsSanitizedAssetName(assetName!)))
        {
            state.Add(
                "DS_V15",
                path + ".name",
                $"name must be a sanitized string of at most {MaxAssetNameLength} characters");
        }

        ValidateBrandMetadata(asset, path, state);

        if (!asset.TryGetPropertyValue("variants", out JsonNode? variantsNode))
            return;

        if (variantsNode is not JsonArray variants)
        {
            state.Add("DS_V15", path + ".variants", "variants must be an array");
            return;
        }

        if (variants.Count > 8)
            state.Add("DS_V15", path + ".variants", "at most 8 variants are allowed");

        for (int i = 0; i < variants.Count; i++)
        {
            string variantPath = $"{path}.variants[{i}]";
            if (variants[i] is not JsonObject variant)
            {
                state.Add("DS_V15", variantPath, "a variant must be an object");
                continue;
            }

            if (variant.ContainsKey("name") && (!TryGetString(variant["name"], out string? name) || name!.Length > 40))
                state.Add("DS_V15", variantPath + ".name", "variant name must be a string of at most 40 characters");

            if (variant.ContainsKey("fileRef"))
                ValidateFileRef(variant["fileRef"], variantPath + ".fileRef", state);
            else
                state.Add("DS_V15", variantPath, "a variant must carry a fileRef");

            ValidateBrandMetadata(variant, variantPath, state);
        }
    }

    /// <summary>
    /// The optional measured/classified logo description on a slot or a variant: file, subject, form,
    /// tone, readableOn and luminance. Every enum is closed; an unknown or malformed value is DS_V15.
    /// </summary>
    private static void ValidateBrandMetadata(JsonObject asset, string path, State state)
    {
        if (asset.ContainsKey("file")
            && (!TryGetString(asset["file"], out string? file) || !IsSanitizedAssetName(file!)))
        {
            state.Add("DS_V15", path + ".file", $"file must be a sanitized string of at most {MaxAssetNameLength} characters");
        }

        ValidateClosedEnum(asset, "subject", ["product", "publisher", "unknown"], path, state);
        ValidateClosedEnum(asset, "form", ["mark", "wordmark", "lockup"], path, state);
        ValidateClosedEnum(asset, "tone", ["dark", "light", "mid"], path, state);

        if (asset.ContainsKey("readableOn"))
        {
            bool ok = asset["readableOn"] is JsonArray array && array.Count <= 2;
            if (ok)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonNode? item in (JsonArray)asset["readableOn"]!)
                {
                    if (!TryGetString(item, out string? value) || value is not ("light" or "dark") || !seen.Add(value))
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (!ok)
                state.Add("DS_V15", path + ".readableOn", "readableOn must be an array of at most 2 distinct values from light, dark");
        }

        if (asset.ContainsKey("luminance"))
        {
            bool ok = false;
            if (asset["luminance"] is JsonValue value && value.TryGetValue(out double number))
                ok = double.IsFinite(number) && number >= 0 && number <= 1;
            if (!ok)
                state.Add("DS_V15", path + ".luminance", "luminance must be a number between 0 and 1");
        }
    }

    private static void ValidateClosedEnum(JsonObject asset, string key, string[] allowed, string path, State state)
    {
        if (!asset.ContainsKey(key))
            return;

        if (!TryGetString(asset[key], out string? value) || !allowed.Contains(value, StringComparer.Ordinal))
            state.Add("DS_V15", path + "." + key, $"{key} must be one of {string.Join(", ", allowed)}");
    }

    /// <summary>
    /// A base-asset name is valid when it is non-empty, bounded, trimmed and free of control,
    /// hidden (bidi/zero-width) runes, markup and the header-injection primitives.
    /// </summary>
    private static bool IsSanitizedAssetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > MaxAssetNameLength
            || !string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        foreach (Rune rune in name.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || DesignText.IsHiddenRune(rune))
                return false;

            if (rune.Value is '"' or '\\' or '/' or '<' or '>')
                return false;
        }

        return true;
    }

    private static void ValidateFileRef(JsonNode? node, string path, State state)
    {
        const string projectPrefix = "project-file:";
        const string libraryPrefix = "library-asset:";

        if (!TryGetString(node, out string? fileRef))
        {
            state.Add("DS_V15", path, "fileRef must be 'project-file:<guid>' or 'library-asset:<guid>'");
            return;
        }

        if (fileRef!.StartsWith(projectPrefix, StringComparison.Ordinal))
        {
            if (!Guid.TryParse(fileRef[projectPrefix.Length..], out Guid projectFileId)
                || !state.Context.ProjectFileExists(projectFileId))
            {
                state.Add("DS_V15", path, "fileRef does not name a file in this project");
            }

            return;
        }

        if (fileRef.StartsWith(libraryPrefix, StringComparison.Ordinal))
        {
            // A library asset is visible only when the host supplied a visibility check (the document
            // is being written to a library, or to a project linked to one) AND the id passes it.
            if (!Guid.TryParse(fileRef[libraryPrefix.Length..], out Guid assetId)
                || state.Context.LibraryAssetExists is null
                || !state.Context.LibraryAssetExists(assetId))
            {
                state.Add("DS_V15", path, "fileRef does not name an asset of this library");
            }

            return;
        }

        state.Add("DS_V15", path, "fileRef must be 'project-file:<guid>' or 'library-asset:<guid>'");
    }

    private static void ValidateFonts(JsonObject root, State state)
    {
        if (!root.TryGetPropertyValue("fonts", out JsonNode? node))
            return;

        if (node is not JsonObject fonts)
        {
            state.Add("DS_V19", "fonts", "fonts must be an object");
            return;
        }

        if (fonts.TryGetPropertyValue("licence", out JsonNode? licenceNode))
        {
            if (licenceNode is not JsonObject licence)
            {
                state.Add("DS_V19", "fonts.licence", "fonts.licence must be an object");
            }
            else
            {
                foreach (KeyValuePair<string, JsonNode?> entry in licence)
                {
                    if (!TryGetString(entry.Value, out string? value) || (value != "attested" && value != "unknown"))
                        state.Add("DS_V19", "fonts.licence." + entry.Key, "licence value must be attested or unknown");
                }
            }
        }

        if (fonts.TryGetPropertyValue("resolved", out JsonNode? resolvedNode))
        {
            if (resolvedNode is not JsonObject resolved)
            {
                state.Add("DS_V19", "fonts.resolved", "fonts.resolved must be an object");
            }
            else
            {
                foreach (KeyValuePair<string, JsonNode?> entry in resolved)
                {
                    if (!TryGetString(entry.Value, out string? value) || !state.Context.BundledFontNames.Contains(value!))
                        state.Add("DS_V19", "fonts.resolved." + entry.Key, "resolved font must be a bundled font name");
                }
            }
        }

        if (fonts.TryGetPropertyValue("files", out JsonNode? filesNode))
        {
            if (filesNode is not JsonObject files)
            {
                state.Add("DS_V19", "fonts.files", "fonts.files must be an object");
                return;
            }

            if (files.Count > MaxFontFiles)
                state.Add("DS_V19", "fonts.files", $"at most {MaxFontFiles} font files are allowed");

            foreach (KeyValuePair<string, JsonNode?> entry in files)
            {
                string path = "fonts.files." + entry.Key;
                if (!IsSanitizedFontFamilyKey(entry.Key))
                {
                    state.Add(
                        "DS_V19",
                        path,
                        $"font family key must be sanitized and at most {MaxFontKeyLength} characters");
                }

                ValidateFontFileRef(entry.Value, path, state);
            }
        }
    }

    /// <summary>
    /// A <c>fonts.files</c> key is a plausible font family: bounded, already trimmed and unchanged
    /// by <see cref="DesignText.Sanitize"/>, so a client cannot smuggle markup or hidden runes.
    /// </summary>
    private static bool IsSanitizedFontFamilyKey(string key) =>
        key.Length > 0
        && key.Length <= MaxFontKeyLength
        && DesignText.IsSafeFontFamily(key)
        && string.Equals(DesignText.Sanitize(key, MaxFontKeyLength), key, StringComparison.Ordinal);

    private static void ValidateFontFileRef(JsonNode? node, string path, State state)
    {
        const string prefix = "project-file:";
        if (!TryGetString(node, out string? fileRef) || !fileRef!.StartsWith(prefix, StringComparison.Ordinal))
        {
            state.Add("DS_V19", path, "font file must be 'project-file:<guid>'");
            return;
        }

        string raw = fileRef[prefix.Length..];
        if (!Guid.TryParse(raw, out Guid id) || !state.Context.ProjectFileExists(id))
            state.Add("DS_V19", path, "font file does not name a file in this project");
    }

    private static void WalkStrings(JsonNode? node, string path, int depth, State state)
    {
        if (node is null || depth > CanonicalJson.MaxDepth)
            return;

        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    string childPath = path.Length == 0 ? property.Key : path + "." + property.Key;
                    WalkStrings(property.Value, childPath, depth + 1, state);
                }

                break;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                    WalkStrings(array[i], $"{path}[{i}]", depth + 1, state);
                break;

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                string text = value.GetValue<string>();
                if (HasControlCharacter(text))
                    state.Add("DS_V20", path, "string contains a control or bidi character");
                if (IsUrlLike(text))
                    state.Add("DS_V16", path, "string looks like a URL or an executable scheme");
                break;
        }
    }

    private static bool IsCanonicalValue(string type, JsonNode? value) => type switch
    {
        "color" => IsColorValue(value),
        "dimension" => IsDimensionValue(value),
        "fontFamily" => IsFontFamilyValue(value),
        "fontWeight" => IsIntegerInRange(value, 1, 1000),
        "duration" => IsDurationValue(value),
        "cubicBezier" => IsCubicBezierValue(value),
        "number" => CanonicalJson.TryGetNumber(value, out double number) && Math.Abs(number) <= 1e6,
        "shadow" => IsShadowValue(value),
        "typography" => IsTypographyValue(value),
        "keyword" => IsKeywordValue(value),
        _ => false,
    };

    private static bool IsKeywordValue(JsonNode? value)
    {
        if (!TryGetString(value, out string? text) || text is null || text.Length is < 1 or > 40)
            return false;

        if (text[0] < 'a' || text[0] > 'z')
            return false;

        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
                return false;
        }

        return true;
    }

    private static bool IsColorValue(JsonNode? value)
    {
        if (value is not JsonObject color)
            return false;

        if (!TryGetString(color["hex"], out string? hex) || !IsLowerHexColor(hex!))
            return false;

        return TryGetString(color["colorSpace"], out string? colorSpace)
            && string.Equals(colorSpace, "srgb", StringComparison.Ordinal);
    }

    private static bool IsDimensionValue(JsonNode? value)
    {
        if (value is not JsonObject dimension)
            return false;

        if (!CanonicalJson.TryGetNumber(dimension["value"], out double number) || Math.Abs(number) > 10000)
            return false;

        return TryGetString(dimension["unit"], out string? unit)
            && unit is "px" or "rem" or "em" or "%";
    }

    private static bool IsFontFamilyValue(JsonNode? value)
    {
        if (value is not JsonArray families || families.Count < 1 || families.Count > 5)
            return false;

        for (int i = 0; i < families.Count; i++)
        {
            if (!TryGetString(families[i], out string? family) || family!.Length < 1 || family.Length > 80)
                return false;
        }

        return true;
    }

    private static bool IsIntegerInRange(JsonNode? value, double min, double max) =>
        CanonicalJson.TryGetNumber(value, out double number)
        && number == Math.Floor(number)
        && number >= min
        && number <= max;

    private static bool IsDurationValue(JsonNode? value)
    {
        if (value is not JsonObject duration)
            return false;

        if (!CanonicalJson.TryGetNumber(duration["value"], out double number) || number < 0)
            return false;

        if (!TryGetString(duration["unit"], out string? unit))
            return false;

        return unit switch
        {
            "ms" => number <= 60000,
            "s" => number <= 60,
            _ => false,
        };
    }

    private static bool IsCubicBezierValue(JsonNode? value)
    {
        if (value is not JsonArray points || points.Count != 4)
            return false;

        var numbers = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!CanonicalJson.TryGetNumber(points[i], out numbers[i]))
                return false;
        }

        return numbers[0] >= 0 && numbers[0] <= 1 && numbers[2] >= 0 && numbers[2] <= 1;
    }

    private static bool IsShadowValue(JsonNode? value)
    {
        if (value is JsonObject single)
            return IsShadowEntry(single);

        if (value is not JsonArray array || array.Count > 4)
            return false;

        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject entry || !IsShadowEntry(entry))
                return false;
        }

        return true;
    }

    private static bool IsShadowEntry(JsonObject entry)
    {
        if (!IsColorValue(entry["color"]))
            return false;

        foreach (string key in ShadowDimensionKeys)
        {
            if (entry.ContainsKey(key) && !IsDimensionValue(entry[key]))
                return false;
        }

        if (entry.ContainsKey("inset") && !IsBool(entry["inset"]))
            return false;

        return true;
    }

    private static bool IsTypographyValue(JsonNode? value)
    {
        if (value is not JsonObject typography)
            return false;

        if (typography.ContainsKey("fontFamily") && !IsFontFamilyValue(typography["fontFamily"]))
            return false;
        if (typography.ContainsKey("fontSize") && !IsDimensionValue(typography["fontSize"]))
            return false;
        if (typography.ContainsKey("fontWeight") && !IsIntegerInRange(typography["fontWeight"], 1, 1000))
            return false;
        if (typography.ContainsKey("lineHeight") && !IsLineHeightValue(typography["lineHeight"]))
            return false;
        if (typography.ContainsKey("letterSpacing") && !IsDimensionValue(typography["letterSpacing"]))
            return false;

        return true;
    }

    private static bool IsLineHeightValue(JsonNode? value) =>
        (CanonicalJson.TryGetNumber(value, out double number) && Math.Abs(number) <= 1e6) || IsDimensionValue(value);

    private static bool IsLowerHexColor(string hex)
    {
        if (hex.Length != 7 && hex.Length != 9)
            return false;
        if (hex[0] != '#')
            return false;

        for (int i = 1; i < hex.Length; i++)
        {
            char c = hex[i];
            bool allowed = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!allowed)
                return false;
        }

        return true;
    }

    private static bool HasDisallowedFontCharacter(string name)
    {
        foreach (char c in name)
        {
            bool allowed = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c is ' ' or '_' or '.' or ',' or '\'' or '&' or '+' or '(' or ')' or '-';

            if (!allowed)
                return true;
        }

        return false;
    }

    private static bool HasControlCharacter(string text)
    {
        // By Rune, so surrogate-encoded tag characters and variation selectors are seen as what they are.
        foreach (Rune rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            if (c <= 0x1F || c == 0x7F)
                return true;
            if (c >= 0x202A && c <= 0x202E)
                return true;
            if (c >= 0x2066 && c <= 0x2069)
                return true;
            if (c is >= 0xE0000 and <= 0xE007F)
                return true;
            if (c is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF)
                return true;
        }

        return false;
    }

    private static bool IsUrlLike(string text)
    {
        int colon = text.IndexOf(':');
        if (colon <= 0)
            return false;

        char first = text[0];
        if (first < 'a' || first > 'z')
            return false;

        for (int i = 1; i < colon; i++)
        {
            char c = text[i];
            bool schemeChar = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '+' or '.' or '-';
            if (!schemeChar)
                return false;
        }

        string afterColon = text[(colon + 1)..];
        if (afterColon.StartsWith("//", StringComparison.Ordinal))
            return true;

        string scheme = text[..colon];
        return string.Equals(scheme, "data", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length < 1 || key.Length > 48)
            return false;

        char first = key[0];
        if (!((first >= 'a' && first <= 'z') || (first >= '0' && first <= '9')))
            return false;

        for (int i = 1; i < key.Length; i++)
        {
            char c = key[i];
            bool allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '_' or '-';
            if (!allowed)
                return false;
        }

        return true;
    }

    private static bool IsValidModeName(string name)
    {
        if (name.Length < 1 || name.Length > 24)
            return false;
        if (name[0] < 'a' || name[0] > 'z')
            return false;

        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            bool allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!allowed)
                return false;
        }

        return true;
    }

    private static bool IsBool(JsonNode? node) =>
        node is JsonValue value && (value.GetValueKind() == JsonValueKind.True || value.GetValueKind() == JsonValueKind.False);

    private static bool TryGetString(JsonNode? node, out string? value)
    {
        if (node is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String)
        {
            value = jsonValue.GetValue<string>();
            return true;
        }

        value = null;
        return false;
    }

    private sealed class State(JsonObject root, DesignValidationContext context)
    {
        public JsonObject Root { get; } = root;

        public DesignValidationContext Context { get; } = context;

        public List<DesignViolation> Violations { get; } = [];

        public HashSet<string> TokenPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int TokenCount { get; set; }

        public void Add(string code, string path, string message) =>
            Violations.Add(new DesignViolation(code, path, message));
    }
}
