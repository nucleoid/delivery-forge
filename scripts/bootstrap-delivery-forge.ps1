#requires -Version 5.1
<#
.SYNOPSIS
Checks or installs the Windows Delivery Forge prerequisites, CLI and both skills.
.DESCRIPTION
Check performs no installation; UNKNOWN retains its process evidence and barrier.
Install performs automatable setup and leaves authentication
to the user. Dot-source this file to use its functions without running the entrypoint.
#>
[CmdletBinding()]
param(
    [ValidateSet('Check', 'Install')][string]$Mode = 'Check',
    [string]$StateRoot,
    [string]$SourceRoot,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$')][string]$Ref = 'main',
    [switch]$Json,
    [ValidateRange(30, 1200)][int]$MaximumSeconds = 1200
)

# A native process launched from PowerShell7 can inherit its module ordering.
# Select the current Windows engine's utility module for this session only.
if ($PSVersionTable.PSVersion.Major -eq 5) {
    Import-Module -Name (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1') -Force -ErrorAction Stop
}

function Get-ForgeDefaultStateRoot {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This bootstrap currently supports native Windows only.' }
    # The folder API can return an AppContainer redirect when called by an agent.
    # Use the user's existing environment selection before that API fallback.
    $base = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($base)) { $base = [Environment]::GetFolderPath('LocalApplicationData') }
    if ($base -match '[\\/]Packages[\\/]sandbox\.[^\\/]+[\\/]AC$') {
        # An agent's disposable AppContainer is not durable per-user storage.
        $base = Join-Path $env:USERPROFILE 'AppData/Local'
    }
    if ([string]::IsNullOrWhiteSpace($base)) { throw 'The current Windows user has no LocalApplicationData directory.' }
    Join-Path $base 'DeliveryForge'
}

function Assert-ForgePlainPath([string]$Path, [switch]$Directory) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path -match '[\r\n\x00]') { throw 'Use a local absolute path, without control characters.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($full -eq [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path)).TrimEnd('\', '/')) { throw 'Select a dedicated directory, not a drive root.' }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Symlink/junction paths are not supported by bootstrap.' }
            if ($cursor -ne $full -and -not $item.PSIsContainer) { throw 'A path ancestor is not a directory.' }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }; $cursor = $parent
    }
    if (Test-Path -LiteralPath $full) {
        if ((Get-Item -LiteralPath $full -Force).PSIsContainer -ne [bool]$Directory) { throw 'The selected path has the wrong file type.' }
    }
    $full
}

function Assert-ForgeWithin([string]$Parent, [string]$Child) {
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($Child).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'An installation path escaped its selected parent.' }
}

function Read-ForgeUtf8Text([string]$Path) {
    [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false, $true)))
}

function ConvertTo-ForgeWireString([AllowEmptyString()][string]$Value) {
    # ASCII-only private wire value; strict UTF8 refuses lone surrogates.
    $utf8 = New-Object Text.UTF8Encoding($false, $true)
    [Convert]::ToBase64String($utf8.GetBytes($Value))
}

function Write-ForgeDiagnostic([string]$Message) {
    try { [Console]::Error.WriteLine($Message) } catch { }
}

function ConvertTo-ForgeNativeArgument([AllowEmptyString()][string]$Value) {
    # CommandLineToArgvW quoting. No cmd.exe or interpolated shell command.
    if ($Value -match '[\r\n\x00]') { throw 'Native arguments cannot contain control characters.' }
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Find-ForgeCommand([string]$Name) {
    if ($Name -eq 'dotnet' -and $env:DOTNET_ROOT -and [IO.Path]::IsPathRooted($env:DOTNET_ROOT)) {
        $selected = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
        if (Test-Path -LiteralPath $selected -PathType Leaf) { return [IO.Path]::GetFullPath($selected) }
    }
    if ($Name -eq 'pwsh' -and $PSVersionTable.PSVersion.Major -ge 7) {
        $current = (Get-Process -Id $PID).Path
        if ($current -and [IO.Path]::GetFileName($current) -eq 'pwsh.exe') { return $current }
    }
    $batchFallback = $null
    $names = @(($Name + '.exe'), ($Name + '.ps1'), ($Name + '.cmd'))
    foreach ($candidate in $names) {
        $command = Get-Command $candidate -CommandType Application, ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) {
            if ([IO.Path]::GetExtension($command.Source) -in @('.cmd', '.bat') -or ($Name -eq 'pwsh' -and [IO.Path]::GetExtension($command.Source) -ine '.exe')) { $batchFallback = $command.Source; continue }
            return $command.Source
        }
    }
    $known = switch ($Name) {
        'git' { @("$env:ProgramFiles/Git/cmd/git.exe", "$env:LOCALAPPDATA/Programs/Git/cmd/git.exe") }
        'gh' { @("$env:ProgramFiles/GitHub CLI/gh.exe") }
        'codex' { @("$env:LOCALAPPDATA/Microsoft/WinGet/Links/codex.exe", "$env:APPDATA/npm/codex.ps1") }
        'claude' { @("$env:USERPROFILE/.local/bin/claude.exe", "$env:LOCALAPPDATA/Microsoft/WinGet/Links/claude.exe") }
        'dotnet' { @("$env:ProgramFiles/dotnet/dotnet.exe", "$env:LOCALAPPDATA/Microsoft/dotnet/dotnet.exe", "$env:USERPROFILE/.dotnet/dotnet.exe") }
        'pwsh' { @("$env:ProgramFiles/PowerShell/7/pwsh.exe") }
        'node' { @("$env:ProgramFiles/nodejs/node.exe", "$env:LOCALAPPDATA/Programs/nodejs/node.exe") }
        default { @() }
    }
    foreach ($candidate in $known) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [IO.Path]::GetFullPath($candidate) } }
    $batchFallback
}

function Get-ForgeRemainingSeconds([Diagnostics.Stopwatch]$Clock, [int]$MaximumSeconds, [int]$ReserveSeconds = 0) {
    if (-not $Clock) { return [Math]::Max(0, $MaximumSeconds - $ReserveSeconds) }
    [Math]::Max(0, [int][Math]::Floor($MaximumSeconds - $Clock.Elapsed.TotalSeconds - $ReserveSeconds))
}

function Get-ForgeSkillDestination([string]$Agent) {
    if ($Agent -eq 'claude') { return Join-Path $env:USERPROFILE '.claude/skills/delivery-forge' }
    $legacy = Join-Path $env:USERPROFILE '.codex/skills/delivery-forge'
    $standard = Join-Path $env:USERPROFILE '.agents/skills/delivery-forge'
    if ((Test-Path -LiteralPath $legacy) -and (Test-Path -LiteralPath $standard)) { throw 'Both Codex skill locations exist; preserve them and resolve the duplicate before bootstrap.' }
    if (Test-Path -LiteralPath $legacy) { return $legacy }
    $standard
}

function Test-ForgeDiagnosticArguments([string[]]$Arguments) {
    if ($Arguments.Count -eq 1 -and $Arguments[0] -cin @('--help', '--version')) { return $true }
    $Arguments.Count -eq 2 -and $Arguments[0] -cin @('plan','implement','pr','merge','resume','capabilities','quality-baseline','setup') -and $Arguments[1] -cin @('--help','--version')
}

function Get-ForgeRequiredAuthentication([string[]]$Arguments) {
    if (-not $Arguments -or (Test-ForgeDiagnosticArguments $Arguments)) { return @() }
    switch -CaseSensitive ($Arguments[0]) {
        'implement' { @('codex', 'claude') }
        'pr' { @('gh', 'codex', 'claude') }
        'merge' { @('gh', 'codex', 'claude') }
        'plan' { if ($Arguments -ccontains '--issue') { @('gh') } }
    }
}

function Get-ForgeRequiredTools([string[]]$Arguments) {
    'pwsh'
    if (-not $Arguments -or (Test-ForgeDiagnosticArguments $Arguments)) { return }
    switch -CaseSensitive ($Arguments[0]) {
        'plan' { 'git'; if ($Arguments -ccontains '--issue') { 'gh' } }
        'implement' { 'git'; 'codex'; 'claude' }
        'pr' { 'git'; 'gh'; 'codex'; 'claude' }
        'merge' { 'git'; 'gh'; 'codex'; 'claude' }
        'setup' { 'git' }
        'quality-baseline' { 'git' }
        # Standalone resume only observes local history or refuses without its
        # fresh same-owner host; JSON does not authorize new worker/publication.
    }
}

function Save-ForgeUnknown([string]$Root) {
    $script:ForgeBootstrapUnsettled = $true
    $path = Join-Path $Root 'bootstrap.lock'
    if (-not (Test-Path -LiteralPath $path)) {
        $null = Assert-ForgePlainPath $Root -Directory
        $null = New-Item -ItemType Directory -Path $Root -Force
        try { Write-ForgeNewJson $path @{ Kind = 'DeliveryForgeBootstrapUnknown-v1'; Reason = 'Native process/output disposition is unverified. Preserve this attempt; no automatic retry.'; ProcessObservation = $script:ForgeUnknownProcessObservation } }
        catch { if (-not (Test-Path -LiteralPath $path)) { throw } }
    }
}

function Write-ForgeImmutableBytes([string]$Path, [byte[]]$Bytes) {
    $null = Assert-ForgePlainPath $Path
    $parent = Split-Path -Parent $Path; $null = Assert-ForgePlainPath $parent -Directory
    $null = New-Item -ItemType Directory -Path $parent -Force
    if (Test-Path -LiteralPath $Path) {
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) -ne [Convert]::ToBase64String($Bytes)) { throw 'An immutable installation file differs; preserve it.' }
        return
    }
    $stage = $Path + '.stage-' + [guid]::NewGuid().ToString('N')
    $null = Assert-ForgePlainPath $stage
    $stream = [IO.File]::Open($stage, 'CreateNew', 'Write', 'None')
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($stage)) -ne [Convert]::ToBase64String($Bytes)) { throw 'Immutable stage readback differs; preserve the stage and previous selection.' }
    $null = Assert-ForgePlainPath $stage; $null = Assert-ForgePlainPath $Path
    [IO.File]::Move($stage, $Path) # Same-parent, no-overwrite publication; failed stages remain evidence.
}

