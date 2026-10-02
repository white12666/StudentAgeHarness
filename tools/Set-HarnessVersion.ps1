<#
.SYNOPSIS
    修改 harness 的版本号（Directory.Build.props 里唯一的 <Version>）。插件、程序集和报告里的版本都从它生成。

.EXAMPLE
    .\tools\Set-HarnessVersion.ps1 -Bump patch
    .\tools\Set-HarnessVersion.ps1 -Version 0.2.0
#>
param(
    [string]$Version,
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($Version) -eq [string]::IsNullOrEmpty($Bump)) { throw '请只指定 -Version 或 -Bump 其中一个。' }

$props = Join-Path (Split-Path $PSScriptRoot -Parent) 'Directory.Build.props'
$text = [IO.File]::ReadAllText($props)
$match = [regex]::Match($text, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if (-not $match.Success) { throw ('Directory.Build.props 里找不到 <Version>x.y.z</Version>：' + $props) }
$major = [int]$match.Groups[1].Value
$minor = [int]$match.Groups[2].Value
$patch = [int]$match.Groups[3].Value
$old = '{0}.{1}.{2}' -f $major, $minor, $patch

if ($Bump) {
    switch ($Bump) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        'patch' { $patch++ }
    }
    $Version = '{0}.{1}.{2}' -f $major, $minor, $patch
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw ('版本号要写成 x.y.z：' + $Version) }
if ($Version -eq $old) { throw ('新版本号和当前版本一样：' + $old) }

$updated = $text.Substring(0, $match.Index) + '<Version>' + $Version + '</Version>' + $text.Substring($match.Index + $match.Length)
[IO.File]::WriteAllText($props, $updated, (New-Object Text.UTF8Encoding($false)))
Write-Output ('{0} -> {1}' -f $old, $Version)
