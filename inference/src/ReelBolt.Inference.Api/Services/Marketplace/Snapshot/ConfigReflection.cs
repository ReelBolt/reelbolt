using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

/// <summary>One settable, serialised property of a step config type.</summary>
internal sealed record ConfigProperty(
    string JsonName,
    PropertyInfo Info,
    Type ValueType,
    bool IsCollection,
    bool IsDictionary,
    Type? KeyType)
{
    /// <summary>Catalog key: <c>DeclaringType.Property</c>.</summary>
    public string RuleKey => $"{Info.DeclaringType!.Name}.{Info.Name}";

    public bool IsComplex => ConfigReflection.IsComplex(ValueType);

    /// <summary>Numbers, bools and enums (alone or in a list or map): never an id and never text.</summary>
    public bool IsPrimitive => ConfigReflection.IsPrimitive(ValueType);
}

internal static class ConfigReflection
{
    private static readonly Dictionary<Type, IReadOnlyList<ConfigProperty>> Cache = [];
    private static readonly object Gate = new();

    public static IReadOnlyList<ConfigProperty> PropertiesOf(Type type)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(type, out IReadOnlyList<ConfigProperty>? cached))
                return cached;
        }

        List<ConfigProperty> list = [];
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.GetMethod is not { IsPublic: true } || p.SetMethod is not { IsPublic: true })
                continue;
            if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
                continue;

            string name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            (Type valueType, bool isCollection, bool isDictionary, Type? keyType) = Unwrap(p.PropertyType);
            list.Add(new ConfigProperty(name, p, valueType, isCollection, isDictionary, keyType));
        }

        lock (Gate)
        {
            Cache[type] = list;
        }

        return list;
    }

    public static ConfigProperty? Find(Type type, string jsonName) =>
        PropertiesOf(type).FirstOrDefault(p => string.Equals(p.JsonName, jsonName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The element type of a list or map value, or the type itself; <c>Nullable&lt;T&gt;</c> is unwrapped.</summary>
    public static (Type Value, bool IsCollection, bool IsDictionary, Type? KeyType) Unwrap(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string))
            return (type, false, false, null);

        Type? dict = new[] { type }.Concat(type.GetInterfaces()).FirstOrDefault(i =>
            i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IDictionary<,>)));
        if (dict is not null)
        {
            Type[] args = dict.GetGenericArguments();
            return (Nullable.GetUnderlyingType(args[1]) ?? args[1], false, true, args[0]);
        }

        if (type.IsArray)
            return (Nullable.GetUnderlyingType(type.GetElementType()!) ?? type.GetElementType()!, true, false, null);

        if (typeof(IEnumerable).IsAssignableFrom(type) && type.IsGenericType)
        {
            Type element = type.GetGenericArguments()[0];
            return (Nullable.GetUnderlyingType(element) ?? element, true, false, null);
        }

        return (type, false, false, null);
    }

    public static bool IsPrimitive(Type t) =>
        t == typeof(bool) || t.IsEnum || t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
        || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
        || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    /// <summary>A record or class of this product (descended into, never classified as a leaf).</summary>
    public static bool IsComplex(Type t) =>
        !IsPrimitive(t) && t != typeof(string) && t != typeof(Guid) && (t.Namespace?.StartsWith("ReelBolt.", StringComparison.Ordinal) ?? false);

    /// <summary>Every complex type reachable from <paramref name="roots"/> (the roots included).</summary>
    public static IReadOnlyList<Type> Reachable(IEnumerable<Type> roots)
    {
        List<Type> seen = [];
        Stack<Type> todo = new(roots);
        while (todo.Count > 0)
        {
            Type t = todo.Pop();
            if (seen.Contains(t))
                continue;
            seen.Add(t);
            foreach (ConfigProperty p in PropertiesOf(t))
            {
                if (p.IsComplex)
                    todo.Push(p.ValueType);
            }
        }

        return seen;
    }
}
