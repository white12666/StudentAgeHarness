<#
.SYNOPSIS
    StudentAge Harness 启动器：在隔离环境里启动学生时代，自动跑测试场景，生成报告。

.DESCRIPTION
    sah doctor            检查环境（游戏目录、BepInEx、Steam、harness 插件、配置文件）
    sah run               准备隔离运行目录，通过 Steam 启动游戏，跑完场景后生成报告
    sah report            显示最近一次（或 -Run 指定）运行的报告摘要
    sah clean             删除旧的运行目录（默认保留最近 20 个）
    sah restore           手动恢复上次被中断的运行留下的注册表快照
    sah init              在当前目录生成 harness.json 模板

    退出码：0 全部通过；1 有测试失败；2 运行没有完成（游戏没启动、插件没启用、超时没有报告）；3 用法或环境问题。

    窗口默认正常显示，可以切到别的窗口或把游戏最小化，测试照常进行。-Background 把窗口藏到桌面下方，不打扰正常使用电脑。

.EXAMPLE
    sah run
    sah run -Plugin .\bin\Release\MyMod.dll -Scenario startup,new-game -Background
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('doctor', 'run', 'report', 'clean', 'restore', 'init', 'help')]
    [string]$Command = 'help',
    [string]$Config,
    [string[]]$Plugin,
    [string[]]$Pack,
    [string[]]$Scenario,
    [switch]$Visible,
    [switch]$Background,
    [string]$Resolution,
    [int]$TimeoutSec = 0,
    [switch]$Strict,
    [string]$GameRoot,
    [string]$Run,
    [int]$Keep = -1,
    [switch]$Delete,
    [switch]$Open
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$script:AppId = '1991040'
$script:RegistrySubKey = 'Software\PakyiGame\StudentAge'
$script:RunFormat = 'studentage-harness-run/1'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:HarnessRoot = Split-Path $PSScriptRoot -Parent
$script:RecoveryDirectory = Join-Path $env:LOCALAPPDATA 'StudentAgeHarness\recovery'

$ExitPassed = 0
$ExitFailed = 1
$ExitIncomplete = 2
$ExitUsage = 3

# ======================== 输出 ========================

function Write-Line([string]$Tag, [string]$Text, [ConsoleColor]$Color) {
    Write-Host ('[' + $Tag + '] ') -ForegroundColor $Color -NoNewline
    Write-Host $Text
}
function Write-Pass([string]$Text) { Write-Line '通过' $Text Green }
function Write-Fail([string]$Text) { Write-Line '失败' $Text Red }
function Write-Warn([string]$Text) { Write-Line '警告' $Text Yellow }
function Write-Note([string]$Text) { Write-Line '提示' $Text Cyan }

function Exit-Usage([string]$Message) {
    Write-Fail $Message
    exit $ExitUsage
}

# ======================== 工具函数 ========================

function Get-Opt($Object, [string]$Name, $Default) {
    if ($null -eq $Object) { return $Default }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name) -and $null -ne $Object[$Name]) { return $Object[$Name] }
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, $script:Utf8)
}

function Read-Json([string]$Path) {
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Resolve-FullPath([string]$Path, [string]$BaseDirectory) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim())
    if (-not [IO.Path]::IsPathRooted($expanded)) { $expanded = Join-Path $BaseDirectory $expanded }
    return [IO.Path]::GetFullPath($expanded)
}

function Test-SafePath([string]$Path) {
    return ($Path -notmatch '\s') -and ($Path -notmatch '[^\x00-\x7F]')
}

