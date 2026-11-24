using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

/// <summary>How an input reaches a setting: inside a string (<c>{{input.id}}</c>) or as the whole value (<c>appInputs</c>).</summary>
public enum AppBindingMode
{
    /// <summary>A <c>{{input.id}}</c> placeholder inside an allowlisted string setting (text inputs).</summary>
    Placeholder,

    /// <summary>A key of the step's <c>appInputs</c> map: the input's value replaces the setting.</summary>
    Value
}

/// <summary>What kind of JSON value a setting holds (what a mapped binding may write into it, docs/design/app-inputs.md 27).</summary>
public enum AppBindableValueKind
{
    /// <summary>A string (text, a choice word, a shape, a model id).</summary>
    Text,

    /// <summary>A number.</summary>
    Number,

    /// <summary>true or false.</summary>
    Boolean
}

/// <summary>
/// One setting an app input may bind (docs/design/app-inputs.md 7). <see cref="Path"/> is the camelCase JSON name, a
/// nested one written <c>trackObjects[].label</c> (an <c>appInputs</c> key names the item: <c>regionEffects[0].effect</c>),
/// or a pseudo-key starting with <c>@</c>. <see cref="ValueKind"/> (appended) is the JSON kind the setting holds.
/// </summary>
public sealed record AppBindableField(
    StepType StepType,
    string Path,
    AppBindingMode Mode,
    IReadOnlyList<AppInputType> Types,
    string Label,
    double? Min = null,
    double? Max = null,
    bool Integer = false,
    int? MaxLength = null,
    IReadOnlyList<string>? Choices = null,
    double? ToggleOn = null,
    double? ToggleOff = null,
    string? GovernedByBriefField = null,
    IReadOnlyList<string>? Capabilities = null,
    AppBindableValueKind ValueKind = AppBindableValueKind.Text,
    bool SpendCap = false)
{
    /// <summary>True for a pseudo-key (<c>@model</c>, <c>@stillModel</c>, <c>@spokenText</c>) the binder handles itself rather than a config property.</summary>
    public bool IsPseudo => Path.StartsWith('@');

    /// <summary>True for a pseudo-key a <c>model</c> input binds (<c>@model</c>, <c>@stillModel</c>).</summary>
    public bool IsModel => IsPseudo && Capabilities is not null;

    /// <summary>True when the path names a member inside an array item or an object (<c>regionEffects[].effect</c>).</summary>
    public bool IsNested => !IsPseudo && (Path.Contains('.') || Path.Contains('['));
}

/// <summary>
/// The allowlist of settable settings, reflected from <see cref="AppBindableAttribute"/> on the config records plus the
/// pseudo-key <c>@model</c> (docs/design/app-inputs.md 7.1, 8.1). The single vocabulary of the binder: nothing that is
/// not listed here can be bound by anyone, the assistant included.
/// </summary>
public static class AppBindableCatalog
{
    /// <summary>The pseudo-key of a model binding.</summary>
    public const string ModelKey = "@model";

    /// <summary>
    /// The pseudo-key of a talking head's still model (docs/design/app-inputs.md 8.1, 27): an <c>ImageEdit</c> model that
    /// makes the picture a talking-head model animates, written to the step's <c>stillProviderId</c>.
    /// </summary>
    public const string StillModelKey = "@stillModel";

    /// <summary>
    /// The pseudo-key of a Voiceover step's own words (docs/design/app-inputs.md 27): a text input whose paragraphs become
    /// the step's <c>Inline</c> lines, spoken in order from 0 s (<see cref="Apps.VoiceoverSpokenText"/>).
    /// </summary>
    public const string SpokenTextKey = "@spokenText";

    /// <summary>
    /// The pseudo-key of a VideoAnalyze step's picture model: a <c>Vision</c> model that writes the step's
    /// <c>visionProviderId</c>, the one model behind everything that step asks about the picture (shot captions, subject
    /// location, looks at events).
    /// </summary>
    public const string VisionModelKey = "@visionModel";

    /// <summary>The step types a <c>model</c> input may bind through <c>@model</c>, and the capabilities it may choose from.</summary>
    public static readonly IReadOnlyDictionary<StepType, IReadOnlyList<string>> ModelCapabilitiesByStep = new Dictionary<StepType, IReadOnlyList<string>>
    {
        [StepType.VideoGenerate] = ["VideoGeneration"],
        [StepType.AudioGenerate] = ["MusicGeneration", "SoundEffectGeneration"],
        [StepType.Voiceover] = ["SpeechSynthesis"]
    };

    private static readonly ConcurrentDictionary<StepType, IReadOnlyList<AppBindableField>> Cache = new();

    /// <summary>The config record type of a step type (<c>&lt;StepType&gt;StepConfig</c> in this namespace), or null.</summary>
    public static Type? ConfigTypeOf(StepType stepType) =>
        typeof(AppBindableCatalog).Assembly.GetType($"{typeof(AppBindableCatalog).Namespace}.{stepType}StepConfig");

    /// <summary>Every bindable field of a step type, in declaration order, pseudo-keys last.</summary>
    public static IReadOnlyList<AppBindableField> For(StepType stepType) => Cache.GetOrAdd(stepType, Build);

