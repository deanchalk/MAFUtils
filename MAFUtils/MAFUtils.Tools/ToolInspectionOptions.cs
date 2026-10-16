namespace MAFUtils.Tools;

/// <summary>Immutable, per-call policy and budgets for offline contract inspection.</summary>
public sealed record ToolInspectionOptions
{
    /// <summary>Report names differing only by ordinal case. Defaults to true.</summary>
    public bool WarnOnCaseOnlyNames { get; init; } = true;
    /// <summary>Include informational findings. Does not affect partial-analysis flags.</summary>
    public bool IncludeInfo { get; init; } = true;
    /// <summary>Check descriptions on non-function tools too. Defaults to true.</summary>
    public bool InspectAllToolDescriptions { get; init; } = true;
    /// <summary>Optional minimum trimmed description length in UTF-16 characters. Null (default) or zero disables MFT011.</summary>
    public int? MinimumDescriptionLength { get; init; }
    /// <summary>Warn above this raw JSON UTF-8 byte count. Null (default) disables the warning.</summary>
    public int? SchemaByteWarningThreshold { get; init; }
    /// <summary>Warn above this JSON value depth (root is zero). Null (default) disables the warning.</summary>
    public int? DepthWarningThreshold { get; init; }
    /// <summary>Warn above this count of properties in visited supported schema positions. Null (default) disables the warning.</summary>
    public int? PropertyCountWarningThreshold { get; init; }
    /// <summary>Skip traversal above this raw UTF-8 byte count. Null (default) imposes no byte budget.</summary>
    public int? MaxSchemaBytes { get; init; }
    /// <summary>Maximum JSON values visited per schema, including annotation values. Null (default) imposes no node budget.</summary>
    public int? MaxSchemaNodes { get; init; }
    /// <summary>Maximum JSON value depth visited (root is zero). A supplied limit must be positive; null (default) imposes no depth budget.</summary>
    public int? MaxSchemaDepth { get; init; }

    internal void Validate()
    {
        if (MinimumDescriptionLength is int minimum) ArgumentOutOfRangeException.ThrowIfNegative(minimum, nameof(MinimumDescriptionLength));
        if (SchemaByteWarningThreshold is int bytes) ArgumentOutOfRangeException.ThrowIfNegative(bytes, nameof(SchemaByteWarningThreshold));
        if (DepthWarningThreshold is int depth) ArgumentOutOfRangeException.ThrowIfNegative(depth, nameof(DepthWarningThreshold));
        if (PropertyCountWarningThreshold is int properties) ArgumentOutOfRangeException.ThrowIfNegative(properties, nameof(PropertyCountWarningThreshold));
        if (MaxSchemaBytes is int maxBytes) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes, nameof(MaxSchemaBytes));
        if (MaxSchemaNodes is int maxNodes) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNodes, nameof(MaxSchemaNodes));
        if (MaxSchemaDepth is int maxDepth) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth, nameof(MaxSchemaDepth));
    }
}
