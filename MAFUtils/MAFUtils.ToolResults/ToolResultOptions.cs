namespace MAFUtils.ToolResults;

/// <summary>Capacity, expiry and read limits for one <see cref="ToolResultStore"/>. Copied when the store is constructed.</summary>
public sealed class ToolResultStoreOptions
{
    /// <summary>Sliding expiry: each read extends a stored result's lifetime. Defaults to 30 minutes.</summary>
    public TimeSpan Expiry { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Maximum stored results. The least recently used result is evicted beyond this. Defaults to 32.</summary>
    public int MaxEntries { get; init; } = 32;
    /// <summary>Maximum characters held across all stored results. Defaults to 4,000,000.</summary>
    public long MaxTotalCharacters { get; init; } = 4_000_000;
    /// <summary>Results with more stored characters are not stored; only a preview is returned. Defaults to 1,000,000.</summary>
    public int MaxResultCharacters { get; init; } = 1_000_000;
    /// <summary>Maximum characters returned by one read or search call. Defaults to 8,000.</summary>
    public int MaxReadCharacters { get; init; } = 8_000;
}

/// <summary>Per-tool limits applied by <see cref="ToolResultLimits.WithResultLimits(Microsoft.Extensions.AI.AIFunction, ToolResultStore, ToolResultLimitOptions?)"/>.</summary>
public sealed record ToolResultLimitOptions
{
    /// <summary>Results at or under this length are returned unchanged. JSON is measured compactly serialized,
    /// text by its length in UTF-16 characters. Defaults to 10,000.</summary>
    public int MaxInlineCharacters { get; init; } = 10_000;
    /// <summary>Maximum preview length returned in place of an oversized result. Must not exceed
    /// <see cref="MaxInlineCharacters"/>. Defaults to 1,500.</summary>
    public int PreviewCharacters { get; init; } = 1_500;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInlineCharacters, nameof(MaxInlineCharacters));
        ArgumentOutOfRangeException.ThrowIfNegative(PreviewCharacters, nameof(PreviewCharacters));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PreviewCharacters, MaxInlineCharacters, nameof(PreviewCharacters));
    }
}
