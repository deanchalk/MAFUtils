using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MAFUtils.ToolResults;

/// <summary>Returned in place of an oversized result. <see cref="Stored"/> is false when only the preview exists.</summary>
/// <param name="ResultHandle">Handle for the read and search tools, or null when not stored.</param>
/// <param name="Stored">Whether the full result was stored.</param>
/// <param name="ToolName">The tool that produced the result.</param>
/// <param name="Format"><c>json</c> (stored indented, one value per line) or <c>text</c>.</param>
/// <param name="TotalCharacters">Length of the stored text.</param>
/// <param name="TotalLines">Number of stored lines. Lines over 1,000 characters are wrapped.</param>
/// <param name="Shape">One-line sketch of the top-level structure.</param>
/// <param name="Preview">The first lines of the stored text. Always incomplete.</param>
/// <param name="Note">Guidance for the model.</param>
public sealed record StoredResultReference(string? ResultHandle, bool Stored, string ToolName, string Format,
    int TotalCharacters, int TotalLines, string Shape, string Preview, string Note);

/// <summary>Wraps tools so oversized results are stored in a <see cref="ToolResultStore"/> and replaced by a
/// <see cref="StoredResultReference"/>. Names, descriptions and input schemas are unchanged.</summary>
public static class ToolResultLimits
{
    /// <summary>Wraps one function. An <see cref="ApprovalRequiredAIFunction"/> stays outermost so it still requires
    /// approval; tools created by <see cref="StoredResultTools.Create"/> are returned unwrapped.</summary>
    public static AIFunction WithResultLimits(this AIFunction function, ToolResultStore store, ToolResultLimitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(store);
        options ??= new();
        options.Validate();
        return Wrap(function, store, options);
    }

    /// <summary>Wraps every <see cref="AIFunction"/> in <paramref name="tools"/>; other tools are returned as they are.</summary>
    public static IReadOnlyList<AITool> WithResultLimits(this IEnumerable<AITool> tools, ToolResultStore store, ToolResultLimitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(store);
        options ??= new();
        options.Validate();
        return tools.Select(tool => tool is AIFunction function ? Wrap(function, store, options) : tool).ToArray().AsReadOnly();
    }

    private static AIFunction Wrap(AIFunction function, ToolResultStore store, ToolResultLimitOptions options)
    {
        if (function is StoredResultFunction) return function;
        var limited = new ResultLimitingFunction(function, store, options);
        // Re-apply approval outermost, whether the invoker checks the outer type or queries GetService.
        return function.GetService<ApprovalRequiredAIFunction>() is null ? limited : new ApprovalRequiredAIFunction(limited);
    }

    private sealed class ResultLimitingFunction(AIFunction inner, ToolResultStore store, ToolResultLimitOptions options)
        : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // Exceptions and cancellation propagate unchanged; only completed results are measured.
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            return ResultLimiter.Limit(Name, result, InnerFunction.JsonSerializerOptions, store, options);
        }
    }
}

internal static class ResultLimiter
{
    private const int MaxShapeMembers = 20;
    private const int MaxShapeItems = 100;
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonWriterOptions Indented = Compact with { Indented = true, NewLine = "\n" };

    public static object? Limit(string toolName, object? result, JsonSerializerOptions serializerOptions,
        ToolResultStore store, ToolResultLimitOptions options)
    {
        if (!TryClassify(result, serializerOptions, out var json, out var text)) return result;
        string format;
        if (json is JsonElement element)
        {
            if (Write(element, Compact).Length <= options.MaxInlineCharacters) return result;
            (format, text) = ("json", Write(element, Indented));
        }
        else
        {
            if (text!.Length <= options.MaxInlineCharacters) return result;
            format = "text";
        }

        var stored = StoredText.Create(text);
        var handle = store.TryAdd(toolName, format, stored);
        var shape = json is JsonElement value ? Shape(value) : $"text, {stored.LineCount} lines";
        var note = handle is null
            ? "The result was too large to return in full or to store. Only this incomplete preview is available. Call the tool again with narrower arguments if more detail is needed."
            : "The result was too large to return in full and has been stored. The preview is incomplete. Search it with SearchStoredResult, then read the relevant lines with ReadStoredResult; do not read every line in sequence unless the whole result is needed.";
        var reference = new StoredResultReference(handle, handle is not null, toolName, format, stored.TotalCharacters,
            stored.LineCount, shape, stored.Preview(options.PreviewCharacters), note);
        return JsonSerializer.SerializeToElement(reference, ToolResultsJsonContext.Default.StoredResultReference);
    }

    /// <summary>Finds JSON or text in a result. Images, other content and unserializable values return false and pass through.</summary>
    private static bool TryClassify(object? result, JsonSerializerOptions serializerOptions, out JsonElement? json, out string? text)
    {
        (json, text) = (null, null);
        switch (result)
        {
            case null: return false;
            case string s: return ClassifyText(s, out json, out text);
            case TextContent content: return ClassifyText(content.Text, out json, out text);
            case AIContent or IEnumerable<AIContent>: return false;
            case JsonElement element: return ClassifyElement(element, out json, out text);
        }
        JsonElement serialized;
        try { serialized = JsonSerializer.SerializeToElement(result, serializerOptions.GetTypeInfo(result.GetType())); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { return false; }
        return ClassifyElement(serialized, out json, out text);
    }

    private static bool ClassifyElement(JsonElement element, out JsonElement? json, out string? text)
    {
        (json, text) = (null, null);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object or JsonValueKind.Array: json = element; return true;
            case JsonValueKind.String: return ClassifyText(element.GetString()!, out json, out text);
            default: return false;
        }
    }

    private static bool ClassifyText(string value, out JsonElement? json, out string? text)
    {
        (json, text) = (null, value);
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            try { json = JsonSerializer.Deserialize(value, ToolResultsJsonContext.Default.JsonElement); }
            catch (JsonException) { }
        }
        return true;
    }

    private static string Write(JsonElement element, JsonWriterOptions writerOptions)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, writerOptions)) element.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>One-line sketch of the top-level structure, e.g. <c>array[1840] of object{id, total}</c>.</summary>
    internal static string Shape(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => $"array[{element.GetArrayLength()}]{ItemShape(element)}",
        JsonValueKind.Object => $"object{{{Members(element.EnumerateObject().Select(p => $"{p.Name}: {Kind(p.Value)}"), element.EnumerateObject().Count())}}}",
        _ => Kind(element)
    };

    private static string ItemShape(JsonElement array)
    {
        var items = array.EnumerateArray().Take(MaxShapeItems).ToArray();
        if (items.Length == 0) return "";
        if (items.All(i => i.ValueKind == JsonValueKind.Object))
        {
            var names = items.SelectMany(i => i.EnumerateObject().Select(p => p.Name)).Distinct(StringComparer.Ordinal).ToArray();
            return $" of object{{{Members(names, names.Length)}}}";
        }
        var kinds = items.Select(Kind).Distinct(StringComparer.Ordinal).ToArray();
        return kinds.Length == 1 ? $" of {kinds[0]}" : " of mixed values";
    }

    private static string Members(IEnumerable<string> members, int count)
    {
        var shown = string.Join(", ", members.Take(MaxShapeMembers));
        return count > MaxShapeMembers ? $"{shown}, … {count - MaxShapeMembers} more" : shown;
    }

    private static string Kind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => $"array[{element.GetArrayLength()}]",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null"
    };
}
