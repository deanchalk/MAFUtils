using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace MAFUtils.ToolResults;

/// <summary>A stored result as seen by the host application.</summary>
/// <param name="ResultHandle">The handle given to the model.</param>
/// <param name="ToolName">The tool that produced the result.</param>
/// <param name="Format"><c>json</c> (indented JSON text) or <c>text</c>.</param>
/// <param name="Text">The complete stored text, exactly as stored, including original line breaks.</param>
/// <param name="StoredAt">When the result was stored.</param>
public sealed record StoredToolResult(string ResultHandle, string ToolName, string Format, string Text, DateTimeOffset StoredAt);

/// <summary>Holds oversized tool results for one session. Thread-safe. Results expire after
/// <see cref="ToolResultStoreOptions.Expiry"/> without reads and are evicted least recently used first.
/// Handles are only meaningful to the store that issued them, so one store per session isolates sessions.</summary>
public sealed class ToolResultStore
{
    private readonly Dictionary<string, StoredResult> entries = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly TimeProvider timeProvider;
    private long totalCharacters;

    /// <param name="options">Capacity, expiry and read limits. Copied; later changes have no effect.</param>
    /// <param name="timeProvider">Clock for expiry. Defaults to <see cref="TimeProvider.System"/>.</param>
    public ToolResultStore(ToolResultStoreOptions? options = null, TimeProvider? timeProvider = null)
    {
        var copy = options ?? new();
        if (copy.Expiry <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(copy.Expiry), copy.Expiry, "Expiry must be positive.");
        ValidateLimit(copy.MaxEntries, 1, 1_000, nameof(copy.MaxEntries));
        ValidateLimit(copy.MaxResultCharacters, 1, 100_000_000, nameof(copy.MaxResultCharacters));
        ValidateLimit(copy.MaxReadCharacters, StoredText.MaxLineCharacters, 100_000, nameof(copy.MaxReadCharacters));
        if (copy.MaxTotalCharacters < 1) throw new ArgumentOutOfRangeException(nameof(copy.MaxTotalCharacters), copy.MaxTotalCharacters, "MaxTotalCharacters must be positive.");
        Options = new()
        {
            Expiry = copy.Expiry, MaxEntries = copy.MaxEntries, MaxTotalCharacters = copy.MaxTotalCharacters,
            MaxResultCharacters = copy.MaxResultCharacters, MaxReadCharacters = copy.MaxReadCharacters
        };
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal ToolResultStoreOptions Options { get; }

    /// <summary>Number of unexpired stored results.</summary>
    public int Count { get { lock (gate) { RemoveExpired(); return entries.Count; } } }

    /// <summary>Characters held by unexpired stored results.</summary>
    public long TotalCharacters { get { lock (gate) { RemoveExpired(); return totalCharacters; } } }

    /// <summary>Removes one stored result. Returns false when the handle is unknown, expired or already evicted.</summary>
    public bool Remove(string handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (gate) { RemoveExpired(); return RemoveEntry(handle); }
    }

    /// <summary>Reads a stored result for the host application. Unlike the agent tools, this does not extend the
    /// result's lifetime. Returns false when the handle is unknown, expired, evicted or removed.</summary>
    public bool TryGetResult(string handle, [NotNullWhen(true)] out StoredToolResult? result)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (gate)
        {
            RemoveExpired();
            result = entries.TryGetValue(handle, out var entry)
                ? new(handle, entry.ToolName, entry.Format, entry.Text.Text, entry.StoredAt)
                : null;
            return result is not null;
        }
    }

    /// <summary>Removes every stored result. Call at the end of a run for run-scoped handles.</summary>
    public void Clear()
    {
        lock (gate) { entries.Clear(); totalCharacters = 0; }
    }

    /// <summary>Stores a result, evicting older ones as needed. Returns null when the result exceeds a size limit.</summary>
    internal string? TryAdd(string toolName, string format, StoredText text)
    {
        if (text.TotalCharacters > Options.MaxResultCharacters || text.TotalCharacters > Options.MaxTotalCharacters) return null;
        var handle = "res_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        lock (gate)
        {
            RemoveExpired();
            while (entries.Count >= Options.MaxEntries || totalCharacters + text.TotalCharacters > Options.MaxTotalCharacters)
                RemoveEntry(entries.MinBy(e => e.Value.LastAccess).Key);
            entries.Add(handle, new(toolName, format, text, timeProvider.GetUtcNow()));
            totalCharacters += text.TotalCharacters;
        }
        return handle;
    }

    /// <summary>Returns an unexpired result for an agent tool and extends its lifetime, or null.</summary>
    internal StoredResult? TryAccess(string handle)
    {
        lock (gate)
        {
            RemoveExpired();
            if (!entries.TryGetValue(handle, out var entry)) return null;
            entry.LastAccess = timeProvider.GetUtcNow();
            return entry;
        }
    }

    private void RemoveExpired()
    {
        var cutoff = timeProvider.GetUtcNow() - Options.Expiry;
        foreach (var (handle, entry) in entries)
            if (entry.LastAccess <= cutoff) RemoveEntry(handle);
    }

    private bool RemoveEntry(string handle)
    {
        if (!entries.Remove(handle, out var entry)) return false;
        totalCharacters -= entry.Text.TotalCharacters;
        return true;
    }

    private static void ValidateLimit(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {minimum} and {maximum}.");
    }
}

internal sealed class StoredResult(string toolName, string format, StoredText text, DateTimeOffset storedAt)
{
    public string ToolName { get; } = toolName;
    public string Format { get; } = format;
    public StoredText Text { get; } = text;
    public DateTimeOffset StoredAt { get; } = storedAt;
    public DateTimeOffset LastAccess { get; set; } = storedAt;
}
