using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// The outcome of <see cref="SvgSanitizer.Sanitize"/>: on success <see cref="Sanitized"/> is a new,
/// inert SVG document; on failure <see cref="Code"/> is one of the sanitizer's stable codes and
/// <see cref="Sanitized"/> is null.
/// </summary>
public sealed record SvgSanitizeResult(
    bool Ok, string? Sanitized, string? Code, string? Message,
    int ElementsDropped, int AttributesDropped);

/// <summary>
/// Rebuilds an SVG from an allowlist of elements and attributes, so an imported mark is inert by
/// construction: nothing from the input survives except validated attribute values and text, which
/// the writer escapes. DTDs are prohibited, entities are never expanded, external references are
/// never resolved and no URL is fetched. Every bound (input size, element count, depth, attribute
/// value length, output size) is enforced here rather than at the call site.
/// </summary>
public static class SvgSanitizer
{
    public const int MaxInputBytes = 512 * 1024;
    public const int MaxOutputBytes = 512 * 1024;
    public const int MaxElements = 10000;
    public const int MaxDepth = 64;
    public const string InvalidCode = "SVG_INVALID";
    public const string TooLargeCode = "SVG_TOO_LARGE";
    public const string EmptyCode = "SVG_EMPTY";

    private const int MaxAttributeLength = 4096;
    private const int MaxIdLength = 128;
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";
    private const string LocalUrlPrefix = "url(#";

    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "title", "desc", "defs", "linearGradient", "radialGradient", "stop",
        "clipPath", "mask", "symbol", "path", "rect", "circle", "ellipse", "line", "polyline",
        "polygon", "use", "text", "tspan",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "viewBox", "width", "height", "x", "y", "x1", "x2", "y1", "y2", "cx", "cy", "r", "rx",
        "ry", "d", "points", "fill", "fill-rule", "fill-opacity", "stroke", "stroke-width",
        "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray",
        "stroke-dashoffset", "stroke-opacity", "opacity", "transform", "offset", "stop-color",
        "stop-opacity", "gradientUnits", "gradientTransform", "spreadMethod", "clipPathUnits",
        "maskUnits", "maskContentUnits", "id", "vector-effect", "paint-order", "font-family",
        "font-size", "font-weight", "text-anchor", "href",
    };

    private static readonly HashSet<string> TextElements = new(StringComparer.Ordinal)
    {
        "title", "desc", "text", "tspan",
    };

    private static readonly HashSet<string> DrawableElements = new(StringComparer.Ordinal)
    {
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "use",
    };

    /// <summary>
    /// Parses <paramref name="input"/> as an inert XML document and rebuilds it from the allowlist.
    /// Never throws: malformed, oversized, too deep or empty input is reported in the result.
    /// </summary>
    public static SvgSanitizeResult Sanitize(ReadOnlySpan<byte> input)
    {
        if (input.Length > MaxInputBytes)
            return Failure(TooLargeCode, "The SVG is larger than the supported limit.");
        if (input.Length == 0)
            return Failure(EmptyCode, "The SVG is empty.");

        var state = new State();
        OutElement root;
        try
        {
            root = Parse(input.ToArray(), state);
            ResolveReferences(root, state);
        }
        catch (SvgInvalidException ex)
        {
            return Failure(InvalidCode, ex.Message, state);
        }
        catch (XmlException)
        {
            return Failure(InvalidCode, "The SVG is not well-formed XML.", state);
        }

        if (!HasDrawable(root))
            return Failure(EmptyCode, "The SVG has no drawable element.", state);

        string sanitized;
        try
        {
            sanitized = Write(root);
        }
        catch (XmlException)
        {
            return Failure(InvalidCode, "The SVG could not be rebuilt.", state);
        }

        if (Encoding.UTF8.GetByteCount(sanitized) > MaxOutputBytes)
            return Failure(InvalidCode, "The sanitized SVG exceeds the output limit.", state);

        return new SvgSanitizeResult(
            true, sanitized, null, null, state.ElementsDropped, state.AttributesDropped);
    }

    private static OutElement Parse(byte[] bytes, State state)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxInputBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CheckCharacters = true,
            CloseInput = true,
        };

        using var stream = new MemoryStream(bytes, writable: false);
        using XmlReader reader = XmlReader.Create(stream, settings);
        reader.MoveToContent();

        if (reader.NodeType != XmlNodeType.Element ||
            reader.LocalName != "svg" ||
            !IsSvgNamespace(reader.NamespaceURI))
            throw new SvgInvalidException("The root element is not an SVG element.");

        OutElement? root = ReadElement(reader, 0, state);
        if (root is null)
            throw new SvgInvalidException("The SVG root element was dropped.");

        // Reading to EOF makes the reader validate the remainder of the document (trailing junk,
        // a second root, an unterminated element) rather than trusting the first subtree alone.
        while (reader.Read())
        {
        }

        return root;
    }

    /// <summary>
    /// Reads the element the reader is positioned on. On return the reader sits on the matching
    /// end tag (or on the element itself when it is empty), so the caller's next <c>Read</c> is the
    /// next sibling.
    /// </summary>
    private static OutElement? ReadElement(XmlReader reader, int depth, State state)
    {
        string name = reader.LocalName;
        state.Elements++;
        if (depth > MaxDepth)
            throw new SvgInvalidException("The SVG is nested deeper than the supported limit.");
        if (state.Elements > MaxElements)
            throw new SvgInvalidException("The SVG has more elements than the supported limit.");

        if (!IsSvgNamespace(reader.NamespaceURI) || !AllowedElements.Contains(name))
        {
            state.ElementsDropped++;
            SkipContent(reader);
            return null;
        }

        var element = new OutElement(name);
        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
                ReadAttribute(reader, element, state);
            reader.MoveToElement();
        }

        // <use> carries no content of its own; whatever sits inside it is dropped with it.
        if (name == "use")
        {
            SkipContent(reader);
            return element;
        }

        if (reader.IsEmptyElement)
            return element;

        bool keepText = TextElements.Contains(name);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
                break;

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    OutElement? child = ReadElement(reader, depth + 1, state);
                    if (child is not null)
                        element.Children.Add(child);
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    if (keepText && reader.Value.Length > 0)
                        element.Children.Add(new OutText(reader.Value));
                    break;
                default:
                    break;
            }
        }

        return element;
    }

    private static void SkipContent(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return;

        int depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                return;
        }
    }

    private static void ReadAttribute(XmlReader reader, OutElement element, State state)
    {
        // Namespace declarations are structural XML overhead, not content: the rebuilt document
        // carries only its own SVG namespace on the root, so input declarations are skipped
        // without counting as a hostile attribute.
        if (reader.NamespaceURI == XmlnsNamespace)
            return;

        // Other namespaced attributes (xlink:*, xml:*) are never carried into the new document.
        if (reader.NamespaceURI.Length != 0 || !AllowedAttributes.Contains(reader.LocalName))
        {
            state.AttributesDropped++;
            return;
        }

        string? value = CleanValue(reader.Value);
        if (value is null)
        {
            state.AttributesDropped++;
            return;
        }

        bool accepted = reader.LocalName switch
        {
            "id" => IsLocalId(value),
            "href" => IsLocalReference(value),
            "fill" or "stroke" or "stop-color" => IsPaint(value),
            "transform" => IsTransform(value),
            _ => true,
        };
        if (!accepted)
        {
            state.AttributesDropped++;
            return;
        }

        element.Attributes.Add(new KeyValuePair<string, string>(reader.LocalName, value));
    }

    private static string? CleanValue(string value)
    {
        if (value.Length > MaxAttributeLength)
            return null;

        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (!IsForbiddenCharacter(c))
                builder.Append(c);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool IsForbiddenCharacter(char c) =>
        (c < ' ' && c is not ('\t' or '\n' or '\r')) ||
        (c >= '\u007f' && c <= '\u009f') ||
        c is '؜' or '‎' or '‏' or '﻿' ||
        (c >= '‪' && c <= '‮') ||
        (c >= '⁦' && c <= '⁩');

    private static bool IsPaint(string value)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
            return true;

        if (value.Length is 4 or 7 or 9 && value[0] == '#')
        {
            for (int i = 1; i < value.Length; i++)
            {
                if (!char.IsAsciiHexDigit(value[i]))
                    return false;
            }

            return true;
        }

        return IsLocalUrl(value);
    }

    private static bool IsLocalUrl(string value) =>
        value.StartsWith(LocalUrlPrefix, StringComparison.Ordinal) &&
        value.EndsWith(')') &&
        IsLocalId(value[LocalUrlPrefix.Length..^1]);

    private static bool IsLocalReference(string value) =>
        value.Length > 1 && value[0] == '#' && IsLocalId(value[1..]);

    private static bool IsLocalId(string value)
    {
        if (value.Length == 0 || value.Length > MaxIdLength)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool valid = char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-';
            if (!valid)
                return false;
        }

        return true;
    }

    /// <summary>
    /// A transform is a comma/space separated sequence of allowlisted functions whose arguments use
    /// only digits, letters and <c>. , -</c>. Nothing is evaluated; the value is only validated.
    /// </summary>
    private static bool IsTransform(string value)
    {
        int i = 0;
        bool any = false;

        while (i < value.Length)
        {
            while (i < value.Length && char.IsWhiteSpace(value[i]))
                i++;
            if (i >= value.Length)
                break;

            int nameStart = i;
            while (i < value.Length && char.IsAsciiLetter(value[i]))
                i++;
            string name = value[nameStart..i];
            if (name is not ("matrix" or "translate" or "scale" or "rotate" or "skewX" or "skewY"))
                return false;

            while (i < value.Length && char.IsWhiteSpace(value[i]))
                i++;
            if (i >= value.Length || value[i] != '(')
                return false;
            i++;

            int argumentStart = i;
            while (i < value.Length && value[i] != ')')
            {
                char c = value[i];
                if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or ',' or '.' or '-'))
                    return false;
                i++;
            }

            if (i >= value.Length || i == argumentStart)
                return false;
            i++;

            any = true;
            while (i < value.Length && (char.IsWhiteSpace(value[i]) || value[i] == ','))
                i++;
        }

        return any;
    }

    /// <summary>
    /// Drops every reference that does not resolve to an id defined in this document. Ids belong to
    /// elements other than <c>use</c> outright; a <c>use</c> is kept only when its <c>#id</c> target
    /// is itself kept, computed as a reachability walk so a chain or cycle of <c>use</c> elements
    /// terminates in linear time.
    /// </summary>
    private static void ResolveReferences(OutElement root, State state)
    {
        var ids = new Dictionary<string, OutElement>(StringComparer.Ordinal);
        CollectIds(root, ids);

        var uses = new List<OutElement>();
        CollectUses(root, uses);

        var aliveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string id, OutElement element) in ids)
        {
            if (element.Name != "use")
                aliveIds.Add(id);
        }

        var usesByTarget = new Dictionary<string, List<OutElement>>(StringComparer.Ordinal);
        foreach (OutElement use in uses)
        {
            string? target = HrefTarget(use);
            if (target is null)
                continue;
            if (!usesByTarget.TryGetValue(target, out List<OutElement>? targeting))
                usesByTarget[target] = targeting = [];
            targeting.Add(use);
        }

        var aliveUses = new HashSet<OutElement>();
        var pending = new Queue<string>(aliveIds);
        while (pending.Count > 0)
        {
            if (!usesByTarget.TryGetValue(pending.Dequeue(), out List<OutElement>? targeting))
                continue;

            foreach (OutElement use in targeting)
            {
                if (!aliveUses.Add(use))
                    continue;
                if (use.GetAttribute("id") is string useId && aliveIds.Add(useId))
                    pending.Enqueue(useId);
            }
        }

        Prune(root, aliveUses, aliveIds, state);
    }

    private static void CollectIds(OutElement element, Dictionary<string, OutElement> ids)
    {
        if (element.GetAttribute("id") is string id && !ids.ContainsKey(id))
            ids.Add(id, element);

        foreach (OutNode child in element.Children)
        {
            if (child is OutElement childElement)
                CollectIds(childElement, ids);
        }
    }

    private static void CollectUses(OutElement element, List<OutElement> uses)
    {
        if (element.Name == "use")
        {
            uses.Add(element);
            return;
        }

        foreach (OutNode child in element.Children)
        {
            if (child is OutElement childElement)
                CollectUses(childElement, uses);
        }
    }

    private static string? HrefTarget(OutElement element)
    {
        string? href = element.GetAttribute("href");
        return href is not null && IsLocalReference(href) ? href[1..] : null;
    }

    private static bool Prune(
        OutElement element,
        HashSet<OutElement> aliveUses,
        HashSet<string> aliveIds,
        State state)
    {
        if (element.Name == "use" && !aliveUses.Contains(element))
        {
            state.ElementsDropped++;
            return false;
        }

        for (int i = element.Attributes.Count - 1; i >= 0; i--)
        {
            KeyValuePair<string, string> attribute = element.Attributes[i];
            bool keep = attribute.Key switch
            {
                "href" => IsLocalReference(attribute.Value) &&
                          aliveIds.Contains(attribute.Value[1..]),
                "fill" or "stroke" or "stop-color" when IsLocalUrl(attribute.Value) =>
                    aliveIds.Contains(attribute.Value[LocalUrlPrefix.Length..^1]),
                _ => true,
            };
            if (!keep)
            {
                element.Attributes.RemoveAt(i);
                state.AttributesDropped++;
            }
        }

        for (int i = element.Children.Count - 1; i >= 0; i--)
        {
            if (element.Children[i] is OutElement child && !Prune(child, aliveUses, aliveIds, state))
                element.Children.RemoveAt(i);
        }

        return true;
    }

    private static bool HasDrawable(OutElement element)
    {
        if (DrawableElements.Contains(element.Name))
            return true;

        foreach (OutNode child in element.Children)
        {
            if (child is OutElement childElement && HasDrawable(childElement))
                return true;
        }

        return false;
    }

    private static string Write(OutElement root)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = NewLineHandling.Replace,
            CheckCharacters = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using var stream = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(stream, settings))
            WriteElement(writer, root);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElement(XmlWriter writer, OutElement element)
    {
        // Every element is written in the SVG namespace so the writer declares the default xmlns
        // once, on the root, and never repeats it or introduces any other namespace.
        writer.WriteStartElement(element.Name, SvgNamespace);
        foreach (KeyValuePair<string, string> attribute in element.Attributes)
            writer.WriteAttributeString(attribute.Key, attribute.Value);

        foreach (OutNode child in element.Children)
        {
            switch (child)
            {
                case OutText text:
                    writer.WriteString(text.Text);
                    break;
                case OutElement childElement:
                    WriteElement(writer, childElement);
                    break;
            }
        }

        writer.WriteEndElement();
    }

    private static bool IsSvgNamespace(string namespaceUri) =>
        namespaceUri.Length == 0 || namespaceUri == SvgNamespace;

    private static SvgSanitizeResult Failure(string code, string message, State? state = null) =>
        new(false, null, code, message, state?.ElementsDropped ?? 0, state?.AttributesDropped ?? 0);

    private sealed class State
    {
        public int Elements { get; set; }
        public int ElementsDropped { get; set; }
        public int AttributesDropped { get; set; }
    }

    private abstract class OutNode;

    private sealed class OutText(string text) : OutNode
    {
        public string Text { get; } = text;
    }

    private sealed class OutElement(string name) : OutNode
    {
        public string Name { get; } = name;

        public List<KeyValuePair<string, string>> Attributes { get; } = [];

        public List<OutNode> Children { get; } = [];

        public string? GetAttribute(string name)
        {
            foreach (KeyValuePair<string, string> attribute in Attributes)
            {
                if (attribute.Key == name)
                    return attribute.Value;
            }

            return null;
        }
    }

    private sealed class SvgInvalidException(string message) : Exception(message);
}
