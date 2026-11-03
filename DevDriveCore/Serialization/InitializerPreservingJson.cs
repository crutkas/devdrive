using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DevDriveCore.Serialization;

/// <summary>
/// Reads JSON through source-generated metadata while keeping the reflection serializer's rule that a
/// member absent from the payload keeps the value of its property initializer.
/// </summary>
/// <remarks>
/// <para>
/// Every model this library reads is a record with <c>init</c> properties, and several of those have
/// initializers (<c>Label = "DevDrive"</c>, <c>IsReadOnlyProbe = true</c>, <c>FileSystem = string.Empty</c>).
/// Reflection constructs the object and then sets only the members the JSON contains. The .NET 10 source
/// generator sets every init-only member in a single object initializer instead, so an absent member
/// gets <c>default</c>: a string the caller dereferences becomes <see langword="null"/> and a
/// <c>true</c> default becomes <c>false</c> (dotnet/runtime#84484, fixed in .NET 11).
/// </para>
/// <para>
/// Until then, a payload that is missing members is completed before it is read: the absent members,
/// taken from a serialized <c>new T()</c>, are appended to each object that lacks them. Everything the
/// payload does contain (values, explicit nulls, unknown members, duplicates) is copied verbatim, and
/// every absent member arrives carrying its initializer's value, which is exactly what reflection
/// produced. If the completed text fails to read, the original text is read instead so the exception,
/// including the path and position in its message, is the one callers always saw. A payload that
/// already names every member (everything this library and its helper write) is read directly. The
/// models are flat, so only the members of a top-level object, or of the objects in a top-level array,
/// need completing.
/// </para>
/// </remarks>
internal static class InitializerPreservingJson
{
    /// <summary>Reads one <typeparamref name="T"/> the way the reflection serializer did.</summary>
    /// <exception cref="JsonException">The JSON is invalid or does not fit <typeparamref name="T"/>.</exception>
    public static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(typeInfo);

        string? completed = TryComplete(json, typeInfo.Options, root =>
        {
            string? absent = AbsentMembers(root, typeInfo, Defaults(typeInfo));
            if (absent is null)
            {
                return null;
            }

            // The root object's closing brace is the last one in the text; only whitespace can follow it.
            int closingBrace = json.LastIndexOf('}');
            return string.Concat(json.AsSpan(0, closingBrace), Separator(root), absent, json.AsSpan(closingBrace));
        });

        return Read(json, completed, typeInfo);
    }

    /// <summary>Reads an array of <typeparamref name="T"/> the way the reflection serializer did.</summary>
    /// <exception cref="JsonException">The JSON is invalid or does not fit <typeparamref name="T"/>[].</exception>
    public static T[]? DeserializeArray<T>(string json, JsonTypeInfo<T[]> arrayTypeInfo, JsonTypeInfo<T> elementTypeInfo)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(arrayTypeInfo);
        ArgumentNullException.ThrowIfNull(elementTypeInfo);

        string? completed = TryComplete(json, arrayTypeInfo.Options, root =>
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement defaults = Defaults(elementTypeInfo);
            List<(JsonElement Element, string? Absent)> elements =
                [.. root.EnumerateArray().Select(element => (element, AbsentMembers(element, elementTypeInfo, defaults)))];
            if (elements.TrueForAll(item => item.Absent is null))
            {
                return null;
            }

            // Each element keeps its exact text; only the whitespace between elements is dropped.
            var builder = new StringBuilder(json.Length + (elements.Count * 64));
            builder.Append('[');
            for (int i = 0; i < elements.Count; i++)
            {
                (JsonElement element, string? absent) = elements[i];
                string raw = element.GetRawText();
                builder.Append(i == 0 ? string.Empty : ",");
                if (absent is null)
                {
                    builder.Append(raw);
                }
                else
                {
                    builder.Append(raw, 0, raw.Length - 1).Append(Separator(element)).Append(absent).Append('}');
                }
            }

            return builder.Append(']').ToString();
        });

        return Read(json, completed, arrayTypeInfo);
    }

    // Parses the payload and hands its root to `complete`. Null when there is nothing to complete or the
    // text is not something this class can safely rewrite (invalid JSON, or a member name that does not
    // decode); the serializer then reads the original text and reports it exactly as before.
    private static string? TryComplete(string json, JsonSerializerOptions options, Func<JsonElement, string?> complete)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = options.AllowTrailingCommas,
                CommentHandling = options.ReadCommentHandling,
                MaxDepth = options.MaxDepth,
            });
            return complete(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static TValue? Read<TValue>(string original, string? completed, JsonTypeInfo<TValue> typeInfo)
    {
        if (completed is null)
        {
            return JsonSerializer.Deserialize(original, typeInfo);
        }

        try
        {
            return JsonSerializer.Deserialize(completed, typeInfo);
        }
        catch (JsonException)
        {
            // Completion only added valid members, so the original fails at the same token. Reading it
            // reports that token's path and position in the caller's own text.
            JsonSerializer.Deserialize(original, typeInfo);
            throw;
        }
    }

    // What a freshly constructed T looks like on the wire: every member, each holding its initializer.
    private static JsonElement Defaults<T>(JsonTypeInfo<T> typeInfo)
        where T : class, new() =>
        JsonSerializer.SerializeToElement(new T(), typeInfo);

    // The members of `defaults` that `element` does not name (matched the way the serializer matches
    // names) as comma-separated JSON members, or null when nothing is missing or `element` is not an
    // object. A null, an array or a primitive is left for the serializer to accept or reject as before.
    private static string? AbsentMembers<T>(JsonElement element, JsonTypeInfo<T> typeInfo, JsonElement defaults)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        StringComparer comparer = typeInfo.Options.PropertyNameCaseInsensitive
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        HashSet<string> present = new(element.EnumerateObject().Select(property => property.Name), comparer);
        List<JsonProperty> absent = [.. defaults.EnumerateObject().Where(property => !present.Contains(property.Name))];
        if (absent.Count == 0)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in absent)
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        // Drop the braces: the members are spliced into the payload's own object.
        return Encoding.UTF8.GetString(buffer.WrittenSpan[1..^1]);
    }

    private static string Separator(JsonElement element) =>
        element.EnumerateObject().Any() ? "," : string.Empty;
}
