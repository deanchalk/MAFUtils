using System.Text.Json;
using static MAFUtils.ToolResults.Tests.TestTools;

namespace MAFUtils.ToolResults.Tests;

public sealed class StoredResultToolTests
{
    private static async Task<(ToolResultStore Store, StoredResultReference Reference)> StoreItems(ToolResultStoreOptions? options = null)
    {
        var store = new ToolResultStore(options);
        return (store, await Overflow(Returning(Items(1000)).WithResultLimits(store)));
    }

    private static JsonElement Data(JsonElement result)
    {
        Assert.Null(ErrorCode(result));
        return result.GetProperty("data");
    }

    [Fact]
    public void ToolsHaveStableNamesAndParameters()
    {
        var tools = StoredResultTools.Create(new ToolResultStore());
        Assert.Equal(["ReadStoredResult", "SearchStoredResult"], tools.Select(t => t.Name));
        Assert.Equal(["resultHandle", "offset", "limit"], tools[0].JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["resultHandle", "text", "offset", "limit"], tools[1].JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Contains("untrusted data", StoredResultTools.RecommendedInstructions);
    }

    [Fact]
    public async Task ReadPagesThroughEveryLine()
    {
        var (store, reference) = await StoreItems();
        List<string> lines = [];
        int? offset = 0;
        while (offset is int next)
        {
            var page = Data(await Read(store, reference.ResultHandle!, next, 100));
            Assert.Equal(next, page.GetProperty("offset").GetInt32());
            Assert.Equal(reference.TotalLines, page.GetProperty("totalLines").GetInt32());
            var text = page.GetProperty("text").GetString()!.Split('\n');
            Assert.Equal(page.GetProperty("lineCount").GetInt32(), text.Length);
            lines.AddRange(text);
            offset = NextOffset(page);
        }
        Assert.Equal(reference.TotalLines, lines.Count);
        Assert.Equal(reference.TotalCharacters, string.Join('\n', lines).Length);
    }

    [Fact]
    public async Task NegativeOffsetReadsFromTheEnd()
    {
        var (store, reference) = await StoreItems();
        var page = Data(await Read(store, reference.ResultHandle!, -3));
        Assert.Equal(reference.TotalLines - 3, page.GetProperty("offset").GetInt32());
        Assert.Equal(3, page.GetProperty("lineCount").GetInt32());
        Assert.Null(NextOffset(page));
        Assert.EndsWith("}\n]", page.GetProperty("text").GetString());
        Assert.Equal(0, Data(await Read(store, reference.ResultHandle!, -1_000_000)).GetProperty("offset").GetInt32());
        Assert.Equal(0, Data(await Read(store, reference.ResultHandle!, reference.TotalLines)).GetProperty("lineCount").GetInt32());
    }

    [Fact]
    public async Task ReadStopsAtTheCharacterBudget()
    {
        var (store, reference) = await StoreItems(new() { MaxReadCharacters = 1_000 });
        var page = Data(await Read(store, reference.ResultHandle!, 0, 1_000));
        Assert.InRange(page.GetProperty("text").GetString()!.Length, 900, 1_000);
        Assert.Equal(page.GetProperty("lineCount").GetInt32(), NextOffset(page));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1_001)]
    [InlineData(1_000_000, null)]
    public async Task InvalidReadArgumentsAreReported(int offset, int? limit)
    {
        var (store, reference) = await StoreItems();
        var result = await Read(store, reference.ResultHandle!, offset, limit);
        Assert.Equal("invalid_argument", ErrorCode(result));
        Assert.Contains("not an empty result", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingOrUnknownHandlesAreReported()
    {
        var store = new ToolResultStore();
        Assert.Equal("invalid_argument", ErrorCode(await Read(store, " ")));
        Assert.Equal("result_unavailable", ErrorCode(await Read(store, "res_000000000000000000000000")));
        Assert.Equal("result_unavailable", ErrorCode(await Search(store, "res_000000000000000000000000", "x")));
    }

    [Fact]
    public async Task SearchIsLiteralCaseInsensitiveAndCountsEveryMatch()
    {
        var (store, reference) = await StoreItems();
        var expected = Enumerable.Range(0, 1000).Count(i => i % 7 == 0);
        var first = Data(await Search(store, reference.ResultHandle!, "OVERDUE"));
        Assert.Equal(expected, first.GetProperty("totalMatches").GetInt32());
        Assert.Equal(20, first.GetProperty("matches").GetArrayLength());
        Assert.Equal(20, NextOffset(first));

        var last = Data(await Search(store, reference.ResultHandle!, "overdue", expected - 3, 100));
        Assert.Equal(3, last.GetProperty("matches").GetArrayLength());
        Assert.Null(NextOffset(last));

        Assert.Equal(0, Data(await Search(store, reference.ResultHandle!, "o.e")).GetProperty("totalMatches").GetInt32());
        Assert.Equal(0, Data(await Search(store, reference.ResultHandle!, "overdue", expected)).GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public async Task MatchLinesCanBeReadAndSnippetsDropIndentation()
    {
        var (store, reference) = await StoreItems();
        var match = Data(await Search(store, reference.ResultHandle!, "item-00700")).GetProperty("matches")[0];
        Assert.Equal("\"id\": \"item-00700\",", match.GetProperty("snippet").GetString());
        var line = Data(await Read(store, reference.ResultHandle!, match.GetProperty("line").GetInt32(), 1));
        Assert.Contains("item-00700", line.GetProperty("text").GetString());
    }

    [Fact]
    public async Task LongLineSnippetsAreWindowedAroundTheMatch()
    {
        var store = new ToolResultStore();
        var text = new string('a', 500) + "needle" + new string('b', 400) + "\n" + new string('c', 10_000);
        var reference = await Overflow(new FixedFunction(() => text).WithResultLimits(store));
        var snippet = Data(await Search(store, reference.ResultHandle!, "NEEDLE")).GetProperty("matches")[0].GetProperty("snippet").GetString()!;
        Assert.StartsWith("…aaaa", snippet);
        Assert.EndsWith("bbbb…", snippet);
        Assert.Contains("needle", snippet);
        Assert.Equal(202, snippet.Length);
    }

    [Fact]
    public async Task SearchStopsAtTheCharacterBudget()
    {
        var store = new ToolResultStore(new() { MaxReadCharacters = 1_000 });
        var text = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"match {i} " + new string('x', 400)));
        var reference = await Overflow(new FixedFunction(() => text).WithResultLimits(store));
        var page = Data(await Search(store, reference.ResultHandle!, "match", 0, 100));
        Assert.Equal(100, page.GetProperty("totalMatches").GetInt32());
        Assert.Equal(4, page.GetProperty("matches").GetArrayLength());
        Assert.Equal(4, NextOffset(page));
    }

    [Theory]
    [InlineData("", 0, null)]
    [InlineData("x", -1, null)]
    [InlineData("x", 0, 0)]
    [InlineData("x", 0, 101)]
    public async Task InvalidSearchArgumentsAreReported(string text, int offset, int? limit)
    {
        var (store, reference) = await StoreItems();
        Assert.Equal("invalid_argument", ErrorCode(await Search(store, reference.ResultHandle!, text, offset, limit)));
        Assert.Equal("invalid_argument", ErrorCode(await Search(store, reference.ResultHandle!, new string('x', 201))));
    }
}