function Invoke-ForgeProcess {
    param([string]$Executable, [string[]]$Arguments = @(), [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 20, [hashtable]$Environment = @{}, [string]$LogPrefix,
        [Diagnostics.Stopwatch]$Clock, [int]$MaximumSeconds = 1200, [switch]$ForwardOutput, [string]$StandardInput)
    $extension = [IO.Path]::GetExtension($Executable)
    if ($extension -in @('.cmd', '.bat')) { throw 'FORGE_BOOTSTRAP_UNSUPPORTED_BATCH: select an existing native executable or .ps1 entry; batch argument semantics are unsupported and no duplicate install was attempted.' }
    if ($extension -ieq '.ps1' -and $PSBoundParameters.ContainsKey('StandardInput')) { throw 'FORGE_BOOTSTRAP_UNSUPPORTED_SHIM_STDIN: explicit stdin cannot be combined with a .ps1 transport envelope.' }
    $timeout = [Math]::Min($TimeoutSeconds, (Get-ForgeRemainingSeconds $Clock $MaximumSeconds 5))
    if ($timeout -lt 1) { throw 'The original bootstrap/command deadline expired before native entry.' }
    # Validate every process path/argument before native entry. The Windows
    # process API carries Unicode directly; only internal stdin needs encoding.
    $executable64 = ConvertTo-ForgeWireString $Executable
    if ($WorkingDirectory) { $null = ConvertTo-ForgeWireString $WorkingDirectory }
    $argument64 = @()
    foreach ($argument in $Arguments) {
        if ($null -eq $argument) { throw 'Null is not a native argument; use an explicit empty string.' }
        $argument64 += ConvertTo-ForgeWireString $argument
    }
    if ($null -ne $StandardInput -and $StandardInput -match '[^\x00-\x7f]') { throw 'Internal stdin must be ASCII encoded before native entry.' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable; $inputText = $StandardInput
    if ($extension -ieq '.ps1') {
        $shell = Find-ForgeCommand 'pwsh'
        if (-not $shell -or [IO.Path]::GetExtension($shell) -ine '.exe') { $shell = "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" }
        $inputText = @{ Executable64 = $executable64; Arguments64 = @($argument64) } | ConvertTo-Json -Depth 5 -Compress
        $null = ConvertTo-ForgeWireString $shell
        $info.FileName = $shell
        $Arguments = @('-NoProfile', '-NonInteractive', '-Command', '$ErrorActionPreference=''Stop'';$u=New-Object Text.UTF8Encoding($false,$true);$m=New-Object IO.MemoryStream;[Console]::OpenStandardInput().CopyTo($m);$r=$u.GetString($m.ToArray()).TrimStart([char]0xfeff)|ConvertFrom-Json;$e=$u.GetString([Convert]::FromBase64String($r.Executable64));$a=@($r.Arguments64|ForEach-Object{$u.GetString([Convert]::FromBase64String($_))});& $e @a;if($null -eq $LASTEXITCODE){exit 125}else{exit $LASTEXITCODE}')
    }
    $info.Arguments = (@($Arguments | ForEach-Object { ConvertTo-ForgeNativeArgument $_ }) -join ' ')
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true; $info.RedirectStandardInput = $true
    if ($WorkingDirectory) { $info.WorkingDirectory = $WorkingDirectory }
    foreach ($key in $Environment.Keys) { $info.EnvironmentVariables[$key] = [string]$Environment[$key] }
    $process = New-Object Diagnostics.Process; $process.StartInfo = $info
    $stdout = $null; $stderr = $null; $started = $false; $settled = $false; $nativePid = $null; $nativeStart = 'unobserved'
    try {
        if (-not $process.Start()) { throw 'Native command did not start.' }; $started = $true
        $nativePid = $process.Id; $nativeStart = $process.StartTime.ToUniversalTime().ToString('o')
        if ($ForwardOutput) {
            $stdout = $process.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput())
            $stderr = $process.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
        } else { $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync() }
        if ($null -ne $inputText) { $inputBytes = [Text.Encoding]::ASCII.GetBytes($inputText); $process.StandardInput.BaseStream.Write($inputBytes, 0, $inputBytes.Length); $process.StandardInput.BaseStream.Flush() }; $process.StandardInput.BaseStream.Close()
        if (-not $process.WaitForExit($timeout * 1000)) { throw 'The bounded native command timed out.' }
        $drain = [Math]::Min(5000, 1000 * (Get-ForgeRemainingSeconds $Clock $MaximumSeconds))
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), [int]$drain)) { throw 'Native output did not settle.' }
        $settled = $true
        $value = [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = ''; Stderr = '' }
        if (-not $ForwardOutput) { $value.Stdout = $stdout.Result; $value.Stderr = $stderr.Result }
        if ($LogPrefix -and -not $ForwardOutput) {
            [IO.File]::WriteAllText($LogPrefix + '.stdout.txt', $value.Stdout, (New-Object Text.UTF8Encoding $false))
            [IO.File]::WriteAllText($LogPrefix + '.stderr.txt', $value.Stderr, (New-Object Text.UTF8Encoding $false))
        }
        $value
    } catch {
        if ($started -and -not $settled) {
            $script:ForgeBootstrapUnsettled = $true
            $script:ForgeUnknownProcessObservation = @{ ObservedPid = $nativePid; ObservedStartUtc = $nativeStart; ContainingPid = $PID; Executable = $info.FileName; Scope = 'Observed prerequisite or forwarder process; no descendant closure claim.' }
            try { if (-not $process.HasExited) { if ($PSVersionTable.PSVersion.Major -ge 7) { $process.Kill($true) } else { $process.Kill() } } } catch { }
            Write-ForgeDiagnostic ("FORGE_BOOTSTRAP_UNKNOWN_PROCESS: pid={0}; startUtc={1}; kill was best-effort and does not prove tree closure." -f $nativePid, $nativeStart)
            throw 'FORGE_BOOTSTRAP_UNKNOWN: native process/output completion is unverified; preserve evidence and do not retry automatically.'
        }
        throw
    } finally {
        if ($LogPrefix -and -not $ForwardOutput) {
            foreach ($entry in @(@($stdout, '.stdout.txt'), @($stderr, '.stderr.txt'))) {
                $path = $LogPrefix + $entry[1]
                if ($entry[0] -and $entry[0].Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion -and -not (Test-Path -LiteralPath $path)) {
                    [IO.File]::WriteAllText($path, $entry[0].Result, (New-Object Text.UTF8Encoding $false))
                }
            }
        }
        $process.Dispose()
    }
}

function New-ForgeCheck([string]$Id, [string]$Status, [string]$Detail, [bool]$Automatic, [string[]]$Steps = @()) {
    [pscustomobject]@{ Id = $Id; Status = $Status; Detail = $Detail; Automatic = $Automatic; Steps = @($Steps) }
}

function Get-ForgeRequiredPackages([string[]]$RequiredTools = @('git','gh','codex','claude','pwsh')) {
    $packages = [ordered]@{ git = 'Git.Git'; gh = 'GitHub.cli'; codex = 'OpenAI.Codex'; claude = 'Anthropic.ClaudeCode'; pwsh = 'Microsoft.PowerShell' }
    $codex = if ('codex' -in $RequiredTools) { Find-ForgeCommand 'codex' } else { $null }
    # Native Codex does not need Node. Retain an existing npm installation and
    # repair its prerequisite instead of creating a competing Codex installation.
    if ($codex -and [IO.Path]::GetExtension($codex) -ieq '.ps1') { $packages['node'] = 'OpenJS.NodeJS.LTS' }
    $selected = [ordered]@{}
    foreach ($name in $packages.Keys) { if ($name -in $RequiredTools -or ($name -eq 'node' -and 'codex' -in $RequiredTools)) { $selected[$name] = $packages[$name] } }
    $selected
}

function Get-ForgeSdkVersion([string]$SelectedSource) {
    $path = if ($SelectedSource) { Join-Path $SelectedSource 'global.json' } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'global.json' }
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $selected = Read-ForgeUtf8Text $path | ConvertFrom-Json
        if ($selected.sdk.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Source global.json must pin an exact stable SDK version.' }
        return [string]$selected.sdk.version
    }
    '10.0.401' # Known source pin until the selected remote checkout is available.
}

function Read-ForgeInstallationSelection([string]$Root) {
    $path = Join-Path $Root 'installation.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try {
        $null = Assert-ForgePlainPath $path
        if ((Get-Item -LiteralPath $path).Length -gt 1MB) { return $null }
        $record = Read-ForgeUtf8Text $path | ConvertFrom-Json
        if ($record.Kind -ne 'DeliveryForgeBootstrapInstallation-v1') { return $null }
        if ($record.SourceCommit -notmatch '^[a-f0-9]{40}$') { return $null }
        $record
    } catch { $null }
}

function Test-ForgeNativeStartReceiptParameter([string]$Path, [string]$Parameter = 'NativeStartReceipt') {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw 'Pinned native launcher has parse errors.' }
    @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq $Parameter }).Count -eq 1
}

