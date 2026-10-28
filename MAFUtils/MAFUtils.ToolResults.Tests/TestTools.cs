using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MAFUtils.ToolResults.Tests;

internal static class TestTools
{
    public static AIFunction Returning(object? value, string name = "Lookup")
        => AIFunctionFactory.Create(() => value, name, "Look up items.");

    public static AIFunction ReturningText(string value, string name = "Lookup")
        => AIFunctionFactory.Create(() => value, name, "Look up items.");

    /// <summary>An array of <paramref name="count"/> objects, roughly 60 compact characters each.</summary>
    public static JsonElement Items(int count) => JsonSerializer.SerializeToElement(
        Enumerable.Range(0, count).Select(i => new Dictionary<string, object> { ["id"] = $"item-{i:D5}", ["total"] = i * 10, ["status"] = i % 7 == 0 ? "Overdue" : "Paid" }));

    public static async Task<JsonElement> Invoke(AIFunction function, Dictionary<string, object?>? arguments = null)
    {
        var result = await function.InvokeAsync(new AIFunctionArguments(arguments ?? []));
        return result is JsonElement element ? element : JsonSerializer.SerializeToElement(result, AIJsonUtilities.DefaultOptions);
    }

    public static async Task<StoredResultReference> Overflow(AIFunction function, Dictionary<string, object?>? arguments = null)
        => (await Invoke(function, arguments)).Deserialize<StoredResultReference>(JsonSerializerOptions.Web)!;

    // The framework's serializer omits null properties.
    public static string? ErrorCode(JsonElement result)
        => result.TryGetProperty("errorCode", out var code) ? code.GetString() : null;

    public static int? NextOffset(JsonElement data)
        => data.TryGetProperty("nextOffset", out var next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null;

    public static AIFunction Tool(ToolResultStore store, string name)
        => StoredResultTools.Create(store).Single(t => t.Name == name);

    public static async Task<JsonElement> Read(ToolResultStore store, string handle, int offset = 0, int? limit = null)
        => await Invoke(Tool(store, "ReadStoredResult"), new() { ["resultHandle"] = handle, ["offset"] = offset, ["limit"] = limit });

    public static async Task<JsonElement> Search(ToolResultStore store, string handle, string text, int offset = 0, int? limit = null)
        => await Invoke(Tool(store, "SearchStoredResult"), new() { ["resultHandle"] = handle, ["text"] = text, ["offset"] = offset, ["limit"] = limit });
}

/// <summary>Returns a fixed value without the marshalling AIFunctionFactory applies.</summary>
internal sealed class FixedFunction(Func<object?> value, JsonSerializerOptions? serializerOptions = null) : AIFunction
{
    public int Invocations { get; private set; }
    public override string Name => "Fixed";
    public override string Description => "Returns a fixed value.";
    public override JsonSerializerOptions JsonSerializerOptions => serializerOptions ?? AIJsonUtilities.DefaultOptions;
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        Invocations++;
        return ValueTask.FromResult(value());
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}
