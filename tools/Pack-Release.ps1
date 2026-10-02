<#
.SYNOPSIS
    Package already-built, tested binaries. Does not build, deploy or publish.
#>
param([string]$OutDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$props = [xml][IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'))
$version = [string]$props.Project.PropertyGroup[0].Version
if (-not $OutDirectory) { $OutDirectory = Join-Path $root 'dist' }
$OutDirectory = [IO.Path]::GetFullPath($OutDirectory)
$name = 'StudentAgeHarness-' + $version
$stage = Join-Path $OutDirectory $name
$zip = $stage + '.zip'
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $zip)) {
    throw 'Release already exists. Use a new version or output directory; packages are never overwritten.'
}
$binaries = @(
    @{ source = 'src\StudentAgeHarness\bin\Release\StudentAgeHarness.dll'; target = 'plugin' },
    @{ source = 'src\StudentAgeHarness\bin\Release\StudentAgeHarness.Core.dll'; target = 'plugin' },
    @{ source = 'samples\ExamplePack\bin\Release\StudentAgeHarness.ExamplePack.dll'; target = 'samples' }
)
foreach ($binary in $binaries) {
    $file = Join-Path $root $binary.source
    if (-not (Test-Path -LiteralPath $file)) { throw ('Build output missing: ' + $file) }
    $builtVersion = ((Get-Item -LiteralPath $file).VersionInfo.ProductVersion -split '\+')[0]
    if ($builtVersion -ne $version) { throw ('Stale build: ' + $file) }
}
New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($binary in $binaries) {
    $target = Join-Path $stage $binary.target
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $binary.source) -Destination $target
}
$xmlDocs = Join-Path $root 'src\StudentAgeHarness\bin\Release\StudentAgeHarness.Core.xml'
if (Test-Path -LiteralPath $xmlDocs) { Copy-Item -LiteralPath $xmlDocs -Destination (Join-Path $stage 'plugin') }
New-Item -ItemType Directory -Path (Join-Path $stage 'tools') | Out-Null
foreach ($file in @('sah.ps1', 'sah.cmd', 'RawRegistry.cs')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stage 'tools')
}
foreach ($directory in @('schemas', 'docs', 'skills')) {
    Copy-Item -LiteralPath (Join-Path $root $directory) -Destination $stage -Recurse
}
foreach ($file in @('README.md', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $root 'samples\ExamplePack\ExampleScenarios.cs') -Destination (Join-Path $stage 'samples')
$sampleConfig = @{
    '$schema' = '../schemas/harness.schema.json'
    plugins = @()
    packs = @('StudentAgeHarness.ExamplePack.dll')
    scenarios = @('startup', 'tag:sample')
}
[IO.File]::WriteAllText((Join-Path $stage 'samples\harness.json'), (ConvertTo-Json $sampleConfig -Depth 5),
    (New-Object Text.UTF8Encoding($false)))
# Only our three assemblies may ship; never redistribute game/Unity/BepInEx binaries.
$allowed = @('StudentAgeHarness.dll', 'StudentAgeHarness.Core.dll', 'StudentAgeHarness.ExamplePack.dll')
$dlls = @(Get-ChildItem -LiteralPath $stage -Recurse -Filter '*.dll')
if ($dlls.Count -ne 3 -or @($dlls | Where-Object { $allowed -notcontains $_.Name }).Count -ne 0) {
    throw 'Unexpected assemblies in package.'
}
$manifest = [ordered]@{
    name = 'StudentAge Harness'
    version = $version
    reportSchema = 'studentage-harness-report/2'
    assemblies = @($dlls | ForEach-Object { $_.Name })
}
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), (ConvertTo-Json $manifest -Depth 5),
    (New-Object Text.UTF8Encoding($false)))
Compress-Archive -LiteralPath $stage -DestinationPath $zip
Write-Output ('Package: ' + $zip)