function Get-HarnessVersion([string]$PluginDirectory) {
    $dll = Join-Path $PluginDirectory 'StudentAgeHarness.dll'
    if (-not (Test-Path -LiteralPath $dll)) { return $null }
    $version = (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion
    if ($version) { return ($version -split '\+')[0] }
    return (Get-Item -LiteralPath $dll).VersionInfo.FileVersion
}

# ======================== 配置 ========================

function Get-ConfigPath {
    if ($Config) {
        $path = Resolve-FullPath $Config (Get-Location).Path
        if (-not (Test-Path -LiteralPath $path)) { Exit-Usage ('找不到配置文件：' + $path) }
        return $path
    }
    $default = Join-Path (Get-Location).Path 'harness.json'
    if (Test-Path -LiteralPath $default) { return [IO.Path]::GetFullPath($default) }
    return $null
}

function Read-HarnessConfig([string]$Path) {
    if (-not $Path) { return $null }
    try {
        $value = Read-Json $Path
        $allowed = @('$schema', 'gameRoot', 'runsDir', 'plugins', 'packs', 'dependencies', 'patchers',
            'configFiles', 'scenarios', 'expectPlugins', 'resolution', 'window', 'mute', 'timeoutSec',
            'startupTimeoutSec', 'strict', 'redactPaths', 'keepRuns', 'workshopMods')
        if (-not ($value -is [pscustomobject])) { throw 'Configuration must be a JSON object.' }
        foreach ($property in $value.PSObject.Properties) {
            if ($allowed -notcontains $property.Name) { throw ('Unknown configuration field: ' + $property.Name) }
        }
        foreach ($field in @('plugins', 'packs', 'dependencies', 'patchers', 'configFiles', 'scenarios', 'expectPlugins')) {
            $list = Get-Opt $value $field $null
            if ($null -ne $list) {
                # Read the property itself: PowerShell functions unwrap one-element arrays.
                $list = $value.PSObject.Properties[$field].Value
                if (-not ($list -is [array])) { throw ($field + ' must be an array of strings.') }
                foreach ($item in $list) {
                    if (-not ($item -is [string]) -or [string]::IsNullOrWhiteSpace($item)) { throw ($field + ' contains an invalid item.') }
                }
            }
        }
        foreach ($field in @('mute', 'strict', 'redactPaths')) {
            $setting = Get-Opt $value $field $null
            if ($null -ne $setting -and -not ($setting -is [bool])) { throw ($field + ' must be boolean.') }
        }
        return $value
    }
    catch { Exit-Usage ('配置文件不是合法的 JSON：' + $Path + '。' + $_.Exception.Message) }
}

function Get-WindowMode($ConfigObject, [bool]$VisibleSwitch, [bool]$BackgroundSwitch) {
    if ($VisibleSwitch -and $BackgroundSwitch) { throw '-Visible 和 -Background 不能同时使用。' }
    if ($VisibleSwitch) { return 'visible' }
    if ($BackgroundSwitch) { return 'background' }
    $mode = [string](Get-Opt $ConfigObject 'window' 'visible')
    if (@('visible', 'background') -notcontains $mode) { throw ('window 只能是 visible 或 background：' + $mode) }
    return $mode
}

function Get-WorkshopSelection($ConfigObject) {
    $property = if ($null -ne $ConfigObject) { $ConfigObject.PSObject.Properties['workshopMods'] } else { $null }
    if ($null -eq $property -or $null -eq $property.Value) { return ,@() }
    $value = $property.Value
    if ($value -is [string]) {
        if ($value -eq 'inherit') { return $value }
        if ($value -eq 'none') { return ,@() }
        throw 'workshopMods must be an ID array, inherit or none.'
    }
    if (-not ($value -is [array])) { throw 'workshopMods must be an array.' }
    $ids = @()
    foreach ($item in $value) {
        $id = [string]$item
        $number = [uint64]0
        if ($id -notmatch '^[1-9][0-9]*$' -or -not [uint64]::TryParse($id, [ref]$number)) {
            throw 'Invalid Workshop item ID.'
        }
        $ids += $id
    }
    return ,$ids
}

# ======================== 查找游戏和 Steam ========================

function Get-SteamDirectory {
    foreach ($candidate in @(
            @{ Path = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
            @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
            @{ Path = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' })) {
        try {
            $value = (Get-ItemProperty -Path $candidate.Path -Name $candidate.Name -ErrorAction Stop).($candidate.Name)
            if ($value -and (Test-Path -LiteralPath (Join-Path $value 'steam.exe'))) { return [IO.Path]::GetFullPath($value) }
        }
        catch { }
    }
    return $null
}

function Find-GameInSteamLibraries {
    $steam = Get-SteamDirectory
    if (-not $steam) { return $null }
    $libraries = New-Object System.Collections.Generic.List[string]
    $libraries.Add($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        foreach ($match in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw -Encoding UTF8), '"path"\s+"([^"]+)"')) {
            $libraries.Add($match.Groups[1].Value.Replace('\\', '\'))
        }
    }
    foreach ($library in $libraries) {
        $manifest = Join-Path $library ('steamapps\appmanifest_' + $script:AppId + '.acf')
        if (-not (Test-Path -LiteralPath $manifest)) { continue }
        $installDir = [regex]::Match((Get-Content -LiteralPath $manifest -Raw -Encoding UTF8), '"installdir"\s+"([^"]+)"')
        if (-not $installDir.Success) { continue }
        $root = Join-Path $library ('steamapps\common\' + $installDir.Groups[1].Value)
        if (Test-GameRoot $root) { return [IO.Path]::GetFullPath($root) }
    }
    return $null
}

function Test-GameRoot([string]$Root) {
    return $Root -and (Test-Path -LiteralPath (Join-Path $Root 'StudentAge.exe')) -and
        (Test-Path -LiteralPath (Join-Path $Root 'StudentAge_Data\Managed\Assembly-CSharp.dll'))
}

function Find-GameRoot($ConfigObject, [string]$ConfigDirectory) {
    $candidates = @()
    if ($GameRoot) { $candidates += @{ Path = (Resolve-FullPath $GameRoot (Get-Location).Path); Source = '命令行 -GameRoot' } }
    $configured = Get-Opt $ConfigObject 'gameRoot' $null
    if ($configured -and $configured -ne 'auto') { $candidates += @{ Path = (Resolve-FullPath $configured $ConfigDirectory); Source = 'harness.json 的 gameRoot' } }
    if ($env:STUDENTAGE_GAME_ROOT) { $candidates += @{ Path = (Resolve-FullPath $env:STUDENTAGE_GAME_ROOT (Get-Location).Path); Source = '环境变量 STUDENTAGE_GAME_ROOT' } }
    foreach ($candidate in $candidates) {
        if (Test-GameRoot $candidate.Path) { return $candidate }
        Exit-Usage ($candidate.Source + ' 指向的不是学生时代的安装目录：' + $candidate.Path)
    }
    # 仓库放在 <游戏目录>\_modsrc\<仓库> 时直接用。
    $nearby = [IO.Path]::GetFullPath((Join-Path $script:HarnessRoot '..\..'))
    if (Test-GameRoot $nearby) { return @{ Path = $nearby; Source = 'harness 所在位置' } }
    $steamGame = Find-GameInSteamLibraries
    if ($steamGame) { return @{ Path = $steamGame; Source = 'Steam 库' } }
    return $null
}

function Find-HarnessPlugin {
    foreach ($candidate in @((Join-Path $script:HarnessRoot 'plugin'), (Join-Path $script:HarnessRoot 'src\StudentAgeHarness\bin\Release'))) {
        if ((Test-Path -LiteralPath (Join-Path $candidate 'StudentAgeHarness.dll')) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'StudentAgeHarness.Core.dll'))) { return [IO.Path]::GetFullPath($candidate) }
    }
    return $null
}

function Get-DoorstopInfo([string]$Root) {
    $info = @{ Installed = $false; Version = $null; Major = 0; Enabled = $false; Problem = $null }
    $proxy = Join-Path $Root 'winhttp.dll'
    $ini = Join-Path $Root 'doorstop_config.ini'
    $preloader = Join-Path $Root 'BepInEx\core\BepInEx.Preloader.dll'
    if (-not (Test-Path -LiteralPath $proxy)) { $info.Problem = '游戏目录里没有 winhttp.dll（Doorstop），BepInEx 没有安装。'; return $info }
    if (-not (Test-Path -LiteralPath $preloader)) { $info.Problem = '找不到 BepInEx\core\BepInEx.Preloader.dll，BepInEx 没有安装完整。'; return $info }
    $info.Installed = $true
    $versionFile = Join-Path $Root '.doorstop_version'
    if (Test-Path -LiteralPath $versionFile) { $info.Version = (Get-Content -LiteralPath $versionFile -Raw).Trim() }
    if ($info.Version -match '^(\d+)') { $info.Major = [int]$Matches[1] } else { $info.Major = 4 }
    if (Test-Path -LiteralPath $ini) {
        $text = Get-Content -LiteralPath $ini -Raw
        $info.Enabled = -not ($text -match '(?im)^\s*enabled\s*=\s*false')
    }
    return $info
}

function Get-RunsDirectory($ConfigObject, [string]$ConfigDirectory, [string]$Root) {
    $configured = Get-Opt $ConfigObject 'runsDir' $null
    if ($configured -and $configured -ne 'auto') { return (Resolve-FullPath $configured $ConfigDirectory) }
    return (Join-Path $Root '_harness_runs')
}

# ======================== 注册表快照 ========================

function Initialize-RawRegistry {
    if (-not ('StudentAgeHarness.RawRegistry' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'RawRegistry.cs')
    }
}

function Get-RegistryState {
    Initialize-RawRegistry
    $state = [ordered]@{ exists = $false; values = @() }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:RegistrySubKey)
    if ($null -eq $key) { return $state }
    try {
        $state.exists = $true
        $values = New-Object System.Collections.ArrayList
        foreach ($name in $key.GetValueNames()) {
            [void]$values.Add([StudentAgeHarness.RawRegistry]::Read($key, $name))
        }
        $state.values = @($values)
    }
    finally { $key.Close() }
    return $state
}

function Save-RegistrySnapshot([string]$Path) {
    $state = Get-RegistryState
    $snapshot = [ordered]@{
        format = 'studentage-harness-registry/2'
        key = 'HKCU\' + $script:RegistrySubKey
        savedAtUtc = [DateTime]::UtcNow.ToString('o')
        exists = $state.exists
        values = $state.values
    }
    Write-Utf8 $Path (ConvertTo-Json $snapshot -Depth 6)
}

function Get-RegistryFingerprint($State) {
    $lines = foreach ($value in @($State.values)) {
        if ($null -eq $value) { continue }
        $data = Get-Opt $value 'data' ''
        if ($data -is [array]) { $data = $data -join "`n" }
        (Get-Opt $value 'name' '') + '|' + (Get-Opt $value 'type' '') + '|' + $data
    }
    return ((@($lines) | Sort-Object) -join "`n") + '|exists=' + $State.exists
}

function Restore-RegistrySnapshot([string]$Path) {
    Initialize-RawRegistry
    $snapshot = Read-Json $Path
    if ($snapshot.format -cne 'studentage-harness-registry/2' -or
        $snapshot.key -cne ('HKCU\' + $script:RegistrySubKey)) { throw 'Invalid registry snapshot or target mismatch.' }
    $wanted = @{}
    foreach ($value in @($snapshot.values)) {
        if ($null -eq $value -or -not ($value.name -is [string]) -or $wanted.ContainsKey($value.name)) {
            throw 'Invalid or duplicate registry snapshot entry.'
        }
        [void][uint32]::Parse([string]$value.type)
        [void][Convert]::FromBase64String([string]$value.data)
        $wanted[$value.name] = $value
    }
    if (-not $snapshot.exists) {
        $existing = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:RegistrySubKey)
        if ($null -ne $existing) {
            $hasChildren = $existing.SubKeyCount -gt 0
            $existing.Close()
            if ($hasChildren) { throw 'Refusing to remove unexpected registry subkeys.' }
            [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($script:RegistrySubKey, $false)
        }
    }
    else {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($script:RegistrySubKey)
        try {
            foreach ($name in $key.GetValueNames()) { if (-not $wanted.ContainsKey($name)) { $key.DeleteValue($name, $false) } }
            foreach ($value in $wanted.Values) {
                [StudentAgeHarness.RawRegistry]::Write($key, $value.name, [uint32]$value.type, [string]$value.data)
            }
        }
        finally { $key.Close() }
    }
    $after = Get-RegistryState
    $expected = [ordered]@{ exists = [bool]$snapshot.exists; values = @(if ($null -ne $snapshot.values) { $snapshot.values }) }
    return (Get-RegistryFingerprint $after) -eq (Get-RegistryFingerprint $expected)
}

function Set-DisplayPreferences([int]$Width, [int]$Height) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:RegistrySubKey, $true)
    if ($null -eq $key) { return 0 }
    $changed = 0
    try {
        foreach ($name in $key.GetValueNames()) {
            $value = $null
            if ($name -like 'Screenmanager Resolution Width_h*' -or $name -like 'Screenmanager Resolution Window Width_h*') { $value = $Width }
            elseif ($name -like 'Screenmanager Resolution Height_h*' -or $name -like 'Screenmanager Resolution Window Height_h*') { $value = $Height }
            elseif ($name -like 'Screenmanager Fullscreen mode_h*') { $value = 3 }
            if ($null -ne $value -and $key.GetValueKind($name).ToString() -eq 'DWord') {
                $key.SetValue($name, [int]$value, [Microsoft.Win32.RegistryValueKind]::DWord)
                $changed++
            }
        }
    }
    finally { $key.Close() }
    return $changed
}

function Invoke-PendingRestore([string]$RunsDirectory, [switch]$Quiet) {
    $pending = Join-Path $script:RecoveryDirectory 'pending.json'
    if (-not (Test-Path -LiteralPath $pending)) {
        if (-not $Quiet) { Write-Note '没有需要恢复的注册表快照。' }
        return $true
    }
    if (Get-Process -Name 'StudentAge' -ErrorAction SilentlyContinue) {
        Write-Fail '上一次运行被中断，留下了待恢复的注册表快照；游戏正在运行，无法恢复。请先退出游戏再运行 sah restore。'
        return $false
    }
    $info = Read-Json $pending
    $snapshot = [string](Get-Opt $info 'snapshot' '')
    if ((Get-Opt $info 'runId' '') -notmatch '^\d{8}-\d{6}-[a-f0-9]{4}$' -or
        $snapshot -ne (Join-Path $script:RecoveryDirectory ($info.runId + '.json'))) {
        throw 'Invalid recovery marker. Refusing registry restore.'
    }
    if (-not (Test-Path -LiteralPath $snapshot)) {
        Write-Fail ('待恢复的快照文件不见了，无法安全继续：' + $snapshot)
        return $false
    }
    $ok = Restore-RegistrySnapshot $snapshot
    if ($ok) {
        Remove-Item -LiteralPath $pending -Force
        Remove-Item -LiteralPath $snapshot -Force
        Write-Pass ('已恢复运行 ' + (Get-Opt $info 'runId' '?') + ' 之前的游戏注册表设置（分辨率、语言等）。')
    }
    else { Write-Warn ('注册表恢复后和快照不完全一致，快照保留在 ' + $snapshot) }
    return $ok
}

# ======================== 窗口 ========================

function Initialize-Native {
    if ('StudentAgeHarness.Native' -as [type]) { return }
    Add-Type -Namespace StudentAgeHarness -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
public struct RECT { public int Left, Top, Right, Bottom; }
'@
}

function Move-WindowBelowDesktop([IntPtr]$Window) {
    $rect = New-Object StudentAgeHarness.Native+RECT
    if (-not [StudentAgeHarness.Native]::GetWindowRect($Window, [ref]$rect)) { return $false }
    $below = [StudentAgeHarness.Native]::GetSystemMetrics(77) + [StudentAgeHarness.Native]::GetSystemMetrics(79) + 40
    return [StudentAgeHarness.Native]::SetWindowPos($Window, [IntPtr]1, $rect.Left, $below, 0, 0, 0x0011)
}

# ======================== 运行目录 ========================

function Get-PluginAssemblies([string]$Folder) {
    $names = @()
    foreach ($dll in Get-ChildItem -LiteralPath $Folder -Recurse -Filter '*.dll' -File) {
        if ($dll.Name -like 'BepInEx*.dll' -or $dll.Name -eq '0Harmony.dll') { continue }
        # 用了 [BepInPlugin] 的程序集，元数据字符串表里会有这个类型名，不用加载程序集就能认出来。
        if ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll.FullName)).Contains('BepInPlugin')) { $names += $dll.Name }
    }
    return ,@($names | Sort-Object)
}