function Read-ForgeInstallation([string]$Root) {
    try {
        $record = Read-ForgeInstallationSelection $Root
        if (-not $record) { return $null }
        foreach ($field in @('NativeLauncher', 'BootstrapScript')) {
            $selected = [string]$record.$field; Assert-ForgeWithin $Root $selected; $null = Assert-ForgePlainPath $selected
            if (-not (Test-Path -LiteralPath $selected -PathType Leaf)) { return $null }
            $hashField = $field + 'Sha256'
            if ((Get-FileHash -LiteralPath $selected -Algorithm SHA256).Hash.ToLowerInvariant() -ne $record.$hashField) { return $null }
        }
        if ($null -ne $record.NativeStartReceiptSupported -and $record.NativeStartReceiptSupported -isnot [bool]) { return $null }
        if ($record.NativeStartReceiptSupported -eq $true -and -not (Test-ForgeNativeStartReceiptParameter $record.NativeLauncher)) { return $null }
        if ($null -ne $record.NativeCompletionReceiptSupported -and $record.NativeCompletionReceiptSupported -isnot [bool]) { return $null }
        if ($record.NativeCompletionReceiptSupported -eq $true -and (-not $record.NativeStartReceiptSupported -or -not (Test-ForgeNativeStartReceiptParameter $record.NativeLauncher 'NativeCompletionReceipt'))) { return $null }
        Assert-ForgeWithin $Root $record.ToolAssembly
        $null = Assert-ForgePlainPath $record.ToolAssembly
        if (-not (Test-Path -LiteralPath $record.ToolAssembly -PathType Leaf)) { return $null }
        if ((Get-FileHash -LiteralPath $record.ToolAssembly -Algorithm SHA256).Hash.ToLowerInvariant() -ne $record.ToolSha256) { return $null }
        $null = Assert-ForgePlainPath $record.DotNetHost
        if (-not (Test-Path -LiteralPath $record.DotNetHost -PathType Leaf)) { return $null }
        if ((Get-FileHash -LiteralPath $record.DotNetHost -Algorithm SHA256).Hash.ToLowerInvariant() -ne $record.DotNetSha256) { return $null }
        if (-not $record.Files -or $record.Files.Count -gt 4096) { return $null }
        foreach ($pin in $record.Files) {
            Assert-ForgeWithin $Root $pin.Path; $null = Assert-ForgePlainPath $pin.Path
            if (-not (Test-Path -LiteralPath $pin.Path -PathType Leaf) -or (Get-FileHash -LiteralPath $pin.Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.Sha256) { return $null }
        }
        $record
    } catch { $null }
}

function Get-ForgeBootstrapReport {
    param([string]$Root, [string]$SelectedSource, [switch]$SkipAuthentication,
        [string[]]$RequiredAuthentication = @('gh', 'codex', 'claude'), [Diagnostics.Stopwatch]$Clock,
        [int]$MaximumSeconds = 1200, [IO.FileStream]$OwnedLock, [string[]]$RequiredTools = @('git','gh','codex','claude','pwsh','dotnet-sdk','codex-skill','claude-skill'))
    if (-not $Clock) { $Clock = [Diagnostics.Stopwatch]::StartNew() }
    $checks = New-Object 'Collections.Generic.List[object]'
    $windows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    $checks.Add((New-ForgeCheck 'windows' $(if ($windows) { 'Ready' } else { 'Blocked' }) 'Native Windows bootstrap; Linux/macOS installation is not implemented here.' $false @('Use a Windows laptop for this script.')))
    if (-not $windows) { return New-ForgeReport $Root $checks 'unknown' }
    $ownLock = $OwnedLock -and $OwnedLock.CanWrite -and -not $OwnedLock.SafeFileHandle.IsClosed -and
        [string]::Equals([IO.Path]::GetFullPath($OwnedLock.Name), [IO.Path]::GetFullPath((Join-Path $Root 'bootstrap.lock')), [StringComparison]::OrdinalIgnoreCase)
    if ($script:ForgeBootstrapUnsettled) { throw 'FORGE_BOOTSTRAP_UNKNOWN: an earlier native disposition is unresolved.' }
    if ((Test-Path -LiteralPath (Join-Path $Root 'bootstrap.lock')) -and -not $ownLock) {
        $checks.Add((New-ForgeCheck 'bootstrap-lock' 'UserAction' 'Another bootstrap owns the installation, or a previous attempt has unresolved process/IO state.' $false @('Inspect the retained bootstrap attempt and confirm its processes are closed before an operator removes bootstrap.lock. Do not delete it or retry automatically.')))
        $locked = New-ForgeReport $Root $checks 'unknown' $RequiredAuthentication
        $locked.AuthenticationReady = $null; $locked.AuthenticationChecked = $false; $locked.ReportComplete = $false
        return $locked
    }
    $commands = @{}; $versions = @{}
    $packages = Get-ForgeRequiredPackages -RequiredTools $RequiredTools
    foreach ($name in $packages.Keys) {
        $path = Find-ForgeCommand $name; $commands[$name] = $path
        $version = $null
        $batchEntry = $path -and [IO.Path]::GetExtension($path) -in @('.cmd', '.bat')
        $unsupportedPowerShell = $name -eq 'pwsh' -and $path -and [IO.Path]::GetExtension($path) -ine '.exe'
        $unsupportedEntry = $batchEntry -or $unsupportedPowerShell
        if ($path -and -not $unsupportedEntry) {
            try { $probe = Invoke-ForgeProcess $path @('--version') -Clock $Clock -MaximumSeconds $MaximumSeconds; if ($probe.ExitCode -eq 0) { $version = ($probe.Stdout -split '\r?\n')[0].Trim() } } catch { if ($script:ForgeBootstrapUnsettled) { throw } }
        }
        $versions[$name] = $version
        $status = if ($unsupportedEntry) { 'Unsupported' } elseif ($version) { 'Ready' } elseif ($path) { 'Unverified' } else { 'Missing' }
        $steps = if ($status -eq 'Unverified') { @("Run '$path --version' in an ordinary terminal. Check execution permissions or the broken installation; do not blindly install a second copy.") } else { @("winget install --id $($packages[$name]) --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity") }
        if ($batchEntry) { $steps = @('Select an existing supported native executable or .ps1 entry. The batch installation is preserved; do not install a duplicate automatically.') }
        if ($unsupportedPowerShell) { $steps = @('Select an existing native pwsh.exe. The script/batch entry is preserved; do not install a duplicate automatically.') }
        $checks.Add((New-ForgeCheck $name $status $(if ($version) { $version } elseif ($unsupportedPowerShell) { "pwsh resolves only to a non-native script/batch entry; no probe was entered." } elseif ($batchEntry) { "$name resolves only to an unsupported batch entry; no probe was entered." } elseif ($path) { "$name exists but its version probe failed or was denied." } else { "$name CLI is missing." }) ($status -eq 'Missing') $steps))
    }
    $winget = Find-ForgeCommand 'winget'
    $needsWinget = @($checks | Where-Object { $_.Id -in $packages.Keys -and $_.Status -eq 'Missing' }).Count -gt 0
    $checks.Add((New-ForgeCheck 'winget' $(if ($winget) { 'Ready' } elseif ($needsWinget) { 'UserAction' } else { 'NotNeeded' }) 'Windows App Installer supplies WinGet; existing prerequisites do not need reinstalling.' $false @('Install or update App Installer: https://apps.microsoft.com/detail/9NBLGGH4NNS1', 'Open a new terminal and rerun this script.')))
    $installation = Read-ForgeInstallation $Root
    $sdkVersion = if (-not $SelectedSource -and $installation) { $installation.RequiredSdkVersion } else { Get-ForgeSdkVersion $SelectedSource }
    $sdkHost = $null
    if ('dotnet-sdk' -in $RequiredTools) {
        $sdkCandidates = @((Join-Path $Root "sdk/$sdkVersion/dotnet.exe"), (Find-ForgeCommand 'dotnet')) | Where-Object { $_ } | Select-Object -Unique
        $sdkHost = $null
        foreach ($candidate in $sdkCandidates) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                try { $probe = Invoke-ForgeProcess $candidate @('--list-sdks') -Clock $Clock -MaximumSeconds $MaximumSeconds; if ($probe.ExitCode -eq 0 -and $probe.Stdout -match ('(?m)^' + [regex]::Escape($sdkVersion) + '\s')) { $sdkHost = $candidate; break } } catch { if ($script:ForgeBootstrapUnsettled) { throw } }
            }
        }
        $checks.Add((New-ForgeCheck 'dotnet-sdk' $(if ($sdkHost) { 'Ready' } else { 'Missing' }) "Exact source-pinned .NET SDK $sdkVersion; newer major/minor versions are not a substitute." $true @("Run this script with -Mode Install; it installs SDK $sdkVersion privately under $Root/sdk/$sdkVersion without changing system .NET.")))
    } else {
        if ($installation) { $sdkHost = $installation.DotNetHost }
        $checks.Add((New-ForgeCheck 'dotnet-sdk' 'NotNeeded' 'This command uses the hashed installed runtime/CLI; no source SDK probe is required.' $false))
    }
    $launcherCompatible = $installation -and $installation.NativeStartReceiptSupported -eq $true -and $installation.NativeCompletionReceiptSupported -eq $true
    if ($launcherCompatible) {
        try {
            $probe = Invoke-ForgeProcess $installation.DotNetHost @($installation.ToolAssembly, '--version') -Environment @{ DOTNET_ROOT = (Split-Path -Parent $installation.DotNetHost); DOTNET_PROCESSOR_COUNT = '2'; DOTNET_GCHeapHardLimit = '0x100000000' } -Clock $Clock -MaximumSeconds $MaximumSeconds
            $version = $probe.Stdout | ConvertFrom-Json
            if ($probe.ExitCode -ne 0 -or $version.tool -ne 'delivery-forge') { $installation = $null }
        } catch { if ($script:ForgeBootstrapUnsettled) { throw }; $installation = $null }
    }
    if ($installation -and -not $launcherCompatible) {
        $checks.Add((New-ForgeCheck 'forge-cli' 'Unsupported' 'Pinned CLI launcher lacks durable start/completion receipts; existing payload and source selection are preserved. No CLI startup probe was entered.' $false @('Explicitly select a compatible SourceRoot/Ref and run Install. Rebuilding the unchanged pin cannot add the required launcher contract.')))
    } else {
        $checks.Add((New-ForgeCheck 'forge-cli' $(if ($installation) { 'Ready' } else { 'Missing' }) 'Hashed installation plus actual CLI version startup; neither certifies worker execution.' (-not [bool]$installation) @('Run this script with -Mode Install.')))
    }
    foreach ($agent in @('codex', 'claude')) {
        if (($agent + '-skill') -notin $RequiredTools) { $checks.Add((New-ForgeCheck ($agent + '-skill') 'NotNeeded' 'Agent skill is not a prerequisite of this CLI command.' $false)); continue }
        try { $skillRoot = Get-ForgeSkillDestination $agent }
        catch { $checks.Add((New-ForgeCheck ($agent + '-skill') 'Conflict' 'Both Codex skill locations exist; preserve and resolve the duplicate.' $false @('Select the existing installed skill location; do not create a competing copy.'))); continue }
        $marker = Join-Path $skillRoot '.forge-bootstrap-skill.json'
        $status = Get-ForgeSkillStatus $skillRoot
        $detail = switch ($status) { 'Ready' { $skillRoot } 'Conflict' { 'An existing unmanaged or modified skill is preserved; review it before replacement.' } 'Repairable' { 'A managed skill is missing files; its remaining files match the retained manifest and can be repaired.' } default { "Install skill at $skillRoot." } }
        $checks.Add((New-ForgeCheck ($agent + '-skill') $status $detail ($status -in @('Missing', 'Repairable')) @($(if ($status -eq 'Conflict') { "Compare $skillRoot with the source skill; preserve customizations and explicitly move the conflicting installation before rerunning." } else { 'Run this script with -Mode Install.' }))))
    }
    if (-not $SkipAuthentication) {
        foreach ($agent in @('gh', 'codex', 'claude')) {
            if ($agent -notin $RequiredAuthentication) {
                $checks.Add((New-ForgeCheck ($agent + '-authentication') 'NotNeeded' 'Not required for this command; no authentication status probe was run.' $false)); continue
            }
            $status = 'UserAction'; $detail = 'Authentication has not been verified; this alone does not prove that you are signed out.'
            $unsupported = $false
            if ($commands[$agent] -and [IO.Path]::GetExtension($commands[$agent]) -notin @('.cmd', '.bat')) {
                try {
                    $arguments = switch ($agent) { 'gh' { @('auth', 'status', '--active', '--hostname', 'github.com') } 'codex' { @('login', 'status') } 'claude' { @('auth', 'status') } }
                    if ($agent -eq 'gh') {
                        $help = Invoke-ForgeProcess $commands[$agent] @('auth','status','--help') -Clock $Clock -MaximumSeconds $MaximumSeconds
                        if ($help.ExitCode -eq 0 -and ($help.Stdout + $help.Stderr) -match '(?m)(?:^|\s)--no-refresh(?:\s|,|$)') { $arguments += '--no-refresh' }
                        elseif ($help.ExitCode -ne 0 -or $versions[$agent] -notmatch '^gh version 2\.86\.0(?:\s|$)') {
                            $unsupported = $true; throw 'GitHub auth-status mutation policy is unverified for this version; no authentication probe was entered.'
                        }
                        # Exact legacy2.86.0 status.go is read-only; newer help
                        # advertising refresh suppression must receive the flag.
                    }
                    # Deliberately never persist or print account-status stdout/stderr.
                    $auth = Invoke-ForgeProcess $commands[$agent] $arguments -Clock $Clock -MaximumSeconds $MaximumSeconds
                    $unsupported = $auth.ExitCode -ne 0 -and $auth.Stderr -match '(?i)unknown (flag|command|option)|unrecognized (argument|option)'
                    $valid = $auth.ExitCode -eq 0
                    if ($agent -eq 'codex') { $valid = $valid -and (($auth.Stdout + $auth.Stderr) -match '(?i)ChatGPT') }
                    if ($agent -eq 'claude' -and $valid) { $account = $auth.Stdout | ConvertFrom-Json; $valid = $account.loggedIn -eq $true -and $account.authMethod -eq 'claude.ai' -and $account.apiProvider -eq 'firstParty' }
                    if ($valid) { $status = 'Ready'; $detail = 'Existing authentication status verified; no credentials were copied or logged.' }
                } catch { if ($script:ForgeBootstrapUnsettled) { throw } }
            }
            $step = switch ($agent) { 'gh' { 'gh auth login --hostname github.com --web' } 'codex' { 'codex login (choose your ChatGPT subscription in the browser)' } 'claude' { 'claude auth login (choose your Claude subscription, not Console/API billing)' } }
            $steps = @($step, 'If already signed in, inspect the status command before signing in again. Complete any required browser sign-in yourself, then rerun the check.')
            if ($unsupported) { $detail = 'The installed CLI does not support the required non-mutating authentication-status command.'; $steps = @("Update the CLI: winget upgrade --id $($packages[$agent]) --exact --source winget", 'Rerun the check. An unsupported status command is not a reason to discard or repeat an existing sign-in.') }
            $checks.Add((New-ForgeCheck ($agent + '-authentication') $status $detail $false $steps))
        }
        if (@($RequiredAuthentication | Where-Object { $_ -in @('codex','claude') }).Count -gt 0) {
            $overrides = @('OPENAI_API_KEY', 'CODEX_API_KEY', 'OPENAI_BASE_URL', 'ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_BASE_URL', 'CLAUDE_CODE_USE_BEDROCK', 'CLAUDE_CODE_USE_VERTEX', 'CLAUDE_CODE_USE_FOUNDRY', 'CLAUDE_CODE_SIMPLE') | Where-Object {
                $value = [Environment]::GetEnvironmentVariable($_)
                -not [string]::IsNullOrEmpty($value) -and ($_ -notlike 'CLAUDE_CODE_*' -or $value -notin @('0', 'false', 'False'))
            }
            if ($overrides.Count -gt 0) { $checks.Add((New-ForgeCheck 'provider-environment' 'UserAction' ('Subscription routing needs review; environment variable names present: ' + ($overrides -join ', ')) $false @('Review these variables yourself for this Forge session. Bootstrap never prints their values, clears them or changes your provider.'))) }
        }
    }
    $report = New-ForgeReport $Root $checks $sdkVersion $RequiredAuthentication
    $report.AuthenticationChecked = -not [bool]$SkipAuthentication -and $RequiredAuthentication.Count -gt 0
    if ($SkipAuthentication -or $RequiredAuthentication.Count -eq 0) { $report.AuthenticationReady = $null }
    $report | Add-Member NoteProperty DotNetHost $sdkHost
    $report | Add-Member NoteProperty RequiredTools @($RequiredTools)
    if ($installation -and $null -ne $installation.BootstrapMatchesSelectedSource) { $report | Add-Member NoteProperty BootstrapMatchesSelectedSource $installation.BootstrapMatchesSelectedSource }
    $report
}

