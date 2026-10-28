using System.Text.Json;
using static MAFUtils.ToolResults.Tests.TestTools;

namespace MAFUtils.ToolResults.Tests;

public sealed class StoreTests
{
    [Fact]
    public async Task ExpiryIsSlidingAndExpiredResultsAreUnavailable()
    {
        var clock = new ManualTimeProvider();
        var store = new ToolResultStore(new() { Expiry = TimeSpan.FromMinutes(30) }, clock);
        var handle = (await Overflow(Returning(Items(1000)).WithResultLimits(store))).ResultHandle!;
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(ErrorCode(await Read(store, handle)));
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(ErrorCode(await Read(store, handle)));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("result_unavailable", ErrorCode(await Read(store, handle)));
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.TotalCharacters);
    }

    [Fact]
    public async Task LeastRecentlyUsedResultIsEvictedAtMaxEntries()
    {
        var clock = new ManualTimeProvider();
        var store = new ToolResultStore(new() { MaxEntries = 2 }, clock);
        var function = Returning(Items(1000)).WithResultLimits(store);
        var first = (await Overflow(function)).ResultHandle!;
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = (await Overflow(function)).ResultHandle!;
        clock.Advance(TimeSpan.FromSeconds(1));
        await Read(store, first);
        clock.Advance(TimeSpan.FromSeconds(1));
        var third = (await Overflow(function)).ResultHandle!;
        Assert.Equal(2, store.Count);
        Assert.Equal("result_unavailable", ErrorCode(await Read(store, second)));
        Assert.Null(ErrorCode(await Read(store, first)));
        Assert.Null(ErrorCode(await Read(store, third)));
    }

    [Fact]
    public async Task OldestResultsAreEvictedToStayWithinTotalCharacters()
    {
        var clock = new ManualTimeProvider();
        var store = new ToolResultStore(new() { MaxTotalCharacters = 150_000 }, clock);
        var function = Returning(Items(1000)).WithResultLimits(store);
        var size = (await Overflow(function)).TotalCharacters;
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Overflow(function);
        }
        Assert.Equal(150_000 / size, store.Count);
        Assert.True(store.TotalCharacters <= 150_000);
    }

    [Fact]
    public async Task HostReadsTheExactStoredTextWithoutExtendingItsLifetime()
    {
        var clock = new ManualTimeProvider();
        var store = new ToolResultStore(new() { Expiry = TimeSpan.FromMinutes(30) }, clock);
        var text = "first\r\n" + new string('x', 2_500) + "\nlast";
        var handle = (await Overflow(new FixedFunction(() => text).WithResultLimits(store, new() { MaxInlineCharacters = 100, PreviewCharacters = 10 }))).ResultHandle!;
        var storedAt = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(20));

        Assert.True(store.TryGetResult(handle, out var result));
        Assert.Equal(new StoredToolResult(handle, "Fixed", "text", text, storedAt), result);

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(store.TryGetResult(handle, out result));
        Assert.Null(result);
        Assert.False(store.TryGetResult("res_unknown", out _));
        Assert.Throws<ArgumentNullException>(() => store.TryGetResult(null!, out _));
    }

    [Fact]
    public async Task HostReadsJsonAsIndentedText()
    {
        var store = new ToolResultStore();
        var reference = await Overflow(Returning(Items(1000)).WithResultLimits(store));
        Assert.True(store.TryGetResult(reference.ResultHandle!, out var result));
        Assert.Equal(("Lookup", "json", reference.TotalCharacters), (result.ToolName, result.Format, result.Text.Length));
        Assert.Equal(1000, JsonDocument.Parse(result.Text).RootElement.GetArrayLength());
        Assert.StartsWith(reference.Preview, result.Text);
    }

    [Fact]
    public async Task RemoveAndClear()
    {
        var store = new ToolResultStore();
        var function = Returning(Items(1000)).WithResultLimits(store);
        var handle = (await Overflow(function)).ResultHandle!;
        await Overflow(function);
        Assert.True(store.Remove(handle));
        Assert.False(store.Remove(handle));
        Assert.False(store.Remove("res_unknown"));
        Assert.Equal(1, store.Count);
        store.Clear();
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.TotalCharacters);
    }

    [Fact]
    public async Task HandlesAreRandomAndScopedToTheirStore()
    {
        var store = new ToolResultStore();
        var function = Returning(Items(1000)).WithResultLimits(store);
        var handles = new HashSet<string>();
        for (var i = 0; i < 10; i++) handles.Add((await Overflow(function)).ResultHandle!);
        Assert.Equal(10, handles.Count);
        Assert.All(handles, h => Assert.Matches("^res_[0-9a-f]{24}$", h));
        Assert.Equal("result_unavailable", ErrorCode(await Read(new ToolResultStore(), handles.First())));
    }

    [Fact]
    public async Task ConcurrentInvocationsRespectCapacity()
    {
        var store = new ToolResultStore(new() { MaxEntries = 8 });
        var function = Returning(Items(1000)).WithResultLimits(store);
        var references = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => Overflow(function))));
        Assert.All(references, r => Assert.True(r.Stored));
        Assert.Equal(8, store.Count);
        Assert.Equal(8L * references[0].TotalCharacters, store.TotalCharacters);
    }

    [Fact]
    public void OptionsAreValidatedAndHaveDocumentedDefaults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultStore(new() { Expiry = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultStore(new() { MaxEntries = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultStore(new() { MaxEntries = 1_001 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultStore(new() { MaxTotalCharacters = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultStore(new() { MaxResultCharacters = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultStore(new() { MaxReadCharacters = 999 }));
        var options = new ToolResultStoreOptions();
        var defaults = (options.Expiry, options.MaxEntries, options.MaxTotalCharacters, options.MaxResultCharacters, options.MaxReadCharacters);
        Assert.Equal((TimeSpan.FromMinutes(30), 32, 4_000_000L, 1_000_000, 8_000), defaults);
        var limits = new ToolResultLimitOptions();
        Assert.Equal((10_000, 1_500), (limits.MaxInlineCharacters, limits.PreviewCharacters));
    }
}
