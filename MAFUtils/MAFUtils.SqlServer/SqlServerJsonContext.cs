using System.Text.Json;
using System.Text.Json.Serialization;

namespace MAFUtils.SqlServer;

// Source-generated metadata lets the tools run where reflection-based JSON is disabled (Native AOT, trimming,
// .NET 10 file-based apps). It is appended after the default resolver, so reflection-enabled output is unchanged.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SqlObjectKind?))]
[JsonSerializable(typeof(DependencyDirection))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(DiscoveryToolResult<DatabaseInventory>))]
[JsonSerializable(typeof(DiscoveryToolResult<CatalogAnswer<MetadataPage<DatabaseObject>>>))]
[JsonSerializable(typeof(DiscoveryToolResult<CatalogAnswer<ObjectStructure>>))]
[JsonSerializable(typeof(DiscoveryToolResult<DependencyQueryResult>))]
[JsonSerializable(typeof(DiscoveryToolResult<CatalogStatus>))]
[JsonSerializable(typeof(DiscoveryToolResult<CatalogAnswer<DefinitionExcerpt>>))]
internal sealed partial class SqlServerJsonContext : JsonSerializerContext;