function New-ForgeReport([string]$Root, $Checks, [string]$SdkVersion, [string[]]$RequiredAuthentication = @('gh', 'codex', 'claude')) {
    $rows = @($Checks.ToArray())
    $pending = @($rows | Where-Object { $_.Status -notin @('Ready', 'NotNeeded') })
    $gating = @($pending | Where-Object {
        if ($_.Id -like '*-authentication') { return $_.Id.Replace('-authentication', '') -in $RequiredAuthentication }
        if ($_.Id -eq 'provider-environment') { return @($RequiredAuthentication | Where-Object { $_ -in @('codex', 'claude') }).Count -gt 0 }
        $true
    })
    [pscustomobject]@{
        Kind = 'DeliveryForgeBootstrapReport-v1'; StateRoot = $Root; RequiredSdkVersion = $SdkVersion
        InstallationReady = @($pending | Where-Object { $_.Id -notlike '*-authentication' -and $_.Id -ne 'provider-environment' }).Count -eq 0
        AuthenticationChecked = $false; AuthenticationReady = @($gating | Where-Object { $_.Id -like '*-authentication' -or $_.Id -eq 'provider-environment' }).Count -eq 0
        ReportComplete = $true; ExecutionCertified = $false; ExecutionReadiness = 'NotEvaluated'
        Checks = $rows; AutomatableActions = @($pending | Where-Object Automatic); UserActions = @($pending | Where-Object { -not $_.Automatic })
        RequiredAuthentication = @($RequiredAuthentication); ExitCode = $(if ($gating.Count -eq 0) { 0 } else { 2 })
        NextExecutionSteps = @('The installed owner must still reconcile the selected plan and verify current host admission, Continuum and project quality/review policy before dispatch. Bootstrap does not manufacture acceptance receipts.')
    }
}

function Write-ForgeNewJson([string]$Path, $Value) {
    $raw = (New-Object Text.UTF8Encoding $false).GetBytes(($Value | ConvertTo-Json -Depth 30))
    $stream = [IO.File]::Open($Path, 'CreateNew', 'Write', 'None')
    try { $stream.Write($raw, 0, $raw.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

function Get-ForgeSkillStatus([string]$Root) {
    try {
        $null = Assert-ForgePlainPath $Root -Directory
        if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return 'Missing' }
        $marker = Join-Path $Root '.forge-bootstrap-skill.json'
        $null = Assert-ForgePlainPath $marker
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or (Get-Item -LiteralPath $marker).Length -gt 1MB) { return 'Conflict' }
        $record = Read-ForgeUtf8Text $marker | ConvertFrom-Json
        if ($record.Kind -ne 'DeliveryForgeBootstrapSkill-v1' -or $record.Files.Count -lt 2 -or $record.Files.Count -gt 4096) { return 'Conflict' }
        $missing = $false; $selected = @{}
        foreach ($pin in $record.Files) {
            $path = Join-Path $Root $pin.RelativePath; Assert-ForgeWithin $Root $path; $null = Assert-ForgePlainPath $path
            if ($selected.ContainsKey($path)) { return 'Conflict' }; $selected[$path] = $true
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $missing = $true }
            elseif ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.Sha256) { return 'Conflict' }
        }
        $entries = @(Get-ChildItem -LiteralPath $Root -Recurse -Force | Select-Object -First 4097)
        if ($entries.Count -gt 4096) { return 'Conflict' }
        foreach ($entry in $entries) {
            $null = Assert-ForgePlainPath $entry.FullName -Directory:$entry.PSIsContainer
            if (-not $entry.PSIsContainer -and $entry.FullName -ne $marker -and -not $selected.ContainsKey($entry.FullName)) { return 'Conflict' }
        }
        if ($missing) { 'Repairable' } else { 'Ready' }
    } catch { 'Conflict' }
}

function Test-ForgeSkill([string]$Root) { (Get-ForgeSkillStatus $Root) -eq 'Ready' }

