$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\tools\sah.ps1')
$script:Passed = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:Passed++
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('sah-tests-' + [Guid]::NewGuid().ToString('N'))
$testKey = 'Software\StudentAgeHarness.Tests\' + [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    Check ((Get-Opt @{ x = 5 } 'x' 0) -eq 5) 'Dictionary options'
    Check ((Get-Opt ([pscustomobject]@{ x = 6 }) 'x' 0) -eq 6) 'Object options'
    Check ((Get-Opt $null 'x' 7) -eq 7) 'Missing option'
    $oneMod = Get-WorkshopSelection ('{"workshopMods":["123"]}' | ConvertFrom-Json)
    Check ($oneMod -is [array] -and $oneMod.Count -eq 1 -and $oneMod[0] -eq '123') 'Single workshop ID stays an array'
    Check ((Get-WorkshopSelection ('{"workshopMods":"inherit"}' | ConvertFrom-Json)) -eq 'inherit') 'Workshop inheritance selection'
    Check ((Get-WorkshopSelection $null).Count -eq 0) 'Default workshop isolation'
    $badWorkshop = $false
    try { [void](Get-WorkshopSelection ('{"workshopMods":["oops"]}' | ConvertFrom-Json)) } catch { $badWorkshop = $true }
    Check $badWorkshop 'Malformed workshop IDs rejected'
    Check ((Get-WindowMode $null $false $false) -eq 'visible') 'Window defaults to visible'
    $backgroundConfig = '{"window":"background"}' | ConvertFrom-Json
    Check ((Get-WindowMode $backgroundConfig $false $false) -eq 'background') 'Config can choose background'
    Check ((Get-WindowMode $backgroundConfig $true $false) -eq 'visible') '-Visible overrides config'
    Check ((Get-WindowMode $null $false $true) -eq 'background') '-Background overrides default'
    $conflict = $false
    try { [void](Get-WindowMode $null $true $true) } catch { $conflict = $true }
    Check $conflict 'Conflicting window switches rejected'
    $badWindow = $false
    try { [void](Get-WindowMode ('{"window":"hidden"}' | ConvertFrom-Json) $false $false) } catch { $badWindow = $true }
    Check $badWindow 'Unknown window mode rejected'
    $buildOutput = Join-Path $testRoot 'MyMod\bin\Release\netstandard2.0'
    New-Item -ItemType Directory -Path $buildOutput | Out-Null
    [IO.File]::WriteAllText((Join-Path $buildOutput 'MyMod.dll'), 'metadata: BepInPlugin BaseUnityPlugin')
    [IO.File]::WriteAllText((Join-Path $buildOutput 'MyMod.Core.dll'), 'metadata: plain library')
    $pluginTarget = Join-Path $testRoot 'plugins'
    New-Item -ItemType Directory -Path $pluginTarget | Out-Null
    $copiedPlugin = Copy-PluginItem $buildOutput $pluginTarget @{}
    Check ($copiedPlugin.Name -eq 'MyMod.dll') 'Folder plugin is named after its plugin DLL'
    Check ((Split-Path $copiedPlugin.Path -Leaf) -eq 'MyMod' -and (Test-Path -LiteralPath (Join-Path $copiedPlugin.Path 'MyMod.Core.dll'))) 'Folder plugin copied under its plugin name'
    $tokens=$null; $errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\tools\sah.ps1'), [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) 'PowerShell parser'

    # Never write the actual game registry key. Exercise raw Unity DWORD storage
    # and exact deletion/restoration in a unique disposable test key instead.
    $script:RegistrySubKey = $testKey
    Initialize-RawRegistry
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKey)
    try {
        [StudentAgeHarness.RawRegistry]::Write($key, 'float-as-dword', 4, [Convert]::ToBase64String([BitConverter]::GetBytes([double]5.0)))
        $key.SetValue('text', 'original')
        $key.SetValue('integer', 12, [Microsoft.Win32.RegistryValueKind]::DWord)
    } finally { $key.Close() }
    $snapshot = Join-Path $testRoot 'registry.json'
    Save-RegistrySnapshot $snapshot
    $before = Get-RegistryFingerprint (Get-RegistryState)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKey, $true)
    try { $key.SetValue('text', 'changed'); $key.SetValue('extra', 'new') } finally { $key.Close() }
    Check (Restore-RegistrySnapshot $snapshot) 'Raw snapshot round-trip'
    Check ((Get-RegistryFingerprint (Get-RegistryState)) -ceq $before) 'Exact registry byte restoration'
    $bad = Read-Json $snapshot
    $bad.key = 'HKCU\Other'
    Write-Utf8 $snapshot (ConvertTo-Json $bad -Depth 8)
    $rejected = $false
    try { [void](Restore-RegistrySnapshot $snapshot) } catch { $rejected = $true }
    Check $rejected 'Mismatched registry target rejected'
    Check ((Get-RegistryFingerprint (Get-RegistryState)) -ceq $before) 'Invalid snapshot cannot mutate registry'

    $script:RecoveryDirectory = Join-Path $testRoot 'recovery'
    New-Item -ItemType Directory -Path $script:RecoveryDirectory | Out-Null
    $recoveryId = '20000101-000000-abcd'
    $recoverySnapshot = Join-Path $script:RecoveryDirectory ($recoveryId + '.json')
    Save-RegistrySnapshot $recoverySnapshot
    $pending = Join-Path $script:RecoveryDirectory 'pending.json'
    Write-Utf8 $pending (ConvertTo-Json @{ runId = $recoveryId; snapshot = $recoverySnapshot })
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKey, $true)
    try { $key.SetValue('interrupted', 1) } finally { $key.Close() }
    Check (Invoke-PendingRestore 'unrelated-runs-directory' -Quiet) 'Recovery works across projects'
    Check ((Get-RegistryFingerprint (Get-RegistryState)) -ceq $before) 'Interrupted run restored exactly'
    Check (-not (Test-Path -LiteralPath $pending)) 'Recovery marker consumed after verification'

    $report = [pscustomobject]@{
        schema = 'studentage-harness-report/2'
        run = [pscustomobject]@{ aborted = $false }
        summary = [pscustomobject]@{ scenarios = 1; passedScenarios = 1; errors = 0; warnings = 0; failedScenarios = 0 }
        scenarios = @([pscustomobject]@{ name = 'test'; status = 'passed' })
    }
    Check ((Get-ReportOutcome $report $false).Code -eq 0) 'Passing exit code'
    $report.summary.warnings = 1
    Check ((Get-ReportOutcome $report $true).Code -eq 1) 'Strict warnings fail'
    $report.summary.failedScenarios = 1
    Check ((Get-ReportOutcome $report $false).Code -eq 1) 'Failed scenarios cannot pass'
    Check ((Get-ReportOutcome $null $false).Code -eq 2) 'Missing report is incomplete'
    $report.summary.failedScenarios = 0
    $report.summary.passedScenarios = 0
    Check ((Get-ReportOutcome $report $false).Code -eq 2) 'All-skipped report cannot pass'
    $report.schema = 'unsupported'
    Check ((Get-ReportOutcome $report $false).Code -eq 2) 'Reject unknown report format'
    $empty = Join-Path $testRoot 'empty'
    New-Item -ItemType Directory -Path $empty | Out-Null
    Check (@(Get-RunDirectories $empty).Count -eq 0) 'Empty run list'
    $generated = Join-Path $empty '20000101-000000-abcd'
    New-Item -ItemType Directory -Path $generated | Out-Null
    Write-Utf8 (Join-Path $generated 'run.json') (ConvertTo-Json @{ format = $script:RunFormat; runId = '20000101-000000-abcd' })
    $unowned = Join-Path $empty '20000101-000001-abcd'
    New-Item -ItemType Directory -Path $unowned | Out-Null
    Write-Utf8 (Join-Path $unowned 'run.json') '{"format":"studentage-harness-run/1"}'
    Check (@(Get-RunDirectories $empty).Count -eq 1) 'Cleanup only lists harness run directories'
    $script:TestRuns = $empty
    function Get-RunsDirectoryForCommand { return @{ Runs = $script:TestRuns; Config = $null } }
    $Keep = 0
    $Delete = $false
    [void](Invoke-Clean)
    Check (Test-Path -LiteralPath $generated) 'Cleanup defaults to preview'
    $Delete = $true
    [void](Invoke-Clean)
    Check (-not (Test-Path -LiteralPath $generated)) 'Explicit cleanup removes only generated run'
    Check (Test-Path -LiteralPath $unowned) 'Cleanup preserves unrelated directories'
    Write-Output ("Launcher checks passed: " + $script:Passed)
}
finally {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testKey, $false)
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
