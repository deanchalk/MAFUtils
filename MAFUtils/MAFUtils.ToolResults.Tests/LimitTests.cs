using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using static MAFUtils.ToolResults.Tests.TestTools;

namespace MAFUtils.ToolResults.Tests;

public sealed class LimitTests
{
    [Fact]
    public async Task SmallResultIsReturnedUnchanged()
    {
        var store = new ToolResultStore();
        var value = Items(3);
        var result = await new FixedFunction(() => value).WithResultLimits(store).InvokeAsync();
        Assert.Equal(value, Assert.IsType<JsonElement>(result));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task ResultAtTheLimitIsInlineAndOneMoreCharacterIsStored()
    {
        var store = new ToolResultStore();
        var options = new ToolResultLimitOptions { MaxInlineCharacters = 100, PreviewCharacters = 10 };
        Assert.Equal(new string('a', 100), await new FixedFunction(() => new string('a', 100)).WithResultLimits(store, options).InvokeAsync());
        var reference = await Overflow(new FixedFunction(() => new string('a', 101)).WithResultLimits(store, options));
        Assert.True(reference.Stored);
        Assert.Equal(101, reference.TotalCharacters);
        Assert.Equal("aaaaaaaaaa", reference.Preview);
    }

    [Fact]
    public async Task OversizedJsonIsStoredWithShapeAndPreview()
    {
        var store = new ToolResultStore();
        var reference = await Overflow(Returning(Items(1000)).WithResultLimits(store));
        Assert.True(reference.Stored);
        Assert.StartsWith("res_", reference.ResultHandle);
        Assert.Equal("Lookup", reference.ToolName);
        Assert.Equal("json", reference.Format);
        Assert.Equal("array[1000] of object{id, total, status}", reference.Shape);
        Assert.StartsWith("[\n  {\n    \"id\": \"item-00000\"", reference.Preview);
        Assert.InRange(reference.Preview.Length, 1, 1_500);
        Assert.True(reference.TotalLines > 1000);
        Assert.Contains("SearchStoredResult", reference.Note);
        Assert.Equal(1, store.Count);
        Assert.Equal(reference.TotalCharacters, store.TotalCharacters);
    }

    [Fact]
    public async Task ObjectShapeListsMembersAndCountsTheRest()
    {
        var value = Enumerable.Range(0, 25).ToDictionary(i => $"p{i:D2}", i => (object)(i == 0 ? Items(400) : "x"));
        var reference = await Overflow(Returning(JsonSerializer.SerializeToElement(value)).WithResultLimits(new ToolResultStore()));
        Assert.StartsWith("object{p00: array[400], p01: string, ", reference.Shape);
        Assert.EndsWith("p19: string, … 5 more}", reference.Shape);
    }

    [Fact]
    public async Task JsonTextIsStoredIndentedAndPlainTextAsLines()
    {
        var store = new ToolResultStore();
        var json = await Overflow(ReturningText(Items(1000).GetRawText()).WithResultLimits(store));
        Assert.Equal("json", json.Format);
        Assert.StartsWith("[\n  {", json.Preview);

        var text = string.Join("\r\n", Enumerable.Range(0, 500).Select(i => $"log line {i} with some padding text"));
        var lines = await Overflow(ReturningText(text).WithResultLimits(store));
        Assert.Equal("text", lines.Format);
        Assert.Equal("text, 500 lines", lines.Shape);
        Assert.Equal(500, lines.TotalLines);
        Assert.DoesNotContain('\r', lines.Preview);
        Assert.Equal(text.Length, lines.TotalCharacters);
    }

    [Fact]
    public async Task InvalidJsonTextIsTreatedAsText()
    {
        var reference = await Overflow(ReturningText("[not json " + new string('x', 20_000)).WithResultLimits(new ToolResultStore()));
        Assert.Equal("text", reference.Format);
    }

    [Fact]
    public async Task TextContentIsLimitedAndOtherContentPassesThrough()
    {
        var store = new ToolResultStore();
        var text = await Overflow(new FixedFunction(() => new TextContent(new string('t', 20_000))).WithResultLimits(store));
        Assert.Equal("text", text.Format);

        var image = new DataContent(new byte[100_000], "image/png");
        Assert.Same(image, await new FixedFunction(() => image).WithResultLimits(store).InvokeAsync());
        AIContent[] contents = [image];
        Assert.Same(contents, await new FixedFunction(() => contents).WithResultLimits(store).InvokeAsync());
        Assert.Null(await new FixedFunction(() => null).WithResultLimits(store).InvokeAsync());
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task UnserializableResultPassesThrough()
    {
        var value = new { Text = new string('x', 20_000) };
        var options = new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() };
        Assert.Same(value, await new FixedFunction(() => value, options).WithResultLimits(new ToolResultStore()).InvokeAsync());
    }

    [Fact]
    public async Task ExceptionsPropagateAndNothingIsStored()
    {
        var store = new ToolResultStore();
        var function = new FixedFunction(() => throw new InvalidOperationException("boom")).WithResultLimits(store);
        Assert.Equal("boom", (await Assert.ThrowsAsync<InvalidOperationException>(() => function.InvokeAsync().AsTask())).Message);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task ResultTooLargeToStoreReturnsOnlyAPreview()
    {
        var store = new ToolResultStore(new() { MaxResultCharacters = 5_000 });
        var reference = await Overflow(Returning(Items(1000)).WithResultLimits(store));
        Assert.False(reference.Stored);
        Assert.Null(reference.ResultHandle);
        Assert.Contains("narrower arguments", reference.Note);
        Assert.NotEmpty(reference.Preview);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ContractIsUnchanged()
    {
        var inner = AIFunctionFactory.Create((string customer, int top) => "", "FindOrders", "Find orders for a customer.");
        var wrapped = inner.WithResultLimits(new ToolResultStore());
        Assert.NotSame(inner, wrapped);
        Assert.Equal(inner.Name, wrapped.Name);
        Assert.Equal(inner.Description, wrapped.Description);
        Assert.Equal(inner.JsonSchema.GetRawText(), wrapped.JsonSchema.GetRawText());
    }

    [Fact]
    public async Task ApprovalRequirementStaysOutermost()
    {
        var store = new ToolResultStore();
        var wrapped = new ApprovalRequiredAIFunction(Returning(Items(1000))).WithResultLimits(store);
        Assert.IsType<ApprovalRequiredAIFunction>(wrapped);
        Assert.True((await Overflow(wrapped)).Stored);
    }

    [Fact]
    public void ListOverloadWrapsOnlyFunctionsAndNeverCompanionTools()
    {
        var store = new ToolResultStore();
        var function = Returning(1);
        var hosted = new HostedWebSearchTool();
        var companions = StoredResultTools.Create(store);
        AITool[] all = [function, hosted, .. companions];
        var tools = all.WithResultLimits(store);
        Assert.Equal(4, tools.Count);
        Assert.NotSame(function, tools[0]);
        Assert.Same(hosted, tools[1]);
        Assert.Same(companions[0], tools[2]);
        Assert.Same(companions[1], tools[3]);
        Assert.Same(companions[0], companions[0].WithResultLimits(store));
    }

    [Fact]
    public void InvalidArgumentsThrow()
    {
        var store = new ToolResultStore();
        Assert.Throws<ArgumentNullException>(() => ((AIFunction)null!).WithResultLimits(store));
        Assert.Throws<ArgumentNullException>(() => Returning(1).WithResultLimits(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => Returning(1).WithResultLimits(store, new() { MaxInlineCharacters = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Returning(1).WithResultLimits(store, new() { PreviewCharacters = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Returning(1).WithResultLimits(store, new() { MaxInlineCharacters = 10, PreviewCharacters = 11 }));
    }

    [Fact]
    public async Task SurrogatePairsAreNeverSplit()
    {
        var store = new ToolResultStore(new() { MaxReadCharacters = 20_000 });
        var text = string.Concat(Enumerable.Repeat("😀", 6_000));
        var reference = await Overflow(new FixedFunction(() => text).WithResultLimits(store, new() { PreviewCharacters = 999 }));
        Assert.Equal(12, reference.TotalLines);
        Assert.Equal(998, reference.Preview.Length);
        var read = await Read(store, reference.ResultHandle!);
        var lines = read.GetProperty("data").GetProperty("text").GetString()!.Split('\n');
        Assert.All(lines, line => Assert.True(Encoding.UTF8.GetByteCount(line) == line.Length * 2, "Line contains an unpaired surrogate."));
        Assert.Equal(text, string.Concat(lines));
    }
}