function Install-ForgeSkill([string]$SelectedSource, [string]$Destination, [string]$Launcher, [string]$Commit) {
    $null = Assert-ForgePlainPath $Destination -Directory
    if (Test-Path -LiteralPath $Destination) { throw 'Skill staging requires a fresh destination; existing skills are preserved and changed only by installation activation.' }
    $files = @((Join-Path $SelectedSource 'SKILL.md'))
    foreach ($folder in @('references', 'docs')) {
        $files += @(Get-ChildItem -LiteralPath (Join-Path $SelectedSource $folder) -Filter '*.md' -File | Where-Object { $_.Name -notmatch '^(review-remediation-|astra-dry-gate-options|fable-dry-gate-options)' } | ForEach-Object FullName)
    }
    if ($files.Count -gt 512) { throw 'Skill payload exceeds the finite file count.' }
    $total = 0L
    foreach ($file in $files) { $null = Assert-ForgePlainPath $file; $total += (Get-Item -LiteralPath $file).Length }
    if ($total -gt 16MB) { throw 'Skill payload exceeds sixteen MiB.' }
    $null = New-Item -ItemType Directory -Path $Destination -ErrorAction Stop
    $pins = @()
    foreach ($file in $files) {
        $relative = $file.Substring($SelectedSource.TrimEnd('\', '/').Length + 1)
        $target = Join-Path $Destination $relative; Assert-ForgeWithin $Destination $target
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
        [IO.File]::Copy($file, $target, $false)
        if ($relative -eq 'SKILL.md') {
            $addition = "`r`n`r`n## Local bootstrap and invocation`r`nUse the installed launcher: ``$Launcher``. It checks prerequisites and automatically repairs missing installable dependencies once, then invokes the CLI. Do not bypass it with a raw tool shim. On bootstrap exit 2, complete the report's user actions and retry; do not repeatedly reinstall. Authentication is always completed by the user. For runtime admission, Continuum or policy failures, report the actual prerequisite; reinstalling does not certify execution.`r`n"
            [IO.File]::AppendAllText($target, $addition, (New-Object Text.UTF8Encoding $false))
        }
        $pins += @{ RelativePath = $relative; Sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    Write-ForgeNewJson (Join-Path $Destination '.forge-bootstrap-skill.json') @{ Kind = 'DeliveryForgeBootstrapSkill-v1'; SourceCommit = $Commit; Launcher = $Launcher; Files = $pins }
    if (-not (Test-ForgeSkill $Destination)) { throw 'Skill readback failed; partial installation remains preserved.' }
}

function Get-ForgeLauncherContent {
    @'
#requires -Version 5.1
$forwarded = @($args)
$hostArguments = [Environment]::GetCommandLineArgs()
$fileArgument = -1; $unsupportedHostPrefix = $false; $unsupportedHostOption = ''; $implicitEntry = $false
for ($index = 1; $index -lt $hostArguments.Length; $index++) {
    $option = $hostArguments[$index]
    if ($option.StartsWith('/')) { $option = '-' + $option.Substring(1) }
    if ($option.Length -gt 1 -and ('-Command'.StartsWith($option, [StringComparison]::OrdinalIgnoreCase) -or '-EncodedCommand'.StartsWith($option, [StringComparison]::OrdinalIgnoreCase) -or '-CommandWithArgs'.StartsWith($option, [StringComparison]::OrdinalIgnoreCase))) { break }
    if ($option -ieq '-File' -or $option -ieq '-f') {
        if ($index + 1 -lt $hostArguments.Length) {
            try { if ([string]::Equals([IO.Path]::GetFullPath($hostArguments[$index + 1]), [IO.Path]::GetFullPath($PSCommandPath), [StringComparison]::OrdinalIgnoreCase)) { $fileArgument = $index + 1 } } catch { }
        }
        break
    }
    if ($option -in @('-NoProfile', '-NoLogo', '-NonInteractive', '-STA', '-MTA', '-Login')) { continue }
    if ($option -in @('-ExecutionPolicy', '-WindowStyle', '-InputFormat', '-OutputFormat', '-WorkingDirectory', '-SettingsFile', '-ConfigurationName', '-Version')) { $index++; continue }
    if ($option.StartsWith('-')) { $unsupportedHostPrefix = $true; if (-not $unsupportedHostOption) { $unsupportedHostOption = $option }; continue }
    if ($unsupportedHostPrefix -and [IO.Path]::GetExtension($option) -ine '.ps1') { continue }
    try { if ([string]::Equals([IO.Path]::GetFullPath($option), [IO.Path]::GetFullPath($PSCommandPath), [StringComparison]::OrdinalIgnoreCase)) { $fileArgument = $index; $implicitEntry = $true } } catch { }
    break # Another script is not this -File entry; do not inspect its argv.
}
if ($fileArgument -ge 0) {
    if ($implicitEntry -and $PSVersionTable.PSVersion.Major -lt 6) { throw 'FORGE_BOOTSTRAP_AMBIGUOUS_FILE_ARGV: Windows PowerShell implicit script entry uses command semantics; use explicit -File or a string-array splat.' }
    if ($unsupportedHostPrefix) { throw "FORGE_BOOTSTRAP_AMBIGUOUS_FILE_ARGV: unsupported host switch '$unsupportedHostOption'; use full PowerShell host switch spellings and explicit -File. -NoExit is unsupported before original command entry." }
    $forwarded = [string[]]@()
    if ($fileArgument + 1 -lt $hostArguments.Length) { $forwarded = [string[]]@($hostArguments[($fileArgument + 1)..($hostArguments.Length - 1)]) }
}
foreach ($value in $forwarded) {
    if ($value -is [Collections.IEnumerable] -and $value -isnot [string]) { throw 'FORGE_BOOTSTRAP_AMBIGUOUS_ARGUMENT: use an explicit string-array splat; nested argument collections are not native strings.' }
    if ($fileArgument -lt 0 -and $value -is [string] -and $value -match '^-.*:$') { throw 'FORGE_BOOTSTRAP_AMBIGUOUS_ARGUMENT: a direct trailing-colon argument may have been split by PowerShell; use explicit -File to preserve it.' }
}
[string[]]$forwarded = @($forwarded)
if ($PSVersionTable.PSVersion.Major -eq 5) { Import-Module -Name (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1') -Force -ErrorAction Stop }
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$recordPath = Join-Path $root 'installation.json'
if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf) -or (Get-Item -LiteralPath $recordPath).Length -gt 1MB) { throw 'Select the retained bootstrap installation before command entry.' }
$record = [IO.File]::ReadAllText($recordPath, (New-Object Text.UTF8Encoding($false, $true))) | ConvertFrom-Json
$bootstrap = [IO.Path]::GetFullPath([string]$record.BootstrapScript)
if (-not $bootstrap.StartsWith(([IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw 'Bootstrap selection escaped its installation.' }
$cursor = $bootstrap
while ($cursor) { if (Test-Path -LiteralPath $cursor) { if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Bootstrap selection is redirected.' } }; $cursor = [IO.Path]::GetDirectoryName($cursor) }
if ((Get-FileHash -LiteralPath $bootstrap -Algorithm SHA256).Hash.ToLowerInvariant() -ne $record.BootstrapScriptSha256) { throw 'Bootstrap selection hash differs.' }
. $bootstrap
Invoke-ForgeCommand $root -Arguments $forwarded
exit $script:ForgeCommandExitCode
'@
}

function Test-ForgeNativeCompletion([string]$StartPath, [string]$CompletionPath, [int]$ExitCode) {
    try {
        foreach ($path in @($StartPath, $CompletionPath)) {
            $null = Assert-ForgePlainPath $path
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -gt 4096) { return $false }
        }
        $started = Read-ForgeUtf8Text $StartPath | ConvertFrom-Json
        $ended = Read-ForgeUtf8Text $CompletionPath | ConvertFrom-Json
        if ($started.Kind -ne 'DeliveryForgeNativeStart-v1' -or $ended.Kind -ne 'DeliveryForgeNativeCompletion-v1') { return $false }
        foreach ($field in @('NativeExited', 'StdoutEof', 'StderrEof')) { if ($ended.$field -isnot [bool] -or $ended.$field -ne $true) { return $false } }
        foreach ($field in @('NativePid', 'LauncherPid')) {
            foreach ($value in @($started.$field, $ended.$field)) { if (($value -isnot [int] -and $value -isnot [long]) -or $value -le 0 -or $value -gt [int]::MaxValue) { return $false } }
            if ($ended.$field -ne $started.$field) { return $false }
        }
        foreach ($field in @('NativeStartUtc', 'LauncherStartUtc')) {
            if ([string]::IsNullOrWhiteSpace([string]$started.$field) -or [string]::IsNullOrWhiteSpace([string]$ended.$field)) { return $false }
            $first = [DateTimeOffset]$started.$field; $last = [DateTimeOffset]$ended.$field
            if ($first.UtcDateTime.Ticks -le 0 -or $last.UtcDateTime.Ticks -ne $first.UtcDateTime.Ticks) { return $false }
        }
        if ($ended.NativeStartReceiptSha256 -ne (Get-FileHash -LiteralPath $StartPath -Algorithm SHA256).Hash.ToLowerInvariant()) { return $false }
        ($ended.ExitCode -is [int] -or $ended.ExitCode -is [long]) -and $ended.ExitCode -eq $ExitCode
    } catch { $false }
}

function Invoke-ForgeCommand([string]$Root, [string[]]$Arguments, [int]$MaximumSeconds = 1200) {
    $script:ForgeCommandExitCode = 1; $clock = [Diagnostics.Stopwatch]::StartNew()
    $commandLock = $null; $knownSettled = $false; $dispatchRequested = $false
    if ($null -eq $Arguments) { $Arguments = [string[]]@() }
    $argument64 = @()
    foreach ($argument in $Arguments) {
        if ($null -eq $argument) { throw 'Null is not a command argument; use an explicit empty string.' }
        $argument64 += ConvertTo-ForgeWireString $argument
    }
    $required = @(Get-ForgeRequiredAuthentication $Arguments)
    $requiredTools = @(Get-ForgeRequiredTools $Arguments)
    try {
        $diagnostic = Test-ForgeDiagnosticArguments $Arguments
        $report = Get-ForgeBootstrapReport $Root -SkipAuthentication:$diagnostic -RequiredAuthentication $required -RequiredTools $requiredTools -Clock $clock -MaximumSeconds $MaximumSeconds
        if ($report.AutomatableActions.Count -gt 0 -and -not @($report.Checks | Where-Object Id -eq 'bootstrap-lock').Count) {
            $selected = Read-ForgeInstallationSelection $Root
            if ((Test-Path -LiteralPath (Join-Path $Root 'installation.json')) -and -not $selected) { throw 'Existing installation source selection is malformed; preserve it and explicitly select a known source before repair.' }
            $ref = if ($selected) { $selected.SourceCommit } else { 'main' }
            $report = Invoke-ForgeBootstrapInstall $Root $null $ref $MaximumSeconds -Clock $clock -RequiredAuthentication $required -RequiredTools $requiredTools
        }
        if ($report.ExitCode -ne 0) { Write-ForgeBootstrapReport $report; $script:ForgeCommandExitCode = [int]$report.ExitCode; return }
        $installation = Read-ForgeInstallation $Root
        if (-not $installation) { throw 'Installation readback failed; original command was not started.' }
        if ($installation.NativeStartReceiptSupported -ne $true -or $installation.NativeCompletionReceiptSupported -ne $true) { throw 'Pinned native launcher lacks durable completion receipts; explicitly select a compatible source before original command entry. Existing payload and source selection were preserved.' }
        $pwsh = Find-ForgeCommand 'pwsh'; if (-not $pwsh) { throw 'The selected PowerShell7 launcher host is missing.' }
        $null = ConvertTo-ForgeWireString $Root
        $attempt = Join-Path $Root ('bootstrap/command-' + [guid]::NewGuid().ToString('N'))
        Assert-ForgeWithin $Root $attempt; $null = Assert-ForgePlainPath $attempt -Directory
        $null = New-Item -ItemType Directory -Path $attempt
        $receipt = Join-Path $attempt 'native-start.json'
        $completionReceipt = Join-Path $attempt 'native-completion.json'
        $payload = @{ NativeLauncher64 = (ConvertTo-ForgeWireString $installation.NativeLauncher); DotNetHost64 = (ConvertTo-ForgeWireString $installation.DotNetHost); ToolAssembly64 = (ConvertTo-ForgeWireString $installation.ToolAssembly); NativeStartReceipt64 = (ConvertTo-ForgeWireString $receipt); NativeCompletionReceipt64 = (ConvertTo-ForgeWireString $completionReceipt); Arguments64 = @($argument64) }
        $lockPath = Join-Path $Root 'bootstrap.lock'; $null = Assert-ForgePlainPath $lockPath
        $commandLock = [IO.File]::Open($lockPath, 'CreateNew', 'ReadWrite', 'None')
        # The durable lock is born and flushed BEFORE any original CLI can start.
        $containing = Get-Process -Id $PID
        $lockData = @{ Kind = 'DeliveryForgeCommandOriginal-v1'; ContainingPid = $PID; ContainingStartUtc = $containing.StartTime.ToUniversalTime().ToString('o'); AttemptDirectory = $attempt; NativeStartReceipt = $receipt; NativeCompletionReceipt = $completionReceipt } | ConvertTo-Json -Depth 5 -Compress
        $lockBytes = (New-Object Text.UTF8Encoding $false).GetBytes($lockData)
        $commandLock.Write($lockBytes, 0, $lockBytes.Length); $commandLock.Flush($true)
        # Recheck the selected immutable payload while holding its install lock.
        $rechecked = Read-ForgeInstallation $Root
        if (-not $rechecked -or $rechecked.ToolSha256 -ne $installation.ToolSha256 -or $rechecked.NativeLauncherSha256 -ne $installation.NativeLauncherSha256) { throw 'Selected installation changed before original entry.' }
        $remaining = Get-ForgeRemainingSeconds $clock $MaximumSeconds 20
        if ($remaining -lt 1) { throw 'Original invocation deadline expired before command entry.' }
        $payload.MaximumSeconds = $remaining
        $wire = $payload | ConvertTo-Json -Depth 5 -Compress
        $fixed = '$ErrorActionPreference=''Stop'';$u=New-Object Text.UTF8Encoding($false,$true);$m=New-Object IO.MemoryStream;[Console]::OpenStandardInput().CopyTo($m);$r=$u.GetString($m.ToArray()).TrimStart([char]0xfeff)|ConvertFrom-Json;$l=$u.GetString([Convert]::FromBase64String($r.NativeLauncher64));$h=$u.GetString([Convert]::FromBase64String($r.DotNetHost64));$t=$u.GetString([Convert]::FromBase64String($r.ToolAssembly64));$p=$u.GetString([Convert]::FromBase64String($r.NativeStartReceipt64));$c=$u.GetString([Convert]::FromBase64String($r.NativeCompletionReceipt64));$a=@($r.Arguments64|ForEach-Object{$u.GetString([Convert]::FromBase64String($_))});$o=@{DotNetHost=$h;ToolAssembly=$t;MaximumSeconds=$r.MaximumSeconds;NativeStartReceipt=$p;NativeCompletionReceipt=$c;ForgeArguments=$a};& $l @o;if($null -eq $LASTEXITCODE){exit 125}else{exit $LASTEXITCODE}'
        $dispatchRequested = $true
        $result = Invoke-ForgeProcess $pwsh @('-NoProfile', '-NonInteractive', '-Command', $fixed) -TimeoutSeconds ($remaining + 15) -StandardInput $wire -ForwardOutput -Clock $clock -MaximumSeconds $MaximumSeconds
        $script:ForgeCommandExitCode = [int]$result.ExitCode
        $knownSettled = $result.ExitCode -ne 125 -and (Test-ForgeNativeCompletion $receipt $completionReceipt ([int]$result.ExitCode))
        if (-not $knownSettled) {
            $script:ForgeCommandExitCode = 125; $script:ForgeBootstrapUnsettled = $true
            try { Save-ForgeUnknown $Root } catch { Write-ForgeDiagnostic 'FORGE_BOOTSTRAP_UNKNOWN_LOCK_UPDATE_FAILED: exit125 retained; the pre-entry lock and native-start receipt must be preserved.' }
        }
    } catch {
        if ($dispatchRequested -or $script:ForgeBootstrapUnsettled) {
            $script:ForgeBootstrapUnsettled = $true
            $script:ForgeCommandExitCode = 125
            try { Save-ForgeUnknown $Root } catch { Write-ForgeDiagnostic 'FORGE_BOOTSTRAP_UNKNOWN_LOCK_UPDATE_FAILED: exit125 retained; do not retry.' }
        } else { $knownSettled = $true } # No original entry occurred on this failure.
        Write-ForgeDiagnostic $_.Exception.Message
    } finally {
        if ($commandLock) {
            try { $commandLock.Dispose() } catch { Write-ForgeDiagnostic 'Command lock close failed; preserve the selected lock.'; $knownSettled = $false }
            if ($knownSettled -and -not $script:ForgeBootstrapUnsettled) {
                try { [IO.File]::Delete((Join-Path $Root 'bootstrap.lock')) } catch { Write-ForgeDiagnostic 'Settled command lock could not be removed; inspect it before retrying.' }
            }
        }
    }
}

function Assert-ForgeFilePreimage([string]$Path, [string]$ExpectedSha256) {
    $null = Assert-ForgePlainPath $Path
    if ($ExpectedSha256) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedSha256) { throw 'Selected installation entry changed during preparation; its activation was not attempted.' }
    } elseif (Test-Path -LiteralPath $Path) { throw 'An installation entry appeared during preparation; its activation was not attempted.' }
}

function Move-ForgeInstallationDirectory([string]$From, [string]$To) {
    $source = Assert-ForgePlainPath $From -Directory
    $target = Assert-ForgePlainPath $To -Directory
    if (Test-Path -LiteralPath $target) { throw 'Directory move target already exists; preserve both paths.' }
    if (-not [string]::Equals([IO.Path]::GetPathRoot($source), [IO.Path]::GetPathRoot($target), [StringComparison]::OrdinalIgnoreCase)) { throw 'Bootstrap skill activation requires the installation and skill on the same volume; no recursive copy or move was attempted.' }
    [IO.Directory]::Move($source, $target)
}

function Test-ForgeSkillRestoreState([string]$Root, [string]$MarkerHash, [string]$Missing) {
    if ((Get-ForgeSkillStatus $Root) -notin @('Ready', 'Repairable')) { return $false }
    $marker = Join-Path $Root '.forge-bootstrap-skill.json'
    if ((Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash.ToLowerInvariant() -ne $MarkerHash) { return $false }
    $record = Read-ForgeUtf8Text $marker | ConvertFrom-Json
    $nowMissing = @($record.Files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Root $_.RelativePath) -PathType Leaf) } | ForEach-Object RelativePath | Sort-Object) -join [char]0
    $nowMissing -ceq $Missing
}

function Invoke-ForgeBootstrapInstall {
    param([string]$Root, [string]$SelectedSource, [string]$SelectedRef, [int]$Seconds = 1200,
        [Diagnostics.Stopwatch]$Clock, [string[]]$RequiredAuthentication = @('gh', 'codex', 'claude'), [switch]$SourceSelectionExplicit, [string[]]$RequiredTools = @('git','gh','codex','claude','pwsh','dotnet-sdk','codex-skill','claude-skill'))
    $installTools = @(@($RequiredTools) + @('git','pwsh','dotnet-sdk') | Select-Object -Unique)
    $null = Assert-ForgePlainPath $Root -Directory
    if ($script:ForgeBootstrapUnsettled) { throw 'FORGE_BOOTSTRAP_UNKNOWN: earlier native state is unresolved; no installation entered.' }
    $watch = $Clock; if (-not $watch) { $watch = [Diagnostics.Stopwatch]::StartNew() }
    if ((Get-ForgeRemainingSeconds $watch $Seconds 5) -lt 1) { throw 'Original invocation deadline expired before installation.' }
    $null = New-Item -ItemType Directory -Path $Root -Force
    $run = Join-Path $Root ('bootstrap/attempt-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $run -Force
    $lock = $null; $step = 0; $failure = $null
    $activations = New-Object 'Collections.Generic.List[object]'
    $pointerPublished = $false; $facadeActivated = $false; $facadeExisted = $false
    $facadeBackup = Join-Path $run 'delivery-forge.facade.previous.ps1'
    $pointerBackup = Join-Path $run 'installation.previous.json'; $activationPlanPath = $null
    $pointer = Join-Path $Root 'installation.json'; $pointerOldHash = $null
    if (Test-Path -LiteralPath $pointer) { $pointerOldHash = (Get-FileHash -LiteralPath $pointer -Algorithm SHA256).Hash.ToLowerInvariant() }
    function Run-Step([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [hashtable]$Environment = @{}) {
        $left = Get-ForgeRemainingSeconds $watch $Seconds 5
        if ($left -le 5) { throw 'The bootstrap twenty-minute ceiling expired; no new step entered.' }
        $script:ForgeBootstrapStepNumber++
        $prefix = Join-Path $run ('{0:D2}-{1}' -f $script:ForgeBootstrapStepNumber, $Name)
        Write-ForgeNewJson ($prefix + '.request.json') @{ Name = $Name; Executable = $Executable; Arguments = @($Arguments); WorkingDirectory = $WorkingDirectory; EnteredAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); RemainingSeconds = $left }
        $result = Invoke-ForgeProcess $Executable $Arguments -WorkingDirectory $WorkingDirectory -Environment $Environment -TimeoutSeconds $left -LogPrefix $prefix -Clock $watch -MaximumSeconds $Seconds
        Write-ForgeNewJson ($prefix + '.result.json') @{ ExitCode = $result.ExitCode; ReturnedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
        if ($result.ExitCode -ne 0) { throw "Step '$Name' failed with exit $($result.ExitCode). Inspect its retained logs; no automatic retry." }
        $result
    }
    $script:ForgeBootstrapStepNumber = 0
    try {
        $lock = [IO.File]::Open((Join-Path $Root 'bootstrap.lock'), 'CreateNew', 'ReadWrite', 'None')
        $selection = Read-ForgeInstallationSelection $Root
        $destinations = @{}
        foreach ($agent in @('codex', 'claude')) {
            $destination = Get-ForgeSkillDestination $agent
            $null = Assert-ForgePlainPath $destination -Directory
            if ((Get-ForgeSkillStatus $destination) -eq 'Conflict') { throw 'Existing unmanaged/modified skill is preserved; resolve the selected skill before installation.' }
            if (-not [string]::Equals([IO.Path]::GetPathRoot($Root), [IO.Path]::GetPathRoot($destination), [StringComparison]::OrdinalIgnoreCase)) { throw 'Installation and skills must be on the same volume for atomic directory activation; no installation step entered.' }
            $destinations[$agent] = $destination
        }
        $bin = Join-Path $Root 'bin'; $null = Assert-ForgePlainPath $bin -Directory
        $launcher = Join-Path $bin 'delivery-forge.ps1'
        $facadeCandidate = Join-Path $run 'delivery-forge.facade.READY.ps1'
        [IO.File]::WriteAllText($facadeCandidate, (Get-ForgeLauncherContent), (New-Object Text.UTF8Encoding $false))
        $facadeHash = (Get-FileHash -LiteralPath $facadeCandidate -Algorithm SHA256).Hash.ToLowerInvariant()
        if (Test-Path -LiteralPath $launcher) {
            $null = Assert-ForgePlainPath $launcher
            $oldPin = @($selection.Files | Where-Object { $_.Path -eq $launcher })
            $facadeOldHash = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($facadeOldHash -ne $facadeHash -and ($oldPin.Count -ne 1 -or $facadeOldHash -ne $oldPin[0].Sha256)) { throw 'Unmanaged/modified launcher is preserved; no installation step entered.' }
            $facadeExisted = $true
        }
        $report = Get-ForgeBootstrapReport $Root $SelectedSource -RequiredAuthentication $RequiredAuthentication -Clock $watch -MaximumSeconds $Seconds -OwnedLock $lock -RequiredTools $installTools
        # This exact attempt owns the newly created lock, so its own report need
        # not classify that lock as a competing owner.
        $report.Checks = @($report.Checks | Where-Object Id -ne 'bootstrap-lock')
        $winget = Find-ForgeCommand 'winget'
        $packages = Get-ForgeRequiredPackages -RequiredTools $installTools
        foreach ($name in $packages.Keys) {
            if (($report.Checks | Where-Object Id -eq $name).Status -eq 'Missing') {
                if (-not $winget) { continue }
                $null = Run-Step ('install-' + $name) $winget @('install', '--id', $packages[$name], '--exact', '--source', 'winget', '--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity') $run
                # Only the current process sees refreshed PATH; no global settings are changed here.
                $env:PATH = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:PATH
            }
        }
        $git = Find-ForgeCommand 'git'; $pwsh = Find-ForgeCommand 'pwsh'
        if (-not $git -or -not $pwsh) { throw 'Git and native PowerShell 7 are needed to finish installation; follow the prerequisite report.' }
        if ([IO.Path]::GetExtension($pwsh) -ine '.exe') { throw 'Existing pwsh entry is unsupported for installation; select a native pwsh.exe without installing a duplicate automatically.' }
        $existing = Read-ForgeInstallation $Root
        $retainedSource = $false
        if (-not $SelectedSource -and $selection -and -not $SourceSelectionExplicit) {
            $SelectedRef = $selection.SourceCommit
            if (Test-Path -LiteralPath $selection.SourceRoot -PathType Container) { $SelectedSource = $selection.SourceRoot; $retainedSource = $true }
        }
        if ($SelectedSource) {
            try {
                $SelectedSource = Assert-ForgePlainPath $SelectedSource -Directory
                $selected = Run-Step 'read-source' $git @('-C', $SelectedSource, 'rev-parse', '--verify', 'HEAD') $run
                $commit = $selected.Stdout.Trim()
                if ($commit -notmatch '^[a-f0-9]{40}$') { throw 'The selected source HEAD is not one exact commit.' }
                $status = Run-Step 'source-status' $git @('-C', $SelectedSource, 'status', '--porcelain') $run
                if ($status.Stdout.Trim()) { throw 'Selected source checkout is dirty; it is preserved. Use a clean checkout or omit -SourceRoot.' }
                if ($retainedSource -and $commit -ne $selection.SourceCommit) { throw 'Retained source HEAD moved; preserve it and clone the exact recorded commit.' }
                if (-not $retainedSource) {
                    $requested = Run-Step 'resolve-explicit-ref' $git @('-C', $SelectedSource, 'rev-parse', '--verify', '--end-of-options', ($SelectedRef + '^{commit}')) $run
                    if ($requested.Stdout.Trim() -ne $commit) { throw 'Explicit SourceRoot HEAD does not match the explicitly selected Ref; checkout and installation were preserved.' }
                }
            } catch {
                if (-not $retainedSource -or $script:ForgeBootstrapUnsettled) { throw }
                # A known moved/dirty/unavailable retained checkout is evidence,
                # never a reset target. Continue only by a fresh exact-pin clone.
                $SelectedSource = $null
            }
        }
        if (-not $SelectedSource) {
            $SelectedSource = Join-Path $run 'source'
            $null = Run-Step 'clone-source' $git @('clone', '--no-checkout', '--origin', 'origin', 'https://github.com/nucleoid/delivery-forge.git', $SelectedSource) $run @{ GIT_TERMINAL_PROMPT = '0'; GCM_INTERACTIVE = 'Never' }
            try { $selected = Run-Step 'resolve-source' $git @('-C', $SelectedSource, 'rev-parse', '--verify', '--end-of-options', ($SelectedRef + '^{commit}')) $run }
            catch {
                if ($script:ForgeBootstrapUnsettled -or $SelectedRef -match '^[a-f0-9]{40}$') { throw }
                $selected = Run-Step 'resolve-origin-source' $git @('-C', $SelectedSource, 'rev-parse', '--verify', '--end-of-options', ('refs/remotes/origin/' + $SelectedRef + '^{commit}')) $run
            }
            $commit = $selected.Stdout.Trim()
            if ($commit -notmatch '^[a-f0-9]{40}$') { throw 'The selected source did not resolve to one exact commit.' }
            if ($selection -and -not $SourceSelectionExplicit -and $commit -ne $selection.SourceCommit) { throw 'Automatic repair clone did not resolve to its recorded exact commit.' }
            $null = Run-Step 'checkout-source' $git @('-C', $SelectedSource, 'checkout', '--detach', $commit) $run
        }
        $project = Join-Path $SelectedSource 'components/tool/DeliveryForge.Tool/DeliveryForge.Tool.csproj'
        if (-not (Test-Path -LiteralPath $project) -or -not (Test-Path -LiteralPath (Join-Path $SelectedSource 'SKILL.md'))) { throw 'Selected source lacks the public CLI/skill. Select a compatible merged revision.' }
        $sdkVersion = Get-ForgeSdkVersion $SelectedSource
        $sdkReport = Get-ForgeBootstrapReport $Root $SelectedSource -SkipAuthentication -Clock $watch -MaximumSeconds $Seconds -OwnedLock $lock -RequiredTools $installTools
        $dotnet = $sdkReport.DotNetHost
        if (-not $dotnet) {
            $installer = Join-Path $run 'dotnet-install.ps1'
            Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -TimeoutSec ([int][Math]::Max(1, [Math]::Min(60, $Seconds - $watch.Elapsed.TotalSeconds)))
            $sdkRoot = Join-Path $Root "sdk/$sdkVersion"
            $null = Run-Step 'install-sdk' $pwsh @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-Version', $sdkVersion, '-InstallDir', $sdkRoot, '-NoPath') $run
            $dotnet = Join-Path $sdkRoot 'dotnet.exe'
        }
        $sdkEnvironment = @{ DOTNET_ROOT = (Split-Path -Parent $dotnet); DOTNET_PROCESSOR_COUNT = '2'; DOTNET_GCHeapHardLimit = '0x100000000'; DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'; MSBUILDDISABLENODEREUSE = '1'; DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'; DOTNET_NOLOGO = '1'; DOTNET_CLI_HOME = (Join-Path $run 'dotnet-home') }
        $installation = $existing
        if (-not $existing -or $existing.SourceCommit -ne $commit) {
            $feed = Join-Path $run 'feed'; $null = New-Item -ItemType Directory -Path $feed
            $publicConfig = Join-Path $run 'NuGet.Public.Config'
            [IO.File]::WriteAllText($publicConfig, '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
            $null = Run-Step 'restore' $dotnet @('restore', $project, '--locked-mode', '--configfile', $publicConfig, '--disable-parallel') $SelectedSource $sdkEnvironment
            $gate = Join-Path $SelectedSource 'components/tool/DeliveryForge.LinuxScopeBirthGate/DeliveryForge.LinuxScopeBirthGate.csproj'
            $buildArgs = @('-c', 'Release', '--no-restore', '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false')
            $null = Run-Step 'build-gate' $dotnet (@('build', $gate) + $buildArgs) $SelectedSource $sdkEnvironment
            $null = Run-Step 'build-tool' $dotnet (@('build', $project) + $buildArgs) $SelectedSource $sdkEnvironment
            $null = Run-Step 'pack' $dotnet @('pack', $project, '-c', 'Release', '--no-build', '--no-restore', '--output', $feed) $SelectedSource $sdkEnvironment
            $archives = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File)
            if ($archives.Count -ne 1) { throw 'Expected one complete tool package.' }
            [xml]$projectXml = Read-ForgeUtf8Text $project
            $version = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
            $prefix = Join-Path $Root ('installations/' + $commit.Substring(0, 12) + '-' + [guid]::NewGuid().ToString('N'))
            $localConfig = Join-Path $run 'NuGet.Local.Config'
            $escapedFeed = [Security.SecurityElement]::Escape($feed)
            [IO.File]::WriteAllText($localConfig, "<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/></packageSources></configuration>")
            $null = Run-Step 'install-tool' $dotnet @('tool', 'install', 'DeliveryForge.Tool', '--version', $version, '--tool-path', $prefix, '--configfile', $localConfig) $SelectedSource $sdkEnvironment
            $assemblies = @(Get-ChildItem -LiteralPath $prefix -Filter 'DeliveryForge.Tool.dll' -Recurse -File -Force)
            if ($assemblies.Count -ne 1) { throw 'Installed tool assembly is missing or ambiguous.' }
            foreach ($flag in @('--help', '--version')) { $null = Run-Step ('verify-' + $flag.TrimStart('-')) $dotnet @($assemblies[0].FullName, $flag) $run $sdkEnvironment }
            $payload = @(Get-ChildItem -LiteralPath $prefix -Recurse -File -Force)
            if ($payload.Count -gt 4096) { throw 'Installed tool payload exceeds the finite file count.' }
            $pins = @($payload | ForEach-Object { @{ Path = $_.FullName; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
            $installation = @{ Kind = 'DeliveryForgeBootstrapInstallation-v1'; PackagePath = $archives[0].FullName; SourceRoot = $SelectedSource; SourceCommit = $commit; RequiredSdkVersion = $sdkVersion; DotNetHost = $dotnet; DotNetSha256 = (Get-FileHash -LiteralPath $dotnet -Algorithm SHA256).Hash.ToLowerInvariant(); ToolAssembly = $assemblies[0].FullName; ToolSha256 = (Get-FileHash -LiteralPath $assemblies[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant(); PackageSha256 = (Get-FileHash -LiteralPath $archives[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant(); Files = $pins; RuntimeCertified = $false }
        }
        $sourceAfter = Run-Step 'verify-source-head' $git @('-C', $SelectedSource, 'rev-parse', '--verify', 'HEAD') $run
        $sourceStatusAfter = Run-Step 'verify-source-status' $git @('-C', $SelectedSource, 'status', '--porcelain') $run
        if ($sourceAfter.Stdout.Trim() -ne $commit -or $sourceStatusAfter.Stdout.Trim()) { throw 'Source moved or changed during package preparation; no activation.' }
        # Stage complete skill payloads outside agent discovery before moving any
        # selected directory. All conflict/cross-volume checks preceded build.
        $skillCandidates = @{}
        foreach ($agent in @('codex', 'claude')) {
            $candidate = Join-Path $run ('skills/' + $agent)
            Install-ForgeSkill $SelectedSource $candidate $launcher $commit
            $skillCandidates[$agent] = $candidate
        }
        if (-not $existing -or $existing.SourceCommit -ne $commit) {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [IO.Compression.ZipFile]::OpenRead($archives[0].FullName)
            try {
                $entry = $zip.GetEntry('tools/net10.0/any/content/scripts/delivery-forge.ps1.txt')
                if (-not $entry -or $entry.Length -gt 1MB) { throw 'Selected package lacks its finite resource-aware launcher.' }
                $input = $entry.Open(); $memory = New-Object IO.MemoryStream
                try { $input.CopyTo($memory); $launcherBytes = $memory.ToArray() } finally { $input.Dispose(); $memory.Dispose() }
            } finally { $zip.Dispose() }
            $sourceLauncher = Join-Path $SelectedSource 'scripts/delivery-forge.ps1'
            if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($sourceLauncher)) -ne [Convert]::ToBase64String($launcherBytes)) { throw 'Package launcher differs from the selected source; no activation.' }
            if (([Text.Encoding]::UTF8.GetString($launcherBytes)) -notmatch 'MaximumSeconds') { throw 'Pinned source launcher lacks remaining-interval support; explicitly select a compatible source.' }
            $nativeLauncher = Join-Path $Root ('launchers/' + $installation.PackageSha256 + '/delivery-forge.ps1')
            Write-ForgeImmutableBytes $nativeLauncher $launcherBytes
            $installation.NativeLauncher = $nativeLauncher; $installation.NativeLauncherBytes = $launcherBytes.Length
            $installation.NativeLauncherSha256 = (Get-FileHash -LiteralPath $nativeLauncher -Algorithm SHA256).Hash.ToLowerInvariant()
            $installation.NativeStartReceiptSupported = Test-ForgeNativeStartReceiptParameter $nativeLauncher
            $installation.NativeCompletionReceiptSupported = $installation.NativeStartReceiptSupported -and (Test-ForgeNativeStartReceiptParameter $nativeLauncher 'NativeCompletionReceipt')
        }
        $bootstrapBytes = [IO.File]::ReadAllBytes($PSCommandPath)
        $bootstrapHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $installedBootstrap = Join-Path $Root ('bootstrap/implementations/' + $bootstrapHash + '/bootstrap-delivery-forge.ps1')
        Write-ForgeImmutableBytes $installedBootstrap $bootstrapBytes
        if ($installation -isnot [Collections.IDictionary]) {
            $copy = @{}; foreach ($property in $installation.PSObject.Properties) { $copy[$property.Name] = $property.Value }; $installation = $copy
        }
        $selectedBootstrap = Join-Path $SelectedSource 'scripts/bootstrap-delivery-forge.ps1'
        $installation.SelectedSourceBootstrapSha256 = if (Test-Path -LiteralPath $selectedBootstrap -PathType Leaf) { $null = Assert-ForgePlainPath $selectedBootstrap; (Get-FileHash -LiteralPath $selectedBootstrap -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        $installation.BootstrapMatchesSelectedSource = $null -ne $installation.SelectedSourceBootstrapSha256 -and $installation.SelectedSourceBootstrapSha256 -eq $bootstrapHash
        $installation.SourceRoot = $SelectedSource
        $installation.BootstrapScript = $installedBootstrap; $installation.BootstrapScriptSha256 = $bootstrapHash
        $installation.Files = @($installation.Files | Where-Object { $_.Path -notin @($launcher, $installedBootstrap, $installation.NativeLauncher) }) + @(
            @{ Path = $installedBootstrap; Sha256 = $bootstrapHash },
            @{ Path = $installation.NativeLauncher; Sha256 = $installation.NativeLauncherSha256 },
            @{ Path = $launcher; Sha256 = $facadeHash }
        )
        if ($installation.Files.Count -gt 4096) { throw 'Complete installation manifest exceeds its finite file bound.' }
        $consumedHead = Run-Step 'verify-consumed-source-head' $git @('-C', $SelectedSource, 'rev-parse', '--verify', 'HEAD') $run
        $consumedStatus = Run-Step 'verify-consumed-source-status' $git @('-C', $SelectedSource, 'status', '--porcelain') $run
        if ($consumedHead.Stdout.Trim() -ne $commit -or $consumedStatus.Stdout.Trim()) { throw 'Source changed while payload bytes were consumed; no activation.' }
        foreach ($destination in $destinations.Values) {
            if ((Get-ForgeSkillStatus $destination) -eq 'Conflict') { throw 'A skill changed before activation; existing selection preserved.' }
        }
        $pending = Join-Path $run 'installation.READY.json'; Write-ForgeNewJson $pending $installation
        $pendingHash = (Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash.ToLowerInvariant()
        if ((Get-ForgeRemainingSeconds $watch $Seconds 5) -lt 1) { throw 'Original deadline expired before activation; previous selection preserved.' }
        # Freeze all destination/backup mappings before the first move. This
        # immutable plan records intended preimages, never claims applied state.
        $backupRoot = Join-Path $Root 'bootstrap/skill-backups'; $null = Assert-ForgePlainPath $backupRoot -Directory
        $null = New-Item -ItemType Directory -Path $backupRoot -Force
        foreach ($agent in @('codex', 'claude')) {
            $destination = $destinations[$agent]; $candidate = $skillCandidates[$agent]
            $newMarker = (Get-FileHash -LiteralPath (Join-Path $candidate '.forge-bootstrap-skill.json') -Algorithm SHA256).Hash.ToLowerInvariant()
            $backup = Join-Path $backupRoot ([guid]::NewGuid().ToString('N')); Assert-ForgeWithin $backupRoot $backup
            $item = [pscustomobject]@{ Agent = $agent; Destination = $destination; Candidate = $candidate; Backup = $backup; OldExists = [bool](Test-Path -LiteralPath $destination); OldMarker = $null; OldMissing = ''; NewMarker = $newMarker; OldMoved = $false; NewMoved = $false }
            if ($item.OldExists) {
                $marker = Join-Path $destination '.forge-bootstrap-skill.json'
                $item.OldMarker = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash.ToLowerInvariant()
                $oldSkill = Read-ForgeUtf8Text $marker | ConvertFrom-Json
                $item.OldMissing = @($oldSkill.Files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $destination $_.RelativePath) -PathType Leaf) } | ForEach-Object RelativePath | Sort-Object) -join [char]0
            }
            $activations.Add($item)
        }
        Assert-ForgeFilePreimage $launcher $facadeOldHash
        Assert-ForgeFilePreimage $pointer $pointerOldHash
        $activationPlanPath = Join-Path $run 'activation-plan.json'
        $plannedSkills = @($activations | ForEach-Object { @{ Agent = $_.Agent; Destination = $_.Destination; Candidate = $_.Candidate; Backup = $_.Backup; OldExists = $_.OldExists; OldMarker = $_.OldMarker; OldMissing = $_.OldMissing; NewMarker = $_.NewMarker } })
        Write-ForgeNewJson $activationPlanPath @{ Kind = 'DeliveryForgeBootstrapActivationPlan-v1'; SourceCommit = $commit; Skills = $plannedSkills; Facade = @{ Path = $launcher; Candidate = $facadeCandidate; Backup = $facadeBackup; OldSha256 = $facadeOldHash; NewSha256 = $facadeHash }; Pointer = @{ Path = $pointer; Candidate = $pending; Backup = $pointerBackup; OldSha256 = $pointerOldHash; NewSha256 = $pendingHash } }
        $lockBytes = (New-Object Text.UTF8Encoding $false).GetBytes((@{ Kind = 'DeliveryForgeBootstrapActivation-v1'; AttemptDirectory = $run; ActivationPlan = $activationPlanPath } | ConvertTo-Json -Compress))
        $lock.Write($lockBytes, 0, $lockBytes.Length); $lock.Flush($true)
        foreach ($item in $activations) {
            $destination = $item.Destination; $candidate = $item.Candidate; $newMarker = $item.NewMarker
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
            if ($item.OldExists) {
                if (-not (Test-ForgeSkillRestoreState $destination $item.OldMarker $item.OldMissing)) { throw 'Skill preimage changed before activation; preserve the selected directory.' }
                try { Move-ForgeInstallationDirectory $destination $item.Backup; $item.OldMoved = $true }
                catch {
                    $unchanged = $false
                    try { $unchanged = -not (Test-Path -LiteralPath $item.Backup) -and (Test-ForgeSkillRestoreState $destination $item.OldMarker $item.OldMissing) } catch { }
                    if (-not $unchanged) { $script:ForgeBootstrapUnsettled = $true }
                    throw
                }
            } elseif (Test-Path -LiteralPath $destination) { throw 'Skill destination appeared before activation; preserve it.' }
            try { Move-ForgeInstallationDirectory $candidate $destination; $item.NewMoved = $true }
            catch {
                $unchanged = $false
                try { $unchanged = -not (Test-Path -LiteralPath $destination) -and (Test-ForgeSkillRestoreState $candidate $newMarker '') } catch { }
                if (-not $unchanged) { $script:ForgeBootstrapUnsettled = $true }
                throw
            }
        }
        $null = New-Item -ItemType Directory -Path $bin -Force
        Assert-ForgeFilePreimage $launcher $facadeOldHash
        try {
            if ($facadeExisted) { [IO.File]::Replace($facadeCandidate, $launcher, $facadeBackup) }
            else { [IO.File]::Move($facadeCandidate, $launcher) }
            $facadeActivated = $true
        } catch {
            $unchanged = $false
            try {
                $oldStillSelected = if ($facadeExisted) { (Test-Path -LiteralPath $launcher) -and (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash.ToLowerInvariant() -eq $facadeOldHash } else { -not (Test-Path -LiteralPath $launcher) }
                $unchanged = $oldStillSelected -and -not (Test-Path -LiteralPath $facadeBackup) -and (Test-Path -LiteralPath $facadeCandidate) -and (Get-FileHash -LiteralPath $facadeCandidate -Algorithm SHA256).Hash.ToLowerInvariant() -eq $facadeHash
            } catch { }
            if (-not $unchanged) { $script:ForgeBootstrapUnsettled = $true }
            throw
        }
        if ((Get-ForgeRemainingSeconds $watch $Seconds 5) -lt 1) { throw 'Original deadline expired before pointer activation.' }
        Assert-ForgeFilePreimage $pointer $pointerOldHash
        try {
            if (Test-Path -LiteralPath $pointer) { [IO.File]::Replace($pending, $pointer, $pointerBackup) }
            else { [IO.File]::Move($pending, $pointer) }
            $pointerPublished = $true
        } catch {
            # A failed publication is known unentered only if the complete old
            # pointer and new candidate bytes are both still exactly present.
            $unchanged = $false
            try {
                $oldStillSelected = if ($pointerOldHash) { (Test-Path -LiteralPath $pointer) -and (Get-FileHash -LiteralPath $pointer -Algorithm SHA256).Hash.ToLowerInvariant() -eq $pointerOldHash } else { -not (Test-Path -LiteralPath $pointer) }
                $unchanged = $oldStillSelected -and -not (Test-Path -LiteralPath $pointerBackup) -and (Test-Path -LiteralPath $pending) -and (Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash.ToLowerInvariant() -eq $pendingHash
            } catch { }
            if (-not $unchanged) { $script:ForgeBootstrapUnsettled = $true }
            throw
        }
        $report = Get-ForgeBootstrapReport $Root $SelectedSource -RequiredAuthentication $RequiredAuthentication -Clock $watch -MaximumSeconds $Seconds -OwnedLock $lock -RequiredTools $installTools
        $checks = [Collections.Generic.List[object]]::new()
        foreach ($row in $report.Checks) { if ($row.Id -ne 'bootstrap-lock') { $checks.Add($row) } }
        $report = New-ForgeReport $Root $checks $sdkVersion $RequiredAuthentication
        $report | Add-Member NoteProperty RequiredTools @($installTools)
        $report | Add-Member NoteProperty BootstrapMatchesSelectedSource $installation.BootstrapMatchesSelectedSource
        $report.AuthenticationChecked = $RequiredAuthentication.Count -gt 0
        if (-not $report.AuthenticationChecked) { $report.AuthenticationReady = $null }
        $report | Add-Member NoteProperty ActivationPlan $activationPlanPath
        Write-ForgeNewJson (Join-Path $run 'report.json') $report
        $report | Add-Member NoteProperty AttemptDirectory $run
        $report
    } catch {
        $failure = $_.Exception.Message
        # Restore only known successful directory moves, only into an absent
        # original target, and only while every exact marker/file state matches.
        if (-not $pointerPublished -and -not $script:ForgeBootstrapUnsettled) {
            if ($facadeActivated) {
                try {
                    $null = Assert-ForgePlainPath $launcher
                    if ((Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash.ToLowerInvariant() -ne $facadeHash) { throw 'Facade changed before restoration.' }
                    if ($facadeExisted) {
                        $null = Assert-ForgePlainPath $facadeBackup
                        if ((Get-FileHash -LiteralPath $facadeBackup -Algorithm SHA256).Hash.ToLowerInvariant() -ne $facadeOldHash) { throw 'Prior facade differs.' }
                        $preservedCandidate = Join-Path $run 'delivery-forge.facade.failed-candidate.ps1'
                        $null = Assert-ForgePlainPath $preservedCandidate
                        if (Test-Path -LiteralPath $preservedCandidate) { throw 'Facade preservation target exists.' }
                        [IO.File]::Replace($facadeBackup, $launcher, $preservedCandidate)
                    } else {
                        $null = Assert-ForgePlainPath $facadeCandidate
                        if (Test-Path -LiteralPath $facadeCandidate) { throw 'Facade candidate restore target exists.' }
                        [IO.File]::Move($launcher, $facadeCandidate)
                    }
                } catch { $script:ForgeBootstrapUnsettled = $true }
            }
            for ($index = $activations.Count - 1; $index -ge 0 -and -not $script:ForgeBootstrapUnsettled; $index--) {
                $item = $activations[$index]
                try {
                    if ($item.NewMoved) {
                        if (-not (Test-ForgeSkillRestoreState $item.Destination $item.NewMarker '') -or (Test-Path -LiteralPath $item.Candidate)) { throw 'Candidate restore state differs.' }
                        Move-ForgeInstallationDirectory $item.Destination $item.Candidate
                    }
                    if ($item.OldMoved) {
                        if ((Test-Path -LiteralPath $item.Destination) -or -not (Test-ForgeSkillRestoreState $item.Backup $item.OldMarker $item.OldMissing)) { throw 'Old skill restore state differs.' }
                        Move-ForgeInstallationDirectory $item.Backup $item.Destination
                    }
                } catch { $script:ForgeBootstrapUnsettled = $true; break }
            }
        }
        try {
            if ($script:ForgeBootstrapUnsettled -or (Get-ForgeRemainingSeconds $watch $Seconds 5) -lt 1) { throw 'No further native diagnostics after UNKNOWN/deadline.' }
            $report = Get-ForgeBootstrapReport $Root $SelectedSource -RequiredAuthentication $RequiredAuthentication -Clock $watch -MaximumSeconds $Seconds -OwnedLock $lock -RequiredTools $installTools
        }
        catch { $report = [pscustomobject]@{ Kind = 'DeliveryForgeBootstrapReport-v1'; StateRoot = $Root; Checks = @(); AutomatableActions = @(); UserActions = @(); InstallationReady = $false; AuthenticationReady = $false; ExecutionCertified = $false; ExitCode = 1 } }
        $report.ExitCode = $(if ($script:ForgeBootstrapUnsettled) { 125 } else { 1 })
        $report | Add-Member NoteProperty ActivationPlan $activationPlanPath
        $report | Add-Member NoteProperty Failure $failure
        $report | Add-Member NoteProperty AttemptDirectory $run
        Write-ForgeNewJson (Join-Path $run 'failure-report.json') $report
        $report
    } finally {
        if ($lock) { $lock.Dispose(); if (-not $script:ForgeBootstrapUnsettled) { [IO.File]::Delete((Join-Path $Root 'bootstrap.lock')) } }
    }
}

function Write-ForgeBootstrapReport($Report, [switch]$AsJson) {
    if ($AsJson) { $Report | ConvertTo-Json -Depth 30; return }
    $Report.Checks | Select-Object Id, Status, Detail | Format-Table -Wrap -AutoSize | Out-Host
    foreach ($row in @($Report.AutomatableActions) + @($Report.UserActions)) {
        Write-Host ("`n{0}: {1}" -f $row.Id, $row.Status)
        foreach ($step in $row.Steps) { Write-Host ('  ' + $step) }
    }
    if ($Report.PSObject.Properties.Name -contains 'Failure') { Write-Host ("`nInstallation stopped: " + $Report.Failure) }
    Write-Host ("`nInstallation ready: {0}; sign-ins ready: {1}; execution certification: not evaluated." -f $Report.InstallationReady, $Report.AuthenticationReady)
    if ($Report.PSObject.Properties.Name -contains 'BootstrapMatchesSelectedSource' -and $Report.BootstrapMatchesSelectedSource -eq $false) { Write-Host 'Bootstrap and selected CLI source bytes differ; their separate hashes are recorded. No source equivalence is implied.' }
    Write-Host ('Launcher: ' + (Join-Path $Report.StateRoot 'bin/delivery-forge.ps1'))
    if ($Report.PSObject.Properties.Name -contains 'AttemptDirectory') { Write-Host ('Evidence: ' + $Report.AttemptDirectory) }
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    try {
        if (-not $StateRoot) { $StateRoot = Get-ForgeDefaultStateRoot }
        $StateRoot = Assert-ForgePlainPath $StateRoot -Directory
        $clock = [Diagnostics.Stopwatch]::StartNew()
        if ($SourceRoot -and -not $PSBoundParameters.ContainsKey('Ref')) { $Ref = 'HEAD' }
        $report = if ($Mode -eq 'Install') { Invoke-ForgeBootstrapInstall $StateRoot $SourceRoot $Ref $MaximumSeconds -Clock $clock -SourceSelectionExplicit:($PSBoundParameters.ContainsKey('Ref') -or $PSBoundParameters.ContainsKey('SourceRoot')) } else { Get-ForgeBootstrapReport $StateRoot $SourceRoot -Clock $clock -MaximumSeconds $MaximumSeconds }
        Write-ForgeBootstrapReport $report -AsJson:$Json
        exit $report.ExitCode
    } catch {
        $failureCode = $(if ($script:ForgeBootstrapUnsettled) { 125 } else { 1 })
        if ($script:ForgeBootstrapUnsettled) { try { Save-ForgeUnknown $StateRoot } catch { Write-ForgeDiagnostic 'FORGE_BOOTSTRAP_UNKNOWN_LOCK_UPDATE_FAILED: exit125 retained; preserve the observed PID/start evidence and do not retry.' } }
        if ($Json) { @{ Kind = 'DeliveryForgeBootstrapReport-v1'; ExitCode = $failureCode; Failure = $_.Exception.Message; ExecutionCertified = $false } | ConvertTo-Json }
        else { [Console]::Error.WriteLine($_.Exception.Message) }
        exit $failureCode
    }
}
