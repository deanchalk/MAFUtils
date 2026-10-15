#Requires -Version 7.0
[CmdletBinding()]
param([string]$Version)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'MAFUtils/MAFUtils.SqlServer/MAFUtils.SqlServer.csproj'
$solution = Join-Path $root 'MAFUtils/MAFUtils.slnx'
if (-not $Version) {
    [xml]$projectXml = Get-Content -LiteralPath $project
    $Version = [string]$projectXml.Project.PropertyGroup.Version
}
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
    throw 'Version must be major.minor.patch with an optional prerelease suffix, e.g. 0.4.0-rc.1.'
}
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
$output = Join-Path $root "artifacts/nuget/$Version"
$validation = Join-Path $root ('artifacts/validation/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $output,$validation | Out-Null
Push-Location $root
try {
    Invoke-Dotnet restore $solution
    Invoke-Dotnet build $solution -c Release --no-restore "-p:Version=$Version"
    Invoke-Dotnet test $solution -c Release --no-build --no-restore --logger 'trx;LogFileName=tests.trx' --results-directory $validation
    [xml]$results = Get-Content -LiteralPath (Join-Path $validation 'tests.trx')
    $counts = $results.TestRun.ResultSummary.Counters
    if ([int]$counts.total -eq 0 -or [int]$counts.executed -ne [int]$counts.total -or [int]$counts.failed -ne 0) {
        throw 'Release validation requires every test to run and pass. Configure MAFUTILS_TEST_SQLSERVER or local.runsettings; skipped SQL tests are not accepted.'
    }
    Invoke-Dotnet pack $project -c Release --no-build --no-restore "-p:Version=$Version" --output $output

    $package = Join-Path $output "MAFUtils.SqlServer.$Version.nupkg"
    $symbols = Join-Path $output "MAFUtils.SqlServer.$Version.snupkg"
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package)
    try {
        $names = @($zip.Entries.FullName)
        foreach ($required in @('MAFUtils.SqlServer.nuspec','README.md','LICENSE','CHANGELOG.md',
            'lib/net10.0/MAFUtils.SqlServer.dll','lib/net10.0/MAFUtils.SqlServer.xml')) {
            if ($required -notin $names) { throw "Package missing $required" }
        }
        if ($names.Where({ $_ -match '(?i)(\.runsettings$|\.cs$|\.csproj$|appsettings.*\.json$|\.env$)' }).Count -gt 0) {
            throw 'Unexpected source/configuration files in package.'
        }
        $reader = [System.IO.StreamReader]::new($zip.GetEntry('MAFUtils.SqlServer.nuspec').Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $manifest.package.metadata
        if ($metadata.id -ne 'MAFUtils.SqlServer' -or $metadata.version -ne $Version -or
            $metadata.authors -ne 'Dean Chalk' -or $metadata.license.InnerText -ne 'MIT' -or
            $metadata.repository.url -ne 'https://github.com/deanchalk/MAFUtils') {
            throw 'Package identity, version, author, license or repository metadata is incorrect.'
        }
    } finally { $zip.Dispose() }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($symbols)
    try {
        if ('lib/net10.0/MAFUtils.SqlServer.pdb' -notin $zip.Entries.FullName) { throw 'Portable symbols are missing.' }
    } finally { $zip.Dispose() }

    # Consume the actual package with a fresh restore directory, not a project reference or cached version.
    $consumer = Join-Path $validation 'consumer'
    New-Item -ItemType Directory $consumer | Out-Null
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><PackageReference Include="MAFUtils.SqlServer" Version="[$Version]" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj')
    @'
using MAFUtils.SqlServer;
var service = new SqlServerMetadataService(() => throw new InvalidOperationException("The smoke test must not connect."));
var names = SqlServerDiscoveryTools.Create(service).Select(t => t.Name).ToArray();
if (!names.Contains("GetDatabaseInventory") || !names.Contains("QueryDependencies"))
    throw new InvalidOperationException("Expected public tools are missing from the package.");
Console.WriteLine("NuGet consumer smoke test passed.");
'@ | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs')
    $escapedOutput = [System.Security.SecurityElement]::Escape($output)
    @"
<configuration>
  <packageSources><clear /><add key="built" value="$escapedOutput" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><packageSource key="built"><package pattern="MAFUtils.SqlServer" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'NuGet.Config')
    $consumerProject = Join-Path $consumer 'Consumer.csproj'
    Invoke-Dotnet restore $consumerProject --configfile (Join-Path $consumer 'NuGet.Config') --packages (Join-Path $validation 'packages')
    Invoke-Dotnet run --project $consumerProject -c Release --no-restore
    Write-Host "Validated packages: $package and $symbols"
} finally { Pop-Location }
