using System.Text.Json;
using System.Text.Json.Serialization;

namespace MAFUtils.ToolResults;

// Source-generated metadata lets the tools run where reflection-based JSON is disabled (Native AOT, trimming,
// .NET 10 file-based apps). It is appended after the default resolver, so reflection-enabled output is unchanged.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(StoredResultReference))]
[JsonSerializable(typeof(StoredResultToolResult<StoredResultLines>))]
[JsonSerializable(typeof(StoredResultToolResult<StoredResultMatches>))]
internal sealed partial class ToolResultsJsonContext : JsonSerializerContext;
