# Changelog

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

### Known issues from the code review

- References explicitly qualified with the current database name are omitted
  from dependency counts.
- Function references from CHECK/DEFAULT constraints are not mapped to their
  parent table in the dependency graph.
- Definition slices can split UTF-16 surrogate pairs at page boundaries.

These issues have not been fixed by the packaging setup. See the README for
additional metadata coverage and visibility limitations.
