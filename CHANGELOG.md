# Changelog

Both packages are released together with the same version from a single tag.

## 0.6.0

### MAFUtils.SqlServer

- Fixed: references qualified with the current database name (for example `MyDb.dbo.Orders`) are now included in
  the dependency graph.
- Fixed: functions referenced from CHECK and DEFAULT constraints are now attributed to the owning table.
- Fixed: definition slices and truncated text no longer split UTF-16 surrogate pairs, which previously reached
  the model as U+FFFD replacement characters. A one-character slice at a surrogate pair returns the whole pair.
- Fixed: the tools now work where reflection-based System.Text.Json is disabled (Native AOT, trimmed apps and
  .NET 10 file-based apps). Tool schemas and results are unchanged for other applications.
- Added `SqlServerDiscoveryOptions.IncludeBuiltInSchemas`.
- Added `SqlServerDiscoveryOptions.AllowAgentRefresh`; set it to false to omit the `RefreshCatalog` agent tool.
- Now depends on `Microsoft.Extensions.AI.Abstractions` 10.11.0 instead of `Microsoft.Extensions.AI`.
- Updated `System.Security.Cryptography.Pkcs` to 10.0.14.
- Argument exceptions name the actual invalid parameter; type-specific describe mismatches include a message.

### MAFUtils.Tools

- Fixed: `ToolInspectionReport.ToText()` no longer throws where reflection-based System.Text.Json is disabled.
- `ToText()` now prints non-ASCII names readably and still escapes quotes, backslashes, control characters,
  invisible formatting characters and unpaired surrogates. Truncated names no longer split surrogate pairs.
- Now references `Microsoft.Extensions.AI.Abstractions` 10.11.0.

### Compatibility

- **Behaviour change:** empty built-in schemas (`guest`, `INFORMATION_SCHEMA`, `sys` and the fixed database-role
  schemas such as `db_owner`) no longer appear in schema lists or inventories. Built-in schemas containing captured
  objects are still listed. Set `IncludeBuiltInSchemas = true` for the previous behaviour.
- Dependency counts can increase where the fixes above add previously missing edges.
- No public APIs were removed; package validation checks both packages against 0.5.1.

## 0.5.1

- First release of **MAFUtils.Tools**: offline inspection of tool names, descriptions and input schemas.
- MAFUtils.SqlServer: the package now embeds its own consumer README, and its project URL links to it.
- MAFUtils.SqlServer: `RecommendedInstructions` is formatted as one instruction per line.

## 0.5.0

- Documentation-only release of MAFUtils.SqlServer; no library changes from 0.4.0.

## 0.4.0

- Bulk-loaded, immutable session metadata catalogs with explicit atomic refresh.
- Complete schema/object inventories and in-memory object details.
- Direct/transitive dependency counts and shortest paths with cycle handling.
- NuGet metadata, XML documentation, portable symbols and publishing workflow.

### Compatibility

Requires .NET 10. The agent tool surface changed from 0.3.x: use
`GetDatabaseInventory`, `DescribeObject` and `QueryDependencies`.
Existing service methods now read from a cached snapshot; refresh explicitly
after schema changes. Schema policy applies consistently to schemas and objects.

### Known issues

The three known issues listed for this release (current-database-qualified references, constraint function
references and surrogate pairs split by definition slices) are fixed in 0.6.0.
