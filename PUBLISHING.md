# Publishing MAFUtils.SqlServer

The publishable package is **MAFUtils.SqlServer**, targeting **.NET 10**.
The placeholder Core project and test project are not packable.
Pushing a version tag such as v0.4.0 automatically validates and publishes that
version. Branch pushes, pull requests and manual validation runs do not publish.

## Build and validate locally

Install a .NET 10 SDK and PowerShell 7. Configure the SQL test connection using
MAFUTILS_TEST_SQLSERVER or the ignored MAFUtils/local.runsettings file.
Tests create/drop their own GUID-named database; use a test-server identity with
CREATE DATABASE permission. For example:

~~~powershell
$env:MAFUTILS_TEST_SQLSERVER = 'Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
./scripts/Build-Package.ps1
# Optional prerelease build without editing the project's default version:
./scripts/Build-Package.ps1 -Version 0.4.0-rc.1
~~~

The script restores, builds, runs all tests, rejects skipped tests, packs, checks
package metadata/content and portable symbols, and compiles/runs a separate
consumer against the generated package using an isolated package cache.
Output is in artifacts/nuget/VERSION:

- MAFUtils.SqlServer.VERSION.nupkg
- MAFUtils.SqlServer.VERSION.snupkg

The package contains the assembly, available XML documentation, README, MIT
license and changelog. Symbols use portable PDBs and the SDK's Source Link support;
repository commit metadata is supplied by the Git checkout. Commit the release
sources before making the distributable release build. Local settings and test
fixtures are not package content. Build output and local.runsettings are ignored.

The default local/branch-build version is in MAFUtils/MAFUtils.SqlServer/MAFUtils.SqlServer.csproj.
For a tagged release, the tag supplies the package version (v0.4.1 produces 0.4.1),
overriding the project default for both the build and package.
Use a new version for every publication; NuGet package versions cannot be replaced.
Review CHANGELOG.md, including the outstanding correctness issues from the code
review, before choosing a production release.

## Configure GitHub and NuGet.org once

The workflow uses [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing),
which obtains a short-lived API key using GitHub OIDC. It does not require a
stored long-lived NuGet API key.

1. Commit and push the library, tests, scripts and .github/workflows/nuget.yml to
   deanchalk/MAFUtils. The workflow must exist on main for manual dispatch.
2. Sign in to the NuGet.org account that will own the package. Verify the
   MAFUtils.SqlServer ID is available or that this account has publishing rights.
3. Create a GitHub environment named **nuget** under repository Settings →
   Environments. Under Deployment branches and tags, select Selected branches and
   tags and add a **Tag** rule matching **v***. Remove a previous main-only rule if
   it prevents tag deployments. Leave required reviewers and wait timers disabled
   if releases should proceed without any manual approval.
4. Add environment secret **NUGET_USER** containing the NuGet.org profile username,
   not an email address or API key.
5. In NuGet.org Trusted Publishing, add a policy with:
   - Repository owner: deanchalk
   - Repository: MAFUtils
   - Workflow file: nuget.yml (filename only)
   - Environment: nuget
   - Package scope: MAFUtils.SqlServer
   - Allow creation of a new package for the first publication, and new versions
     for subsequent publications.

The account/environment configuration is not performed by repository files.

## Publish

After committing and pushing the workflow changes, tag the commit you want to
release and push that specific tag. For example, from the intended release commit:

~~~powershell
git tag v0.4.0
git push origin v0.4.0
~~~

This publishes version 0.4.0 automatically after validation succeeds. Use
v0.4.1 for the next version, or v0.4.1-rc.1 for a prerelease. The tag must point
to a commit containing this version-tag workflow. Creating a tag locally without
pushing it does not start a GitHub run. Do not move or reuse published version tags.

GitHub Actions → NuGet → Run workflow remains available for validation only.

The Windows validation job starts LocalDB, runs the same package script, and
uploads the validated package/symbol pair. The publish job downloads those exact
artifacts, obtains the OIDC credential, then pushes to NuGet.org. Only a push of a
version tag can enter the publish job. Invalid version tags fail validation. Duplicate
versions fail instead of silently accepting a different existing package.

NuGet ingestion/indexing is asynchronous: a successful push does not mean the
version is immediately searchable. In NuGet.org, select your username → Manage
packages and check the unpublished/unlisted section for its validation status.
NuGet sends an email when validation completes or fails. Processing usually takes
less than 15 minutes; see [NuGet validation and indexing](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package#package-validation-and-indexing).

The root README.md is also embedded in the NuGet package. Repository documentation
edits appear on GitHub when pushed, but the README inside an existing package does
not change. Include documentation updates in the next version tag to ship them
with the package; do not reuse an existing version.

## Optional manual publication

If you prefer a scoped NuGet.org API key instead of GitHub Trusted Publishing,
provide it as NUGET_API_KEY in your local shell, then run the following only when
you intend to publish the already validated package:

~~~powershell
dotnet nuget push ./artifacts/nuget/0.4.0/MAFUtils.SqlServer.0.4.0.nupkg --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json
~~~

Keep the matching .snupkg beside the .nupkg; dotnet push also submits the symbols.
Never put credentials in source files, runsettings committed to Git, or command
examples containing literal keys.