function Copy-PluginItem([string]$Source, [string]$PluginsDirectory, [hashtable]$UsedNames) {
    $isFolder = Test-Path -LiteralPath $Source -PathType Container
    $leaf = Split-Path $Source -Leaf
    $displayName = $leaf
    $folderName = if ($isFolder) { $leaf } else { [IO.Path]::GetFileNameWithoutExtension($leaf) }
    if ($isFolder) {
        # 构建输出目录常叫 netstandard2.0、Release 之类，用插件 DLL 的名字命名更好认，也贴近玩家实际的安装目录。
        $pluginNames = Get-PluginAssemblies $Source
        if ($pluginNames.Count -gt 0) {
            $displayName = $pluginNames -join ', '
            $folderName = [IO.Path]::GetFileNameWithoutExtension($pluginNames[0])
        }
    }
    $unique = $folderName
    $suffix = 2
    while ($UsedNames.ContainsKey($unique)) { $unique = $folderName + '-' + $suffix; $suffix++ }
    $UsedNames[$unique] = $true
    $target = Join-Path $PluginsDirectory $unique
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    if ($isFolder) {
        Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $target -Recurse -Force
        return @{ Path = $target; Kind = 'folder'; Name = $displayName }
    }
    Copy-Item -LiteralPath $Source -Destination $target -Force
    $pdb = [IO.Path]::ChangeExtension($Source, '.pdb')
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $target -Force }
    return @{ Path = (Join-Path $target $leaf); Kind = 'file'; Name = $leaf }
}

function Get-RelativePath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $full.Substring($prefix.Length).Replace('\', '/') }
    return $full
}

# ======================== 报告 ========================

function Get-StatusText([string]$Status) {
    switch ($Status) {
        'passed' { return '通过' }
        'failed' { return '失败' }
        'skipped' { return '跳过' }
        'skipped-allowed' { return '跳过（预期）' }
        default { return $Status }
    }
}

