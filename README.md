# MAFUtils

SQL Server metadata catalogs and dependency analysis for Microsoft Agent Framework and ordinary .NET applications.
Use the [MAFUtils.SqlServer NuGet package](https://www.nuget.org/packages/MAFUtils.SqlServer) in .NET 10 applications
to explore database structure, list schemas and tables, and find direct or transitive dependencies.
It works with ordinary .NET code and exposes function tools for AI agents.

The service loads database metadata into an immutable, session-scoped in-memory catalog. It builds incoming and
outgoing dependency indexes at load time. Subsequent searches, inventories, descriptions and dependency queries
use memory, not database round trips. Application data rows are never loaded, and model-generated SQL is never executed.

## Install

From your .NET 10 application directory:

```powershell
dotnet add package MAFUtils.SqlServer --version 0.4.0
```

You need a SQL Server connection whose identity can read the required metadata, including database
`VIEW DEFINITION` and `SELECT` on `sys.sql_expression_dependencies`. See
[Captured metadata and visibility](#captured-metadata-and-visibility) for scope and permission details.
No model provider or Agent Framework package is required for direct .NET use.
Review the [release notes and known issues](https://github.com/deanchalk/MAFUtils/blob/main/CHANGELOG.md)
when evaluating dependency results.

## Quick start: list schemas and tables

Set `SQLSERVER_CONNECTION_STRING` in your application's environment, then use:

```csharp
using MAFUtils.SqlServer;

var connectionString = Environment.GetEnvironmentVariable("SQLSERVER_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set SQLSERVER_CONNECTION_STRING first.");
var metadata = new SqlServerMetadataService(connectionString);

// One call returns every visible, permitted schema and its tables, including empty schemas.
var inventory = await metadata.GetDatabaseInventoryAsync(kind: SqlObjectKind.Table);
foreach (var schema in inventory.Schemas)
{
    var tables = schema.Objects.Count == 0
        ? "(no visible tables)"
        : string.Join(", ", schema.Objects.Select(table => table.Name));
    Console.WriteLine($"{schema.Schema}: {tables}");
}
```

Keep the service for subsequent questions; it reuses the loaded catalog. Call
`await metadata.RefreshCatalogAsync()` when you want to capture database schema changes.

## Use with Microsoft Agent Framework

Add the Agent Framework package to your host application (the integration tests use version `1.24.0`):

```powershell
dotnet add package Microsoft.Agents.AI --version 1.24.0
```

Also install and configure your chosen model provider's `IChatClient` implementation. The example below
assumes your application supplies `chatClient`, `connectionString` and `cancellationToken`.

```csharp
using MAFUtils.SqlServer;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

var metadata = new SqlServerMetadataService(connectionString, new SqlServerDiscoveryOptions
{
    AllowedSchemas = [], // Null or empty: all visible schemas. Use exact names to restrict scope.
    IncludeDefinitions = true, // Eagerly cache complete SQL module text; optional.
    CommandTimeoutSeconds = 30,
    MaxPageSize = 100
});

// Recommended: load before the first chat message so initialization has visible progress.
var catalog = await metadata.GetCatalogAsync(cancellationToken);
Console.WriteLine($"Loaded {catalog.Status.ObjectCount} objects at {catalog.Status.CapturedAt:O}");

AIAgent agent = chatClient.AsAIAgent(
    name: "DatabaseExplorer",
    instructions: SqlServerDiscoveryTools.RecommendedInstructions,
    tools: [.. SqlServerDiscoveryTools.Create(metadata)]);

// Retain both the metadata service and the framework conversation session for follow-up questions.
var session = await agent.CreateSessionAsync();
Console.WriteLine(await agent.RunAsync(
    "Give me a grid containing all schemas and their tables", session));
Console.WriteLine(await agent.RunAsync(
    "Which procedures depend on sales.Customers, including through views or functions?", session));
```

Create a service per exploration session and keep its database, identity and allow lists stable. Do not put one
unscoped static catalog behind multiple users. Authentication factories must return a fresh unopened connection;
the loader owns/disposes it. SqlClient handles pooling. No Agent Framework runtime or model provider is imposed by
the library; only the function-tool adapter depends on Microsoft.Extensions.AI.

First use loads lazily if `GetCatalogAsync` was not called. Concurrent first callers share one successful load.
Canceled/failed initialization is not published and can be retried. No background refresh or time-based expiry occurs.
Call `RefreshCatalogAsync` explicitly after schema changes. It builds a replacement before publishing it atomically;
existing readers keep their previous snapshot, and failed/canceled refresh leaves the previous snapshot usable.
A refresh discards the old snapshot's cached dependency traversals once no references to it remain.

## Agent tools

| Tool | Purpose |
| --- | --- |
| `GetDatabaseInventory` | Complete grouped inventory, including empty permitted schemas. Optional Table/View/Procedure/Function filter. One call answers schema/table grids; no model-controlled enumeration loop. |
| `ListDatabaseObjects` | Literal name search, optional schema/kind filters, paged results. Omitted schema means all permitted schemas. |
| `DescribeObject` | Complete captured columns, parameters, indexes, constraints, expressions and incoming/outgoing foreign keys for one table/view/routine. Module text is separate. |
| `QueryDependencies` | Direct/transitive incoming/outgoing references, exact total count within the known graph, optional shortest paths and paged matches. |
| `GetCatalogStatus` | Snapshot ID, capture interval, object/edge counts and coverage limitations. |
| `RefreshCatalog` | Explicit reload; not needed between ordinary questions. |
| `GetObjectDefinition` | Only registered when definitions are enabled. Character slices over fully cached module text, with continuation. |

Tools return `DiscoveryToolResult<T>` with `Data`, or `ErrorCode`/`Error`. Errors are not empty results. SQL exception
details and connection credentials are not returned to the model. Cancellation and unexpected host/programming
errors propagate. Successful results carry snapshot provenance directly or in a `CatalogAnswer<T>` envelope.

Only requested results go to the model; the complete catalog stays in application memory. Inventory intentionally
returns every matching name/kind without truncation, and descriptions contain all captured structural details.
For huge databases, render inventories directly as grids/downloads or narrow the request before model delivery.
`IsComplete` means complete within the snapshot's stated visibility scope, not proof of unrestricted database access.
A host rendering `DatabaseInventory.Schemas` directly does not depend on the model reproducing every row faithfully.

## Dependency graph

```csharp
// Procedures directly referencing (normally calling) a procedure.
var callers = await metadata.QueryDependenciesAsync("sales", "ProcessOrder",
    kind: SqlObjectKind.Procedure, cancellationToken: cancellationToken);
Console.WriteLine(callers.TotalCount); // Already counts all matches, not just this page.

// Procedures ultimately depending on a table, through views, functions or other procedures.
var dependants = await metadata.QueryDependenciesAsync("sales", "Customers",
    transitive: true, kind: SqlObjectKind.Procedure, includePaths: true,
    cancellationToken: cancellationToken);

// Everything that a routine ultimately references.
var dependencies = await metadata.QueryDependenciesAsync("sales", "ProcessOrder",
    direction: DependencyDirection.Outgoing, transitive: true,
    cancellationToken: cancellationToken);
```

The catalog is a directed graph, not a tree: shared dependencies, recursion and cycles are normal. Traversal is
cycle-safe, excludes the starting object, deduplicates matches and records a shortest path. Kind filtering happens
**after** traversal so a procedure can be discovered through an intermediate view/function. Both endpoints and every
intermediate node must be in the permitted catalog. Paths follow reference direction, from caller to referenced object.
`TotalCount` covers all matching nodes regardless of paging; `Matches.NextOffset` retrieves further names/paths.
Incoming/outgoing adjacency indexes are built once. Traversal maps are cached by target/direction/transitivity with a
128-entry eviction threshold; maps retain predecessors rather than eagerly materializing all possible paths.
The implementation does not precompute an O(N²) transitive closure for every object pair.

Edges come from [sys.sql_expression_dependencies](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sql-expression-dependencies-transact-sql).
They represent known persisted SQL expression references, not proof of runtime execution. Dynamic SQL, unresolved or
caller-dependent references, cross-database/server references and unsupported/excluded objects are not traversed.
Foreign keys are modeled separately. Empty dependency results do not prove that an object is unused. These limitations
are included in dependency responses. No regex inference or procedure execution is used to invent missing edges.

## Captured metadata and visibility

Bulk extraction uses one connection and a set-based batch, not one query per object. Response row/text limits never
truncate the underlying catalog. Supported metadata includes:

- User tables, views, SQL/CLR procedures and functions, with schema-qualified names and descriptions.
- Columns, SQL types, nullability, identity/computed flags, computed/default expressions and descriptions.
- Routine parameters, scalar return metadata, table-valued function columns.
- Indexes, index columns, primary/unique keys, filters, check/default constraints and foreign-key column pairs.
- Resolved expression dependencies between supported objects.
- Complete available SQL module definitions when `IncludeDefinitions` is enabled.

This is a structural discovery catalog, not a full database backup or DDL scripting engine. Synonyms, triggers,
sequences, table-type members, permissions and physical/storage configuration are not modeled. Procedure result sets
and T-SQL parameter defaults are not inferred. Catalog lengths are bytes (`nvarchar(80)` is 160); -1 means MAX.
Encrypted/inaccessible definitions and objects without SQL modules have null definition text. Tables have no module DDL.

`AllowedSchemas`: null or empty means no restriction. A non-empty collection applies to **both schemas and objects**.
`AllowedObjects`: null means unrestricted; empty means none. When set, only schemas containing permitted supported
objects appear. Active policies intersect and are copied on construction. They use exact ordinal names, independently
of SQL collation, with a combined limit of 500 configured names (each object counts as two).
Lookups use exact names returned by the catalog; name substring search is ordinal case-insensitive and treats `%`/`_`
literally. The in-memory matching semantics do not follow database collation.

SQL identity permissions still determine visibility. Complete dependency extraction requires database `VIEW DEFINITION`
and `SELECT` on `sys.sql_expression_dependencies`, even when module text is disabled. Missing catalog permissions may
fail the entire load rather than publish a partial snapshot. Other permission-limited metadata can appear absent;
see [SQL Server metadata visibility](https://learn.microsoft.com/en-us/sql/relational-databases/security/metadata-visibility-configuration).
Definitions, expressions, descriptions and type names can themselves mention objects outside the allow list; their text
is not redacted. Treat all database text as untrusted data.

A published snapshot is stable, but its extraction spans multiple catalog reads and is **not** a transactional DDL
snapshot. Concurrent schema deployment can affect capture. Load during a stable period and refresh after deployment;
status includes start/end times. All captured metadata consumes application memory; provision for database size and
concurrent sessions. Definitions can be disabled to reduce memory use.

## Compatibility with 0.3.x

Existing `SqlServerMetadataService` constructors and list/describe methods remain available, but now read from the session
snapshot. Calls no longer automatically observe schema changes. `ListSchemasAsync` now respects the same schema policy
as object queries. Search/lookup semantics are explicit in-memory semantics as described above.

Legacy `DescribeTableAsync`, `DescribeViewAsync`, `DescribeProcedureAsync`, `DescribeFunctionAsync` and
`GetObjectDefinitionAsync` retain their bounded response behaviour and truncation flags. `MaxDetailRows`, `MaxTextLength`
and `MaxDefinitionLength` control those compatibility responses, not the loader. Use `DescribeObjectAsync` or
`catalog.Describe(...)` for complete details. Agent `DescribeObject` returns complete structure, and the definition tool
supports slices instead of irreversible truncation. Default list pages are min(50, MaxPageSize); the default host maximum
is 100 and configured values may be at most 1000.

The agent tool surface has changed: `GetDatabaseInventory` replaces schema-by-schema discovery, `DescribeObject` replaces
four type-specific describe tools, and `QueryDependencies` replaces the direct-only reference tool. Recreate your agent's
tool collection and update prompts to `RecommendedInstructions`. No application row query tool has been introduced.

## Build, test and pack

Repository contributors need the .NET 10 SDK. `MAFUtils.SqlServer` is the library and NuGet package;
`MAFUtils.SqlServer.Tests` contains its tests, and `MAFUtils.Core` is a placeholder for future shared utilities.

```powershell
dotnet build MAFUtils/MAFUtils.slnx -c Release
dotnet test MAFUtils/MAFUtils.slnx -c Release
dotnet pack MAFUtils/MAFUtils.SqlServer/MAFUtils.SqlServer.csproj -c Release -o artifacts
```

SQL integration tests use `MAFUTILS_TEST_SQLSERVER`, or the existing `MAFUtils/local.runsettings` file selected by the test
project. Without either, SQL tests are skipped. The fixture creates and drops its own `MAFUtilsTests_<guid>` database and
requires a test-server identity allowed to do so. It never uses the configured database as its fixture database.

Coverage includes complete inventories above page limits, full cached details despite legacy limits, definition slices,
single-flight initialization, explicit refresh/failure recovery, policy enforcement, composite relationships, cyclic and
diamond dependency graphs, intermediate views/functions, distinct counts independent of paging, literal search, unusual
identifiers, cancellation and invocation through Agent Framework. The scripted agent tests validate plumbing and response
contracts; they do not replace evaluations with a real model.

For the full package validation script and maintainer release instructions, see
[PUBLISHING.md](https://github.com/deanchalk/MAFUtils/blob/main/PUBLISHING.md).
Pushing a version tag publishes that version after validation; ordinary branch pushes do not publish.

## License

MAFUtils is distributed under the [MIT license](https://github.com/deanchalk/MAFUtils/blob/main/LICENSE).