    /// <summary>The bindable field at <paramref name="path"/> (array indexes written <c>[]</c>), or null.</summary>
    public static AppBindableField? Find(StepType stepType, string path) =>
        For(stepType).FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every bindable field of every step type.</summary>
    public static IReadOnlyList<AppBindableField> All() => [.. Enum.GetValues<StepType>().SelectMany(For)];

    /// <summary>
    /// The properties of <paramref name="configType"/> reachable from its JSON (camelCase name to property), the same
    /// members the save validator treats as known.
    /// </summary>
    internal static IReadOnlyDictionary<string, PropertyInfo> JsonMembers(Type configType)
    {
        Dictionary<string, PropertyInfo> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo p in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
                continue;
            string name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            map.TryAdd(name, p);
        }

        return map;
    }

    /// <summary>The element type of a list/array property, or the type itself; unwraps nullables.</summary>
    internal static Type ElementOrSelf(Type type, out bool isCollection)
    {
        isCollection = false;
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string))
            return t;
        if (t.IsArray)
        {
            isCollection = true;
            return t.GetElementType()!;
        }

        if (t.IsGenericType && t.GetGenericArguments().Length == 1 && typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
        {
            isCollection = true;
            Type arg = t.GetGenericArguments()[0];
            return Nullable.GetUnderlyingType(arg) ?? arg;
        }

        return t;
    }

    private static IReadOnlyList<AppBindableField> Build(StepType stepType)
    {
        List<AppBindableField> fields = [];
        if (ConfigTypeOf(stepType) is { } configType)
            Walk(stepType, configType, prefix: string.Empty, fields, depth: 0);

        if (ModelCapabilitiesByStep.TryGetValue(stepType, out IReadOnlyList<string>? capabilities))
            fields.Add(new AppBindableField(stepType, ModelKey, AppBindingMode.Value, [AppInputType.Model], "Model", Capabilities: capabilities));
        if (stepType == StepType.VideoGenerate)
            fields.Add(new AppBindableField(stepType, StillModelKey, AppBindingMode.Value, [AppInputType.Model], "Picture model", Capabilities: ["ImageEdit"]));
        if (stepType == StepType.VideoAnalyze)
            fields.Add(new AppBindableField(stepType, VisionModelKey, AppBindingMode.Value, [AppInputType.Model], "Picture model", Capabilities: ["Vision"]));
        if (stepType == StepType.Voiceover)
        {
            fields.Add(new AppBindableField(stepType, SpokenTextKey, AppBindingMode.Value, [AppInputType.ShortText, AppInputType.LongText],
                "Words to speak", MaxLength: Apps.VoiceoverSpokenText.MaxTotalChars));
        }

        return fields;
    }

    private static void Walk(StepType stepType, Type type, string prefix, List<AppBindableField> fields, int depth)
    {
        if (depth > 4)
            return;
        foreach ((string name, PropertyInfo property) in JsonMembers(type))
        {
            string path = prefix + name;
            if (property.GetCustomAttribute<AppBindableAttribute>() is { } attr)
            {
                fields.Add(ToField(stepType, path, attr, property));
                continue;
            }

            Type element = ElementOrSelf(property.PropertyType, out bool isCollection);
            if (IsRecordLike(element))
                Walk(stepType, element, path + (isCollection ? "[]." : "."), fields, depth + 1);
        }
    }

    private static bool IsRecordLike(Type t) =>
        t.IsClass && t != typeof(string) && t.Namespace == typeof(AppBindableCatalog).Namespace;

    private static AppBindableField ToField(StepType stepType, string path, AppBindableAttribute a, PropertyInfo property)
    {
        bool text = a.Types.Length > 0 && a.Types.All(AppInputTypes.IsText);
        if (a.Types.Length == 0 || (!text && a.Types.Any(AppInputTypes.IsText)))
            throw new InvalidOperationException($"[AppBindable] on {property.DeclaringType?.Name}.{property.Name} mixes text and non-text types or names none.");

        IReadOnlyList<string>? choices = a.Choices;
        Type underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (choices is null && underlying.IsEnum)
            choices = Enum.GetNames(underlying);

        return new AppBindableField(
            stepType,
            path,
            text ? AppBindingMode.Placeholder : AppBindingMode.Value,
            a.Types,
            a.Label,
            double.IsNaN(a.Min) ? null : a.Min,
            double.IsNaN(a.Max) ? null : a.Max,
            a.Integer || underlying == typeof(int) || underlying == typeof(long),
            text ? (a.MaxLength > 0 ? a.MaxLength : AppInputTypes.MaxLongText) : null,
            choices,
            double.IsNaN(a.ToggleOn) ? null : a.ToggleOn,
            double.IsNaN(a.ToggleOn) ? null : a.ToggleOff,
            a.GovernedByBriefField,
            ValueKind: underlying == typeof(bool) ? AppBindableValueKind.Boolean
                : underlying.IsPrimitive || underlying == typeof(decimal) ? AppBindableValueKind.Number
                : AppBindableValueKind.Text,
            SpendCap: a.SpendCap);
    }
}