function Get-ReportOutcome($Report, [bool]$StrictMode) {
    if ($null -eq $Report) { return @{ Code = $ExitIncomplete; Text = '没有完成（没有生成报告）' } }
    if ((Get-Opt $Report 'schema' '') -cne 'studentage-harness-report/2') {
        return @{ Code = $ExitIncomplete; Text = '不支持的报告格式' }
    }
    $run = Get-Opt $Report 'run' $null
    $summary = Get-Opt $Report 'summary' $null
    $aborted = [bool](Get-Opt $run 'aborted' $false)
    $reason = [string](Get-Opt $run 'abortReason' '')
    $errors = [int](Get-Opt $summary 'errors' 0)
    $warnings = [int](Get-Opt $summary 'warnings' 0)
    if ($aborted -and @('startup-timeout', 'activation-failed', 'engine-start-failed') -contains $reason) {
        return @{ Code = $ExitIncomplete; Text = ('没有完成（' + $reason + '），' + $errors + ' 个 error') }
    }
    if ($aborted -or [int](Get-Opt $summary 'failedScenarios' 0) -gt 0 -or
        [int](Get-Opt $summary 'skippedSteps' 0) -gt 0) {
        return @{ Code = $ExitFailed; Text = '失败：存在中止、失败场景或未执行的步骤' }
    }
    if ($summary -eq $null -or [int](Get-Opt $summary 'scenarios' 0) -eq 0 -or
        ([int](Get-Opt $summary 'passedScenarios' 0) -eq 0 -and $errors -eq 0) -or
        @(Get-Opt $Report 'scenarios' @() | Where-Object { $_.status -eq 'pending' }).Count -gt 0) {
        return @{ Code = $ExitIncomplete; Text = '没有完成（报告缺少已执行场景）' }
    }
    if ($errors -gt 0) { return @{ Code = $ExitFailed; Text = ('失败：' + $errors + ' 个 error，' + $warnings + ' 个 warning') } }
    if ($StrictMode -and $warnings -gt 0) { return @{ Code = $ExitFailed; Text = ('失败（严格模式）：' + $warnings + ' 个 warning') } }
    return @{ Code = $ExitPassed; Text = ('通过' + $(if ($warnings -gt 0) { '（' + $warnings + ' 个 warning）' } else { '' })) }
}

function Format-Cell([string]$Text, [int]$Max = 160) {
    if ($null -eq $Text) { return '' }
    $clean = ($Text -replace '\r?\n', ' ' -replace '\|', '\|').Trim()
    if ($clean.Length -gt $Max) { $clean = $clean.Substring(0, $Max) + '…' }
    return $clean
}

function Write-ReportMarkdown([string]$RunDirectory, $Report, [hashtable]$Launcher, [bool]$StrictMode) {
    $outcome = Get-ReportOutcome $Report $StrictMode
    if ($Launcher.Problem) { $outcome = @{ Code = $ExitIncomplete; Text = $Launcher.Problem } }
    $lines = New-Object System.Collections.Generic.List[string]
    $runId = Split-Path $RunDirectory -Leaf
    $lines.Add('# StudentAge Harness 报告：' + $runId)
    $lines.Add('')
    $lines.Add('**结果：' + $outcome.Text + '**')
    $lines.Add('')
    if ($null -eq $Report) {
        $lines.Add('游戏没有写出 report.json。请查看运行目录里的 `BepInEx/LogOutput.log` 和 `logs/Player.log`。')
        if ($Launcher.Problem) { $lines.Add(''); $lines.Add('启动器发现的问题：' + $Launcher.Problem) }
        Write-Utf8 (Join-Path $RunDirectory 'report.md') ($lines -join "`r`n")
        return $outcome
    }

    $run = Get-Opt $Report 'run' $null
    $environment = Get-Opt $Report 'environment' $null
    $game = Get-Opt $environment 'game' $null
    $bepinex = Get-Opt $environment 'bepinex' $null
    $harness = Get-Opt $Report 'harness' $null
    $resolution = Get-Opt (Get-Opt $environment 'resolution' $null) 'actual' $null
    $lines.Add(('- 时间：{0} 起，耗时 {1} 秒' -f (Get-Opt $run 'startedAtUtc' '?'), (Get-Opt $run 'durationSec' '?')))
    $lines.Add(('- 版本：游戏 {0} · BepInEx {1} · Harness {2}' -f (Get-Opt $game 'version' '?'), (Get-Opt $bepinex 'version' '?'), (Get-Opt $harness 'version' '?')))
    $lines.Add(('- 窗口：{0}，{1}x{2}' -f (Get-Opt $run 'window' '?'), (Get-Opt $resolution 'width' '?'), (Get-Opt $resolution 'height' '?')))
    if ([bool](Get-Opt $run 'aborted' $false)) { $lines.Add('- 运行被中止：' + (Get-Opt $run 'abortReason' '?')) }
    $lines.Add('')

    $lines.Add('## 场景')
    $lines.Add('')
    $lines.Add('| 场景 | 结果 | 耗时 | 说明 |')
    $lines.Add('|---|---|---|---|')
    foreach ($scenarioRecord in @(Get-Opt $Report 'scenarios' @())) {
        if ($null -eq $scenarioRecord) { continue }
        $note = Get-Opt $scenarioRecord 'error' (Get-Opt $scenarioRecord 'skipReason' '')
        $lines.Add(('| {0} | {1} | {2}s | {3} |' -f $scenarioRecord.name, (Get-StatusText $scenarioRecord.status), (Get-Opt $scenarioRecord 'durationSec' 0), (Format-Cell $note)))
    }
    $lines.Add('')

    $findings = @(Get-Opt $Report 'findings' @()) | Where-Object { $null -ne $_ }
    foreach ($level in @('error', 'warning')) {
        $matching = @($findings | Where-Object { $_.severity -eq $level })
        if ($matching.Count -eq 0) { continue }
        $lines.Add('## ' + $level + '（' + $matching.Count + '）')
        $lines.Add('')
        foreach ($finding in $matching) {
            $where = @(@((Get-Opt $finding 'scenario' ''), (Get-Opt $finding 'step' '')) | Where-Object { $_ })
            $location = if ($where.Count -gt 0) { '（' + ($where -join ' / ') + '）' } else { '' }
            $lines.Add(('- `{0}/{1}`{2} {3}' -f $finding.category, $finding.rule, $location, (Format-Cell $finding.message 400)))
        }
        $lines.Add('')
    }
    $infos = @($findings | Where-Object { $_.severity -eq 'info' })
    if ($infos.Count -gt 0) {
        $lines.Add('info 共 ' + $infos.Count + ' 条（详见 report.json）。')
        $lines.Add('')
    }

    $shots = New-Object System.Collections.Generic.List[string]
    foreach ($shot in @(Get-Opt $run 'screenshots' @())) { if ($shot) { $shots.Add($shot) } }
    foreach ($scenarioRecord in @(Get-Opt $Report 'scenarios' @())) {
        if ($null -eq $scenarioRecord) { continue }
        foreach ($step in @(Get-Opt $scenarioRecord 'steps' @())) {
            if ($null -eq $step) { continue }
            foreach ($shot in @(Get-Opt $step 'screenshots' @())) { if ($shot) { $shots.Add($shot) } }
        }
    }
    if ($shots.Count -gt 0) {
        $lines.Add('## 截图')
        $lines.Add('')
        foreach ($shot in $shots) { $lines.Add('- [' + $shot + '](' + $shot + ')') }
        $lines.Add('')
    }

    $isolation = Get-Opt $Report 'isolation' $null
    $lines.Add('## 隔离')
    $lines.Add('')
    $saves = Get-Opt $isolation 'saves' $null
    $steam = Get-Opt $isolation 'steam' $null
    $realSaves = Get-Opt $isolation 'realSaves' $null
    $localMods = Get-Opt $isolation 'localMods' $null
    $lines.Add(('- 存档：写在运行目录 `{0}`，拦截了 {1} 次写到别处的存档' -f (Get-Opt $saves 'root' '?'), (Get-Opt $saves 'blockedWrites' 0)))
    $lines.Add(('- 游戏偏好：只存在内存里（写入 {0} 次）' -f (Get-Opt (Get-Opt $isolation 'preferences' $null) 'writes' 0)))
    $lines.Add(('- Steam：拦截了 {0} 次成就/统计/创意工坊写入' -f (Get-Opt $steam 'blockedTotal' 0)))
    $lines.Add(('- 玩家真实存档：{0}' -f $(if ([bool](Get-Opt $realSaves 'unchanged' $false)) { '没有变化' } else { '有变化，见 error' })))
    $lines.Add(('- 本地 Mods 目录：{0}' -f $(if ([bool](Get-Opt $localMods 'unchanged' $false)) { '没有变化' } else { '有变化，见 warning' })))
    $lines.Add('- 注册表（分辨率、语言等 PlayerPrefs）：' + $Launcher.Registry)
    $lines.Add('')

    $lines.Add('## 被测插件')
    $lines.Add('')
    foreach ($entry in @(Get-Opt $environment 'pluginsUnderTest' @())) {
        if ($null -eq $entry) { continue }
        $loaded = @(Get-Opt $entry 'loaded' @()) | Where-Object { $null -ne $_ } | ForEach-Object { $_.name + ' ' + $_.version + '（' + $_.guid + '）' }
        $text = if (@($loaded).Count -gt 0) { @($loaded) -join '；' } else { '没有加载' }
        $lines.Add('- ' + $entry.name + '：' + $text)
    }
    $lines.Add('')
    $lines.Add('运行目录里还有：`report.json`（完整数据）、`BepInEx/LogOutput.log`、`logs/Player.log`、`screenshots/`、`ui-dump/`。')
    Write-Utf8 (Join-Path $RunDirectory 'report.md') ($lines -join "`r`n")
    return $outcome
}

