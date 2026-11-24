using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Reads the legacy analyzer outputs (<c>CodeStructureOutput</c>, <c>DependencyAnalysisOutput</c>,
/// <c>ComponentInventoryOutput</c>, <c>RouteAndApiOutput</c>, <c>StyleAndThemeOutput</c>) leniently.
///
/// <para>
/// Their stored output schema is prompt documentation, not enforcement, so a model occasionally
/// emits a string list in a richer shape — production run <c>5761becc</c> returned
/// <c>entryPoints</c> as <c>{path,type,description}</c> objects while
/// <c>CodeStructureOutput.EntryPoints</c> is <c>List&lt;string&gt;</c>. Strict deserialization threw
/// and nulled the whole record, failing Compose. The tolerance is confined to <c>List&lt;string&gt;</c>
/// members: every other member, and the design-token sanitizer that reads the style extractor, are
/// unchanged.
/// </para>
/// </summary>
internal static class AnalyzerOutputJson
{
    /// <summary>The tolerant reader options, shared with the structured-output validator so it never rejects what this accepts.</summary>
    internal static JsonSerializerOptions ReaderOptions => Options;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter(), new TolerantStringListConverter(), new TolerantObjectListConverter(),
            new TolerantStringConverter(), new TolerantInt32Converter(), new TolerantBooleanConverter()
        }
    };

    /// <summary>
    /// Deserializes an analyzer step output, extracting the first balanced JSON object from any
    /// surrounding prose. Returns null when the text carries no readable object, exactly as before.
    /// </summary>
    public static T? Deserialize<T>(string? output) where T : class
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            string json = RobustJsonExtractor.ExtractJsonObject(output) ?? output;
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Reads a <c>List&lt;string&gt;</c> whose entries drifted out of schema. A string is kept verbatim;
/// an object contributes the first non-empty string among <c>path</c>, <c>name</c>, <c>value</c>
/// and <c>id</c>; anything else is skipped. A single string becomes a one-entry list, and any other
/// shape an empty list (a literal <c>null</c> is left to the framework, which sets the property to
/// null). Reading an entry never throws; a malformed document still makes
/// <see cref="AnalyzerOutputJson.Deserialize{T}"/> return null, exactly as strict parsing did.
/// </summary>
internal sealed class TolerantStringListConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(List<string>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        new StringListConverter();

    private sealed class StringListConverter : JsonConverter<List<string>>
    {
        public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return [reader.GetString()!];

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                return [];
            }

            var values = new List<string>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.EndArray:
                        return values;
                    case JsonTokenType.String:
                        values.Add(reader.GetString()!);
                        break;
                    case JsonTokenType.StartObject:
                        string? entry = ReadObjectEntry(ref reader);
                        if (entry is not null)
                            values.Add(entry);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            return values;
        }

        public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();
            foreach (string item in value)
                writer.WriteStringValue(item);
            writer.WriteEndArray();
        }

        private static string? ReadObjectEntry(ref Utf8JsonReader reader)
        {
            string? path = null;
            string? name = null;
            string? value = null;
            string? id = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    reader.Skip();
                    continue;
                }

                string property = reader.GetString() ?? string.Empty;
                reader.Read();
                string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();

                if (string.Equals(property, "path", StringComparison.OrdinalIgnoreCase))
                    path = FirstNonEmpty(path, text);
                else if (string.Equals(property, "name", StringComparison.OrdinalIgnoreCase))
                    name = FirstNonEmpty(name, text);
                else if (string.Equals(property, "value", StringComparison.OrdinalIgnoreCase))
                    value = FirstNonEmpty(value, text);
                else if (string.Equals(property, "id", StringComparison.OrdinalIgnoreCase))
                    id = FirstNonEmpty(id, text);
            }

            foreach (string? candidate in new[] { path, name, value, id })
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate;
            }

            return null;
        }

        private static string? FirstNonEmpty(string? current, string? candidate) =>
            string.IsNullOrWhiteSpace(current) ? candidate : current;
    }
}

/// <summary>
/// Reads a <c>List&lt;T&gt;</c> of objects whose entries drifted: an entry that is a bare string becomes an
/// object whose first of Name/Path/Type is that string, an entry that fails to read is skipped, and a
/// non-array value is an empty list. Never throws on shape, so one bad entry cannot null the whole
/// analyzer record (prod "step N did not yield a ..." failures of 2026-10-21).
/// </summary>
internal sealed class TolerantObjectListConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(List<>)
        && typeToConvert.GetGenericArguments()[0] is { IsClass: true } item && item != typeof(string);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Impl<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class Impl<T> : JsonConverter<List<T>> where T : class
    {
        public override List<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            var result = new List<T>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (JsonElement element in doc.RootElement.EnumerateArray())
            {
                try
                {
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        if (element.Deserialize<T>(options) is { } value)
                            result.Add(value);
                    }
                    else if (element.ValueKind == JsonValueKind.String && FromString(element.GetString()) is { } fromText)
                    {
                        result.Add(fromText);
                    }
                }
                catch (JsonException)
                {
                    // A single unreadable entry is dropped; the rest of the record is kept.
                }
            }

            return result;
        }

        private static T? FromString(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            foreach (string name in new[] { "Name", "Path", "Type" })
            {
                System.Reflection.PropertyInfo? property = typeof(T).GetProperty(name);
                if (property is { CanWrite: true } && property.PropertyType == typeof(string)
                    && typeof(T).GetConstructor(Type.EmptyTypes) is { } ctor)
                {
                    var instance = (T)ctor.Invoke(null);
                    property.SetValue(instance, text);
                    return instance;
                }
            }

            return null;
        }

        public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (T item in value)
                JsonSerializer.Serialize(writer, item, options);
            writer.WriteEndArray();
        }
    }
}

/// <summary>A string member that arrives as a number or boolean keeps its text; an object or array becomes empty.</summary>
internal sealed class TolerantStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return Encoding.UTF8.GetString(reader.ValueSpan);
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            default:
                reader.Skip();
                return string.Empty;
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>An int member that arrives as numeric text keeps its value; anything unreadable is 0.</summary>
internal sealed class TolerantInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out int i))
                    return i;
                return reader.TryGetDouble(out double d) ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : 0;
            case JsonTokenType.String:
                string? text = reader.GetString();
                return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : 0;
            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>A bool member that arrives as text keeps its value; anything unreadable is false.</summary>
internal sealed class TolerantBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.String:
                return bool.TryParse(reader.GetString(), out bool b) && b;
            default:
                reader.Skip();
                return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}