function Show-ReportSummary([string]$RunDirectory, $Report, $Outcome) {
    Write-Host ''
    Write-Host ('结果：' + $Outcome.Text) -ForegroundColor $(if ($Outcome.Code -eq 0) { 'Green' } elseif ($Outcome.Code -eq 1) { 'Red' } else { 'Yellow' })
    if ($null -ne $Report) {
        foreach ($scenarioRecord in @(Get-Opt $Report 'scenarios' @())) {
            if ($null -eq $scenarioRecord) { continue }
            $color = switch ($scenarioRecord.status) { 'passed' { 'Green' } 'skipped' { 'Yellow' } default { 'Red' } }
            Write-Host ('  {0,-28} {1}' -f $scenarioRecord.name, (Get-StatusText $scenarioRecord.status)) -ForegroundColor $color
        }
        $errors = @(@(Get-Opt $Report 'findings' @()) | Where-Object { $null -ne $_ -and $_.severity -eq 'error' })
        foreach ($finding in ($errors | Select-Object -First 8)) {
            Write-Host ('  error {0}/{1}: {2}' -f $finding.category, $finding.rule, (Format-Cell $finding.message 200)) -ForegroundColor Red
        }
        if ($errors.Count -gt 8) { Write-Host ('  ……还有 ' + ($errors.Count - 8) + ' 个 error，见 report.md') -ForegroundColor Red }
    }
    Write-Host ('报告：' + (Join-Path $RunDirectory 'report.md'))
}

# ======================== 命令：doctor ========================

function Invoke-Doctor {
    $failures = 0
    $configPath = Get-ConfigPath
    $configObject = Read-HarnessConfig $configPath
    $configDirectory = if ($configPath) { Split-Path $configPath -Parent } else { (Get-Location).Path }
    $pluginDirectory = Find-HarnessPlugin
    $version = if ($pluginDirectory) { Get-HarnessVersion $pluginDirectory } else { $null }
    Write-Host ('StudentAge Harness ' + $(if ($version) { $version } else { '(未构建)' }) + ' 环境检查')

    if ($PSVersionTable.PSVersion.Major -lt 5) { Write-Fail ('需要 PowerShell 5.1 或更高版本，当前 ' + $PSVersionTable.PSVersion); $failures++ }

    $game = Find-GameRoot $configObject $configDirectory
    if ($null -eq $game) {
        Write-Fail '找不到学生时代的安装目录。请在 harness.json 里写 gameRoot，或设置环境变量 STUDENTAGE_GAME_ROOT。'
        $failures++
    }
    else {
        Write-Pass ('游戏目录：' + $game.Path + '（来自' + $game.Source + '）')
        $doorstop = Get-DoorstopInfo $game.Path
        if (-not $doorstop.Installed) { Write-Fail $doorstop.Problem; $failures++ }
        else {
            $bepinexVersion = (Get-Item -LiteralPath (Join-Path $game.Path 'BepInEx\core\BepInEx.dll')).VersionInfo.FileVersion
            Write-Pass ('BepInEx ' + $bepinexVersion + '，Doorstop ' + $(if ($doorstop.Version) { $doorstop.Version } else { '(版本未知)' }) +
                $(if ($doorstop.Enabled) { '' } else { '（doorstop_config.ini 里是禁用的；sah 会在启动参数里临时启用）' }))
        }
        $harnessInGame = Join-Path $game.Path 'BepInEx\plugins\StudentAgeHarness\StudentAgeHarness.dll'
        if (Test-Path -LiteralPath $harnessInGame) { Write-Warn ('游戏自己的 BepInEx 里也装了 harness 插件（' + $harnessInGame + '）。它在那里不会启用，但日常游玩建议删掉。') }
        $runsDirectory = Get-RunsDirectory $configObject $configDirectory $game.Path
        if (-not (Test-SafePath $runsDirectory)) { Write-Warn ('运行目录路径含有空格或非 ASCII 字符，可能无法通过 Steam 传给游戏：' + $runsDirectory + '。可以在 harness.json 里用 runsDir 换一个位置。') }
        else { Write-Pass ('运行目录：' + $runsDirectory) }
        if (Test-Path -LiteralPath (Join-Path $script:RecoveryDirectory 'pending.json')) { Write-Warn '有一次被中断的运行还没恢复注册表，下次 sah run 会自动恢复（也可以现在运行 sah restore）。' }
    }

    $steam = Get-SteamDirectory
    if (-not $steam) { Write-Fail '找不到 Steam 安装目录。'; $failures++ }
    elseif (-not (Get-Process -Name 'steam' -ErrorAction SilentlyContinue)) { Write-Fail 'Steam 没有在运行。请先从开始菜单正常启动 Steam 并登录。'; $failures++ }
    else { Write-Pass ('Steam 正在运行（' + $steam + '）') }

    if (Get-Process -Name 'StudentAge' -ErrorAction SilentlyContinue) { Write-Fail '学生时代正在运行，请先退出游戏。'; $failures++ }
    else { Write-Pass '学生时代没有在运行' }

    $doorstopEnvironment = @([Environment]::GetEnvironmentVariables().Keys | Where-Object { [string]$_ -like 'DOORSTOP_*' })
    if ($doorstopEnvironment.Count -gt 0) { Write-Fail ('当前终端继承了 ' + ($doorstopEnvironment -join ', ') + '，请换一个新开的终端。'); $failures++ }

    if (-not $pluginDirectory) { Write-Fail ('找不到 harness 插件。发布包里应有 plugin 目录；源码仓库请先运行：dotnet build "' + $script:HarnessRoot + '" -c Release'); $failures++ }
    else { Write-Pass ('harness 插件：' + $pluginDirectory) }

    if ($configPath) {
        Write-Pass ('配置文件：' + $configPath)
        foreach ($field in @('plugins', 'packs', 'dependencies', 'patchers', 'configFiles')) {
            foreach ($item in @(Get-Opt $configObject $field @())) {
                if (-not $item) { continue }
                $path = Resolve-FullPath $item $configDirectory
                if (-not (Test-Path -LiteralPath $path)) { Write-Fail ($field + ' 里的路径不存在：' + $path); $failures++ }
            }
        }
        if (@(Get-Opt $configObject 'plugins' @()).Count -eq 0) { Write-Note 'harness.json 里没有 plugins：只会测试原版游戏。' }
    }
    else { Write-Note '当前目录没有 harness.json（可以运行 sah init 生成）。不带配置时 sah run 只跑内置场景 startup。' }

    Write-Host ''
    if ($failures -eq 0) { Write-Host '结论：可以运行 sah run。' -ForegroundColor Green; return $ExitPassed }
    Write-Host ('结论：有 ' + $failures + ' 个问题需要先解决。') -ForegroundColor Red
    return $ExitUsage
}

# ======================== 命令：run ========================

function Invoke-HarnessRun {
    $configPath = Get-ConfigPath
    $configObject = Read-HarnessConfig $configPath
    $configDirectory = if ($configPath) { Split-Path $configPath -Parent } else { (Get-Location).Path }

    $game = Find-GameRoot $configObject $configDirectory
    if ($null -eq $game) { Exit-Usage '找不到学生时代的安装目录。请运行 sah doctor 查看原因。' }
    $root = $game.Path
    $doorstop = Get-DoorstopInfo $root
    if (-not $doorstop.Installed) { Exit-Usage $doorstop.Problem }
    $pluginDirectory = Find-HarnessPlugin
    if (-not $pluginDirectory) { Exit-Usage 'harness 插件还没有构建。请运行 sah doctor 查看原因。' }
    $steam = Get-SteamDirectory
    if (-not $steam) { Exit-Usage '找不到 Steam 安装目录。' }
    if (-not (Get-Process -Name 'steam' -ErrorAction SilentlyContinue)) { Exit-Usage 'Steam 没有在运行。请先从开始菜单正常启动 Steam 并登录。' }
    if (Get-Process -Name 'StudentAge' -ErrorAction SilentlyContinue) { Exit-Usage '学生时代正在运行，请先退出游戏。' }
    $doorstopEnvironment = @([Environment]::GetEnvironmentVariables().Keys | Where-Object { [string]$_ -like 'DOORSTOP_*' })
    if ($doorstopEnvironment.Count -gt 0) { Exit-Usage '当前终端继承了 DOORSTOP_*，请从开始菜单打开干净的终端。' }

    $runsDirectory = Get-RunsDirectory $configObject $configDirectory $root
    New-Item -ItemType Directory -Force -Path $runsDirectory | Out-Null
    if (-not (Invoke-PendingRestore $runsDirectory -Quiet)) { exit $ExitUsage }

    # ---------- 合并配置和命令行 ----------
    $pluginItems = @(if ($Plugin) { $Plugin | ForEach-Object { Resolve-FullPath $_ (Get-Location).Path } } else { @(Get-Opt $configObject 'plugins' @()) | Where-Object { $_ } | ForEach-Object { Resolve-FullPath $_ $configDirectory } })
    $packItems = @(if ($Pack) { $Pack | ForEach-Object { Resolve-FullPath $_ (Get-Location).Path } } else { @(Get-Opt $configObject 'packs' @()) | Where-Object { $_ } | ForEach-Object { Resolve-FullPath $_ $configDirectory } })
    $dependencyItems = @(@(Get-Opt $configObject 'dependencies' @()) | Where-Object { $_ } | ForEach-Object { Resolve-FullPath $_ $configDirectory })
    $patcherItems = @(@(Get-Opt $configObject 'patchers' @()) | Where-Object { $_ } | ForEach-Object { Resolve-FullPath $_ $configDirectory })
    $configFileItems = @(@(Get-Opt $configObject 'configFiles' @()) | Where-Object { $_ } | ForEach-Object { Resolve-FullPath $_ $configDirectory })
    foreach ($path in @($pluginItems + $packItems + $dependencyItems + $patcherItems + $configFileItems)) {
        if (-not (Test-Path -LiteralPath $path)) { Exit-Usage ('文件或目录不存在：' + $path) }
    }
    foreach ($path in $packItems) { if ((Test-Path -LiteralPath $path -PathType Container) -or $path -notlike '*.dll') { Exit-Usage ('场景包必须是 DLL 文件：' + $path) } }

    $scenarios = @(if ($Scenario) { $Scenario | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } } else { @(Get-Opt $configObject 'scenarios' @('startup')) | Where-Object { $_ } })
    if ($scenarios.Count -eq 0) { $scenarios = @('startup') }
    $resolutionText = if ($Resolution) { $Resolution } else { [string](Get-Opt $configObject 'resolution' '1920x1080') }
    if ($resolutionText -notmatch '^(\d{3,5})x(\d{3,5})$') { Exit-Usage ('分辨率要写成 1920x1080 这样：' + $resolutionText) }
    $width = [int]$Matches[1]
    $height = [int]$Matches[2]
    if ($width -lt 320 -or $width -gt 8192 -or $height -lt 200 -or $height -gt 8192) { Exit-Usage '分辨率超出支持范围。' }
    $window = Get-WindowMode $configObject $Visible.IsPresent $Background.IsPresent
    $timeout = if ($TimeoutSec -gt 0) { $TimeoutSec } else { [int](Get-Opt $configObject 'timeoutSec' 600) }
    $startupTimeout = [int](Get-Opt $configObject 'startupTimeoutSec' 180)
    if ($timeout -le 0 -or $startupTimeout -le 0) { Exit-Usage '超时必须大于零。' }
    $strictMode = $Strict.IsPresent -or [bool](Get-Opt $configObject 'strict' $false)
    $workshop = Get-WorkshopSelection $configObject

    # ---------- 准备运行目录 ----------
    $runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 4)
    $runDirectory = Join-Path $runsDirectory $runId
    if (Test-Path -LiteralPath $runDirectory) { throw 'Run directory already exists; refusing to overwrite.' }
    $bepinex = Join-Path $runDirectory 'BepInEx'
    New-Item -ItemType Directory -Force -Path (Join-Path $bepinex 'config'), (Join-Path $bepinex 'plugins'), (Join-Path $runDirectory 'logs') | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'BepInEx\core') -Destination (Join-Path $bepinex 'core') -Recurse
    $userConfig = Join-Path $root 'BepInEx\config\BepInEx.cfg'
    if (Test-Path -LiteralPath $userConfig) { Copy-Item -LiteralPath $userConfig -Destination (Join-Path $bepinex 'config') }
    foreach ($file in $configFileItems) { Copy-Item -LiteralPath $file -Destination (Join-Path $bepinex 'config') -Force }

    $harnessTarget = Join-Path $bepinex 'plugins\StudentAgeHarness'
    New-Item -ItemType Directory -Force -Path $harnessTarget | Out-Null
    Get-ChildItem -LiteralPath $pluginDirectory -File | Where-Object { $_.Extension -in '.dll', '.pdb' } | Copy-Item -Destination $harnessTarget

    $usedNames = @{ 'StudentAgeHarness' = $true }
    $pluginsUnderTest = @()
    foreach ($item in $pluginItems) {
        $copied = Copy-PluginItem $item (Join-Path $bepinex 'plugins') $usedNames
        $pluginsUnderTest += [ordered]@{ path = (Get-RelativePath $copied.Path $runDirectory); kind = $copied.Kind; name = $copied.Name }
    }
    if ($dependencyItems.Count -gt 0) {
        $dependencyRoot = Join-Path $bepinex 'plugins\_dependencies'
        New-Item -ItemType Directory -Force -Path $dependencyRoot | Out-Null
        foreach ($item in $dependencyItems) { [void](Copy-PluginItem $item $dependencyRoot $usedNames) }
    }
    if ($patcherItems.Count -gt 0) {
        $patcherRoot = Join-Path $bepinex 'patchers'
        New-Item -ItemType Directory -Force -Path $patcherRoot | Out-Null
        foreach ($item in $patcherItems) { [void](Copy-PluginItem $item $patcherRoot $usedNames) }
    }
    $packs = @()
    if ($packItems.Count -gt 0) {
        $packRoot = Join-Path $runDirectory 'packs'
        New-Item -ItemType Directory -Force -Path $packRoot | Out-Null
        foreach ($item in $packItems) {
            if (Test-Path -LiteralPath (Join-Path $packRoot (Split-Path $item -Leaf))) { throw 'Duplicate scenario pack filename.' }
            Copy-Item -LiteralPath $item -Destination $packRoot -Force
            $pdb = [IO.Path]::ChangeExtension($item, '.pdb')
            if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $packRoot -Force }
            $packs += 'packs/' + (Split-Path $item -Leaf)
        }
    }

    $runSettings = [ordered]@{
        format = $script:RunFormat
        runId = $runId
        scenarios = @($scenarios)
        timeoutSec = $timeout
        startupTimeoutSec = $startupTimeout
        resolution = [ordered]@{ width = $width; height = $height }
        window = $window
        mute = [bool](Get-Opt $configObject 'mute' $true)
        redactPaths = [bool](Get-Opt $configObject 'redactPaths' $true)
        pluginsUnderTest = @($pluginsUnderTest)
        expectPlugins = @(@(Get-Opt $configObject 'expectPlugins' @()) | Where-Object { $_ })
        packs = @($packs)
        workshopMods = $workshop
        source = [ordered]@{
            config = $configPath
            launcher = 'sah.ps1 ' + (Get-HarnessVersion $pluginDirectory)
        }
    }
    Write-Utf8 (Join-Path $runDirectory 'run.json') (ConvertTo-Json $runSettings -Depth 8)

    Write-Host ('运行目录：' + $runDirectory)
    Write-Host ('场景：' + ($scenarios -join ', ') + '；窗口：' + $window + ' ' + $width + 'x' + $height + '；超时 ' + $timeout + ' 秒')
    if ($pluginsUnderTest.Count -gt 0) { Write-Host ('被测插件：' + (($pluginsUnderTest | ForEach-Object { $_.name }) -join ', ')) }
    if ($packs.Count -gt 0) { Write-Host ('场景包：' + (($packs | ForEach-Object { Split-Path $_ -Leaf }) -join ', ')) }

    # ---------- 注册表快照，然后启动 ----------
    $registryDirectory = $script:RecoveryDirectory
    New-Item -ItemType Directory -Force -Path $registryDirectory | Out-Null
    $snapshotPath = Join-Path $registryDirectory ($runId + '.json')
    Save-RegistrySnapshot $snapshotPath
    $pendingPath = Join-Path $script:RecoveryDirectory 'pending.json'
    Write-Utf8 $pendingPath (ConvertTo-Json ([ordered]@{ runId = $runId; snapshot = $snapshotPath; createdAtUtc = [DateTime]::UtcNow.ToString('o') }))
    $launcher = @{ Registry = '没有恢复'; Problem = $null }
    $gameProcess = $null
    $report = $null
    try {
        [void](Set-DisplayPreferences $width $height)
        Initialize-Native
        if ($window -eq 'background') { Write-Utf8 (Join-Path $runDirectory 'launch-focus.txt') ([StudentAgeHarness.Native]::GetForegroundWindow().ToInt64().ToString()) }

        $preloader = Join-Path $bepinex 'core\BepInEx.Preloader.dll'
        $doorstopArgs = if ($doorstop.Major -ge 4) { '--doorstop-enabled true --doorstop-target-assembly "' + $preloader + '"' } else { '--doorstop-enable true --doorstop-target "' + $preloader + '"' }
        $arguments = '-applaunch ' + $script:AppId + ' ' + $doorstopArgs + ' -screen-fullscreen 0 -screen-width ' + $width + ' -screen-height ' + $height +
            ' -logFile "' + (Join-Path $runDirectory 'logs\Player.log') + '"'
        $launchedAt = Get-Date
        Start-Process -FilePath (Join-Path $steam 'steam.exe') -ArgumentList $arguments | Out-Null
        Write-Host '已通过 Steam 启动游戏，等待 harness 启用……'

        $processDeadline = $launchedAt.AddSeconds(90)
        while ($null -eq $gameProcess) {
            if ((Get-Date) -gt $processDeadline) { throw [InvalidOperationException]'Steam 在 90 秒内没有启动游戏。请确认 Steam 已登录、游戏能正常从 Steam 启动。' }
            $gameProcess = Get-Process -Name 'StudentAge' -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $launchedAt.AddSeconds(-5) } | Select-Object -First 1
            if ($null -eq $gameProcess) { Start-Sleep -Milliseconds 300 }
        }

        $readyPath = Join-Path $runDirectory 'harness-ready.json'
        $readyDeadline = (Get-Date).AddSeconds(150)
        $parked = $false
        while (-not (Test-Path -LiteralPath $readyPath)) {
            $gameProcess.Refresh()
            if ($gameProcess.HasExited) { throw [InvalidOperationException]'游戏在 harness 启用前就退出了。' }
            if ((Get-Date) -gt $readyDeadline) {
                $logPath = Join-Path $bepinex 'LogOutput.log'
                if (-not (Test-Path -LiteralPath $logPath)) { throw [InvalidOperationException]'BepInEx 没有从隔离目录启动（运行目录里没有 LogOutput.log）。可能 Doorstop 启动参数没有生效，请运行 sah doctor。' }
                throw [InvalidOperationException]'harness 插件在 150 秒内没有启用。请查看运行目录里的 BepInEx/LogOutput.log。'
            }
            if ($window -eq 'background' -and -not $parked -and $gameProcess.MainWindowHandle -ne [IntPtr]::Zero) { $parked = Move-WindowBelowDesktop $gameProcess.MainWindowHandle }
            Start-Sleep -Milliseconds 250
        }
        Write-Host 'harness 已启用，正在跑场景……'

        $reportPath = Join-Path $runDirectory 'report.json'
        $runDeadline = (Get-Date).AddSeconds($timeout + $startupTimeout + 120)
        $reportSeenAt = $null
        while ($true) {
            $gameProcess.Refresh()
            if ($gameProcess.HasExited) { break }
            if ($null -eq $reportSeenAt -and (Test-Path -LiteralPath $reportPath)) { $reportSeenAt = Get-Date }
            if ($null -ne $reportSeenAt -and ((Get-Date) - $reportSeenAt).TotalSeconds -gt 45) {
                Write-Warn '报告已经写出，但游戏 45 秒后仍未退出，强制结束。'
                $launcher.Problem = '游戏写报告后未能正常退出。'
                Stop-Process -Id $gameProcess.Id -Force
                break
            }
            if ((Get-Date) -gt $runDeadline) {
                $launcher.Problem = '超过最长等待时间，游戏仍在运行，已强制结束。'
                Write-Warn $launcher.Problem
                Stop-Process -Id $gameProcess.Id -Force
                break
            }
            Start-Sleep -Milliseconds 500
        }
        if (Test-Path -LiteralPath $reportPath) { $report = Read-Json $reportPath }
    }
    catch {
        $launcher.Problem = $_.Exception.Message
        Write-Fail $launcher.Problem
    }
    finally {
        if ($null -ne $gameProcess) {
            try {
                $gameProcess.Refresh()
                if (-not $gameProcess.HasExited) { Stop-Process -Id $gameProcess.Id -Force; $gameProcess.WaitForExit(15000) | Out-Null }
            }
            catch { }
            $gameProcess.Dispose()
        }
        try {
            # HasExited 变为 true 后，进程还可能在进程列表里停留片刻；立刻检查会误判游戏仍在运行而推迟恢复。
            $exitDeadline = (Get-Date).AddSeconds(15)
            while ((Get-Process -Name StudentAge -ErrorAction SilentlyContinue) -and (Get-Date) -lt $exitDeadline) { Start-Sleep -Milliseconds 250 }
            if (Get-Process -Name StudentAge -ErrorAction SilentlyContinue) { throw 'Game still running; recovery deferred.' }
            if (Restore-RegistrySnapshot $snapshotPath) {
                Remove-Item -LiteralPath $pendingPath -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $snapshotPath -Force -ErrorAction SilentlyContinue
                $launcher.Registry = '运行结束后已恢复成运行前的样子'
            }
            else { $launcher.Registry = '恢复后和快照不一致，请运行 sah restore'; $launcher.Problem = '注册表恢复校验失败。' }
        }
        catch { $launcher.Registry = '恢复失败，下次 sah run 会再试；也可以运行 sah restore'; $launcher.Problem = '注册表尚未恢复。' }
    }

    Write-Utf8 (Join-Path $runDirectory 'launcher.json') (ConvertTo-Json $launcher)
    $outcome = Write-ReportMarkdown $runDirectory $report $launcher $strictMode
    if ($launcher.Problem) { $outcome = @{ Code = $ExitIncomplete; Text = $launcher.Problem } }
    Show-ReportSummary $runDirectory $report $outcome
    if ($Open) { Invoke-Item (Join-Path $runDirectory 'report.md') }
    return $outcome.Code
}

# ======================== 命令：report / clean / restore / init ========================

function Get-RunDirectories([string]$RunsDirectory) {
    if (-not (Test-Path -LiteralPath $RunsDirectory)) { return @() }
    return @(Get-ChildItem -LiteralPath $RunsDirectory -Directory | Where-Object {
            $runJson = Join-Path $_.FullName 'run.json'
            $valid = $false
            if ($_.Name -match '^\d{8}-\d{6}-[a-f0-9]{4}$' -and (Test-Path -LiteralPath $runJson)) {
                try {
                    $data = Read-Json $runJson
                    $valid = $data.format -ceq $script:RunFormat -and $data.runId -ceq $_.Name
                } catch { $valid = $false }
            }
            $valid
        } | Sort-Object Name -Descending)
}

function Get-RunsDirectoryForCommand {
    $configPath = Get-ConfigPath
    $configObject = Read-HarnessConfig $configPath
    $configDirectory = if ($configPath) { Split-Path $configPath -Parent } else { (Get-Location).Path }
    $game = Find-GameRoot $configObject $configDirectory
    if ($null -eq $game) { Exit-Usage '找不到学生时代的安装目录。' }
    return @{ Runs = (Get-RunsDirectory $configObject $configDirectory $game.Path); Config = $configObject }
}

function Invoke-Report {
    if ($Run -and (Test-Path -LiteralPath $Run -PathType Container)) {
        $context = @{ Runs = ''; Config = $null }
    } else { $context = Get-RunsDirectoryForCommand }
    $directory = $null
    if ($Run) {
        if (Test-Path -LiteralPath $Run -PathType Container) { $directory = (Resolve-Path -LiteralPath $Run).ProviderPath }
        else { $directory = Join-Path $context.Runs $Run }
    }
    else {
        $latest = Get-RunDirectories $context.Runs | Select-Object -First 1
        if ($null -ne $latest) { $directory = $latest.FullName }
    }
    if (-not $directory -or -not (Test-Path -LiteralPath $directory)) { Exit-Usage '没有找到运行目录。' }
    $reportPath = Join-Path $directory 'report.json'
    $report = if (Test-Path -LiteralPath $reportPath) { Read-Json $reportPath } else { $null }
    $markdown = Join-Path $directory 'report.md'
    $strictMode = $Strict.IsPresent -or [bool](Get-Opt $context.Config 'strict' $false)
    $launcherPath = Join-Path $directory 'launcher.json'
    $launcherData = if (Test-Path -LiteralPath $launcherPath) { Read-Json $launcherPath } else { $null }
    $launcher = @{ Registry = (Get-Opt $launcherData 'Registry' '（未知）'); Problem = (Get-Opt $launcherData 'Problem' $null) }
    $outcome = Write-ReportMarkdown $directory $report $launcher $strictMode
    Write-Host ('运行目录：' + $directory)
    Show-ReportSummary $directory $report $outcome
    if ($Open) { Invoke-Item $markdown }
    return $outcome.Code
}

function Invoke-Clean {
    if (Get-Process -Name StudentAge -ErrorAction SilentlyContinue) { Exit-Usage '请先退出游戏再清理。' }
    if (Test-Path -LiteralPath (Join-Path $script:RecoveryDirectory 'pending.json')) { Exit-Usage '请先运行 sah restore，恢复成功后再清理。' }
    $context = Get-RunsDirectoryForCommand
    $keepCount = if ($Keep -ge 0) { $Keep } else { [int](Get-Opt $context.Config 'keepRuns' 20) }
    if ($keepCount -lt 0) { Exit-Usage 'keepRuns 必须大于等于零。' }
    $all = @(Get-RunDirectories $context.Runs)
    $old = @($all | Select-Object -Skip $keepCount)
    $bytes = 0
    foreach ($directory in $old) {
        $bytes += (Get-ChildItem -LiteralPath $directory.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        if ($Delete) { Remove-Item -LiteralPath $directory.FullName -Recurse -Force }
        else { Write-Host ('将清理：' + $directory.FullName) }
    }
    Write-Note (($old.Count.ToString()) + ' 个运行目录，' + [Math]::Round($bytes / 1MB, 1) + ' MB。' +
        $(if ($Delete) { '已删除。' } else { '当前只是预览；加 -Delete 才会删除。' }))
    return $ExitPassed
}

function Invoke-Restore {
    if (Invoke-PendingRestore '') { return $ExitPassed }
    return $ExitUsage
}

function Invoke-Init {
    $path = Join-Path (Get-Location).Path 'harness.json'
    if (Test-Path -LiteralPath $path) { Exit-Usage ('harness.json 已经存在：' + $path) }
    $template = [ordered]@{
        plugins = @('bin/Release/MyMod.dll')
        packs = @()
        scenarios = @('startup', 'new-game')
        window = 'visible'
        resolution = '1920x1080'
        timeoutSec = 600
    }
    Write-Utf8 $path (ConvertTo-Json $template -Depth 4)
    Write-Pass ('已生成 ' + $path)
    Write-Host '把 plugins 改成你的 mod 构建出来的 DLL（或整个输出目录），然后运行 sah doctor 和 sah run。'
    return $ExitPassed
}

function Show-Help {
    Get-Help $PSCommandPath -Detailed | Out-String | Write-Host
    return $ExitPassed
}

if ($MyInvocation.InvocationName -eq '.') { return }

$mutex = $null
$locked = $false
try {
    if (@('run', 'restore', 'clean') -contains $Command) {
        $mutex = New-Object System.Threading.Mutex($false, 'Local\StudentAgeHarness-PakyiGame')
        try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { Exit-Usage '另一个 harness 启动器正在运行，拒绝并发操作。' }
    }
    switch ($Command) {
        'doctor' { exit (Invoke-Doctor) }
        'run' { exit (Invoke-HarnessRun) }
        'report' { exit (Invoke-Report) }
        'clean' { exit (Invoke-Clean) }
        'restore' { exit (Invoke-Restore) }
        'init' { exit (Invoke-Init) }
        default { exit (Show-Help) }
    }
}
catch { Write-Fail $_.Exception.Message; exit $ExitUsage }
finally {
    if ($locked) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
