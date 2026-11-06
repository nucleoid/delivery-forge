#requires -Version 7.0
[CmdletBinding()]
param([string]$TestRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) ('.handoff/bootstrap-tests/' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'bootstrap-delivery-forge.ps1')
$null = New-Item -ItemType Directory -Path $TestRoot -Force
$results = [Collections.Generic.List[object]]::new()
$originals = @{}
foreach ($name in @('Find-ForgeCommand', 'Invoke-ForgeProcess', 'Read-ForgeInstallation', 'Get-ForgeBootstrapReport', 'Invoke-ForgeBootstrapInstall', 'Write-ForgeBootstrapReport', 'Get-ForgeSkillStatus', 'Get-ForgeSkillDestination', 'Save-ForgeUnknown')) { $originals[$name] = (Get-Item ('Function:' + $name)).ScriptBlock }
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Mock([string]$Name, [scriptblock]$Body) { Set-Item ('Function:script:' + $Name) $Body }
function Test([string]$Name, [scriptblock]$Body) {
    $wasUnsettled = $script:ForgeBootstrapUnsettled
    try { & $Body; $results.Add([pscustomobject]@{ Name = $Name; Passed = $true }) }
    catch { $results.Add([pscustomobject]@{ Name = $Name; Passed = $false; Error = $_.Exception.Message }) }
    finally { $script:ForgeBootstrapUnsettled = $wasUnsettled; foreach ($key in $originals.Keys) { Set-Item ('Function:script:' + $key) $originals[$key] } }
}
$savedProfile = $env:USERPROFILE; $savedLocal = $env:LOCALAPPDATA
$savedOverrides = @{}
foreach ($name in @('OPENAI_API_KEY', 'CODEX_API_KEY', 'OPENAI_BASE_URL', 'ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_BASE_URL', 'CLAUDE_CODE_SIMPLE', 'CLAUDE_CODE_USE_BEDROCK', 'CLAUDE_CODE_USE_VERTEX', 'CLAUDE_CODE_USE_FOUNDRY')) { $savedOverrides[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, $null) }
try {
    $env:USERPROFILE = Join-Path $TestRoot 'user'; $env:LOCALAPPDATA = Join-Path $env:USERPROFILE 'AppData/Local'
    $source = Join-Path $TestRoot 'source'; $null = New-Item -ItemType Directory -Path $source -Force
    [IO.File]::WriteAllText((Join-Path $source 'global.json'), '{"sdk":{"version":"10.0.401","rollForward":"disable"}}')
    Test 'Default state does not follow disposable agent AppContainer redirect' {
        $before = $env:LOCALAPPDATA
        try { $env:LOCALAPPDATA = Join-Path $env:USERPROFILE 'AppData/Local/Packages/sandbox.test/AC'; Assert ((Get-ForgeDefaultStateRoot) -eq (Join-Path $env:USERPROFILE 'AppData/Local/DeliveryForge')) 'Default escaped into disposable sandbox.' }
        finally { $env:LOCALAPPDATA = $before }
    }
    Test 'Actual command discovery searches each supported filename separately' {
        $lookup=Join-Path $TestRoot 'actual-native-path';$null=New-Item -ItemType Directory -Path $lookup
        $expected=Join-Path $lookup 'codex.exe';[IO.File]::WriteAllText($expected,'INERT lookup fixture; never executed.')
        $priorPath=$env:Path
        try{
            $env:Path=$lookup+';'+$priorPath
            Assert ((Find-ForgeCommand 'codex') -eq $expected) 'Actual PATH lookup did not select the existing native executable.'
        }finally{$env:Path=$priorPath}
    }
    Test 'Missing prerequisite report includes every tool and exact remediation without creating state' {
        Mock Find-ForgeCommand { param($Name) $null }
        Mock Invoke-ForgeProcess { throw 'A missing executable must never be invoked.' }
        $root = Join-Path $TestRoot 'missing-state'
        $report = Get-ForgeBootstrapReport $root $source
        foreach ($id in @('git', 'gh', 'codex', 'claude', 'pwsh', 'dotnet-sdk', 'forge-cli', 'codex-skill', 'claude-skill')) {
            $row = $report.Checks | Where-Object Id -eq $id
            Assert ($row.Status -eq 'Missing' -and $row.Automatic -and $row.Steps.Count -gt 0) ('Missing/action list incomplete for ' + $id)
        }
        foreach ($id in @('gh-authentication', 'codex-authentication', 'claude-authentication', 'winget')) { Assert ((@($report.UserActions | Where-Object Id -eq $id)).Count -eq 1) ('User action missing for ' + $id) }
        Assert (-not (Test-Path -LiteralPath $root)) 'Check created installation state.'
        Assert ($report.ExitCode -eq 2 -and -not $report.ExecutionCertified) 'Missing inputs were reported ready.'
    }
    Test 'Existing but denied/broken executables are not classified as auto-installable missing tools' {
        Mock Find-ForgeCommand { param($Name) Join-Path $TestRoot ($Name + '.exe') }
        Mock Invoke-ForgeProcess { throw 'Execution denied by sandbox.' }
        $report = Get-ForgeBootstrapReport (Join-Path $TestRoot 'denied-state') $source
        foreach ($id in @('git', 'gh', 'codex', 'claude', 'pwsh')) { $row = $report.Checks | Where-Object Id -eq $id; Assert ($row.Status -eq 'Unverified' -and -not $row.Automatic) ('Unsafe reinstall proposed for ' + $id) }
    }
    Test 'Node is an automatic prerequisite only for an existing npm Codex shim' {
        Mock Find-ForgeCommand { param($Name) if ($Name -eq 'codex') { Join-Path $TestRoot 'codex.ps1' } elseif ($Name -ne 'node') { Join-Path $TestRoot ($Name + '.exe') } }
        Mock Invoke-ForgeProcess { [pscustomobject]@{ ExitCode = 0; Stdout = 'v1'; Stderr = '' } }
        $report = Get-ForgeBootstrapReport (Join-Path $TestRoot 'npm-state') $source -SkipAuthentication
        $node = $report.Checks | Where-Object Id -eq 'node'
        Assert ($node.Status -eq 'Missing' -and $node.Automatic -and ($node.Steps -join ' ') -match 'OpenJS.NodeJS.LTS') 'Missing npm runtime was not repairable.'
    }
    Test 'Authentication probes never invoke login or retain account output' {
        $script:ObservedCommands = [Collections.Generic.List[string]]::new()
        Mock Find-ForgeCommand { param($Name) Join-Path $TestRoot ($Name + '.exe') }
        Mock Invoke-ForgeProcess {
            param($Executable, $Arguments)
            $script:ObservedCommands.Add(([IO.Path]::GetFileNameWithoutExtension($Executable) + ' ' + ($Arguments -join ' ')))
            $out = 'v1'
            if ($Arguments[0] -eq 'login') { $out = 'Logged in using ChatGPT private@example.invalid' }
            elseif ($Arguments[0] -eq 'auth' -and $Executable -like '*claude.exe') { $out = '{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"private@example.invalid"}' }
            elseif ($Arguments[0] -eq 'auth') { $out = 'private@example.invalid' }
            [pscustomobject]@{ ExitCode = 0; Stdout = $out; Stderr = '' }
        }
        $report = Get-ForgeBootstrapReport (Join-Path $TestRoot 'auth-state') $source
        Assert $report.AuthenticationReady 'Valid subscription status was not recognized.'
        Assert (-not (($report | ConvertTo-Json -Depth 30) -match 'private@example')) 'Account output leaked into report.'
        Assert (-not (($script:ObservedCommands -join "`n") -match 'auth login|login --')) 'Bootstrap attempted authentication.'
        Assert (($script:ObservedCommands -join "`n") -match 'gh auth status .*--no-refresh') 'gh status may refresh authentication.'
    }
    Test 'API/third-party sign-ins do not satisfy subscription readiness' {
        Mock Find-ForgeCommand { param($Name) Join-Path $TestRoot ($Name + '.exe') }
        Mock Invoke-ForgeProcess {
            param($Executable, $Arguments)
            $out = if ($Arguments[0] -eq 'login') { 'Logged in using an API key' } elseif ($Executable -like '*claude.exe' -and $Arguments[0] -eq 'auth') { '{"loggedIn":true,"authMethod":"api_key"}' } else { 'v1' }
            [pscustomobject]@{ ExitCode = 0; Stdout = $out; Stderr = '' }
        }
        $report = Get-ForgeBootstrapReport (Join-Path $TestRoot 'api-state') $source
        Assert (-not $report.AuthenticationReady) 'API billing was accepted as subscription login.'
        foreach ($id in @('codex-authentication', 'claude-authentication')) { Assert (($report.Checks | Where-Object Id -eq $id).Status -eq 'UserAction') ('Wrong route accepted for ' + $id) }
    }
    Test 'Local planning keeps worker sign-in gaps visible without blocking the command' {
        $sdk=Join-Path $TestRoot 'dotnet.exe';[IO.File]::WriteAllText($sdk,'INERT SDK FIXTURE; all probes mocked.')
        Mock Find-ForgeCommand {param($Name) Join-Path $TestRoot ($Name+'.exe')}
        Mock Read-ForgeInstallation {[pscustomobject]@{RequiredSdkVersion='10.0.401';DotNetHost=(Join-Path $TestRoot 'dotnet.exe');ToolAssembly='fixture.dll'}}
        Mock Get-ForgeSkillStatus {'Ready'}
        Mock Invoke-ForgeProcess {
            param($Executable,$Arguments)
            if($Arguments[0] -eq '--list-sdks'){$out='10.0.401 [fixture SDK]';$code=0}
            elseif($Arguments[0] -eq 'fixture.dll'){$out='{"tool":"delivery-forge","version":"0.1.0"}';$code=0}
            elseif($Arguments[0] -eq 'login' -or ($Arguments[0] -eq 'auth' -and $Executable -like '*claude.exe')){$out='';$code=1}
            else{$out='v1';$code=0}
            [pscustomobject]@{ExitCode=$code;Stdout=$out;Stderr=''}
        }
        $full=Get-ForgeBootstrapReport (Join-Path $TestRoot 'auth-scope') $source
        Assert ($full.ExitCode -eq 2) 'Full prerequisite check hid missing worker sign-ins.'
        $local=Get-ForgeBootstrapReport (Join-Path $TestRoot 'auth-scope') $source -RequiredAuthentication @()
        Assert ($local.ExitCode -eq 0) 'Local planning was blocked on unused worker subscriptions.'
        foreach($id in @('codex-authentication','claude-authentication')){Assert (($local.Checks|Where-Object Id -eq $id).Status -eq 'NotNeeded') 'Unneeded authentication was probed or falsely reported verified.'}
    }
    Test 'Claude subscription label on a third-party provider is not first-party readiness' {
        Mock Find-ForgeCommand {param($Name) Join-Path $TestRoot ($Name+'.exe')}
        Mock Invoke-ForgeProcess {
            param($Executable,$Arguments)
            $out=if($Arguments[0] -eq 'login'){'Logged in using ChatGPT'}elseif($Executable -like '*claude.exe' -and $Arguments[0] -eq 'auth'){'{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"bedrock"}'}else{'v1'}
            [pscustomobject]@{ExitCode=0;Stdout=$out;Stderr=''}
        }
        $report=Get-ForgeBootstrapReport (Join-Path $TestRoot 'provider-route') $source
        Assert (($report.Checks|Where-Object Id -eq 'claude-authentication').Status -eq 'UserAction') 'Third-party route was accepted as first-party subscription readiness.'
    }
    Test 'Skipped authentication is explicitly unobserved, not verified' {
        Mock Find-ForgeCommand { param($Name) $null }
        $report = Get-ForgeBootstrapReport (Join-Path $TestRoot 'skip-auth-state') $source -SkipAuthentication
        Assert (-not $report.AuthenticationChecked -and $null -eq $report.AuthenticationReady) 'Skipped authentication claimed ready.'
    }
    Test 'Provider environment reports names only and remains unchanged' {
        $env:ANTHROPIC_API_KEY = 'secret-test-do-not-emit'
        try {
            Mock Find-ForgeCommand { param($Name) $null }
            $report = Get-ForgeBootstrapReport (Join-Path $TestRoot 'provider-state') $source
            $json = $report | ConvertTo-Json -Depth 30
            Assert ($json -match 'ANTHROPIC_API_KEY' -and $json -notmatch 'secret-test') 'Provider value leaked or missing prerequisite.'
            Assert ($env:ANTHROPIC_API_KEY -eq 'secret-test-do-not-emit') 'Bootstrap changed provider environment.'
        } finally { $env:ANTHROPIC_API_KEY = $null }
    }
    Test 'Path policy rejects relative, drive-relative, UNC and escaping destinations' {
        foreach ($path in @('relative', 'C:relative', '\rooted', '\\server\share', 'C:\')) { $threw = $false; try { $null = Assert-ForgePlainPath $path -Directory } catch { $threw = $true }; Assert $threw ('Path accepted: ' + $path) }
        $threw = $false; try { Assert-ForgeWithin (Join-Path $TestRoot 'a') (Join-Path $TestRoot 'ab/file') } catch { $threw = $true }; Assert $threw 'Prefix collision escaped parent.'
    }
    Test 'Skill installation is idempotent and detects later edits without overwriting them' {
        $payload = Join-Path $TestRoot 'skill-source'; $null = New-Item -ItemType Directory -Path (Join-Path $payload 'references'), (Join-Path $payload 'docs'), (Join-Path $payload 'scripts') -Force
        [IO.File]::WriteAllText((Join-Path $payload 'SKILL.md'), "---`nname: delivery-forge`ndescription: Test fixture only`n---`nTest fixture.")
        [IO.File]::WriteAllText((Join-Path $payload 'references/read.md'), 'Reference fixture.')
        [IO.File]::WriteAllText((Join-Path $payload 'docs/install.md'), 'Document fixture.')
        $destination = Join-Path $TestRoot 'skills/one'
        Install-ForgeSkill $payload $destination (Join-Path $TestRoot 'bin/delivery-forge.ps1') ('a' * 40)
        Assert (Test-ForgeSkill $destination) 'Installed skill failed readback.'
        $path = Join-Path $destination 'SKILL.md'; $before = [IO.File]::ReadAllBytes($path)
        Install-ForgeSkill $payload $destination (Join-Path $TestRoot 'bin/delivery-forge.ps1') ('a' * 40)
        Assert ([Convert]::ToBase64String($before) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))) 'Idempotent install changed skill.'
        [IO.File]::AppendAllText($path, 'owner customization')
        Assert (-not (Test-ForgeSkill $destination)) 'Modified skill was accepted unchanged.'
        $threw = $false; try { Install-ForgeSkill $payload $destination 'ignored' ('a' * 40) } catch { $threw = $true }
        Assert ($threw -and ([IO.File]::ReadAllText($path)).EndsWith('owner customization')) 'Customized skill was overwritten.'
    }
    Test 'Unmanaged existing skill is refused and kept byte-identical' {
        $destination = Join-Path $TestRoot 'skills/unmanaged'; $null = New-Item -ItemType Directory -Path $destination -Force
        $path = Join-Path $destination 'SKILL.md'; [IO.File]::WriteAllText($path, 'owner skill')
        $threw = $false; try { Install-ForgeSkill $source $destination 'ignored' ('a' * 40) } catch { $threw = $true }
        Assert ($threw -and [IO.File]::ReadAllText($path) -eq 'owner skill') 'Unmanaged installation changed.'
    }
    Test 'Missing managed skill file can be repaired with the previous directory retained' {
        $payload = Join-Path $TestRoot 'skill-source'
        $destination = Join-Path $TestRoot 'repair/skills/delivery-forge'
        $launcher = Join-Path $TestRoot 'repair/bin/delivery-forge.ps1'
        Install-ForgeSkill $payload $destination $launcher ('a' * 40)
        [IO.File]::Delete((Join-Path $destination 'docs/install.md'))
        Assert ((Get-ForgeSkillStatus $destination) -eq 'Repairable') 'A missing owned file was not repairable.'
        Install-ForgeSkill $payload $destination $launcher ('a' * 40)
        Assert (Test-ForgeSkill $destination) 'Managed repair failed.'
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $TestRoot 'repair/bootstrap/skill-backups') -Directory)
        Assert ($backups.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $backups[0].FullName '.forge-bootstrap-skill.json'))) 'Previous directory was not preserved.'
        [IO.File]::WriteAllText((Join-Path $destination 'custom-notes.txt'), 'owner data')
        Assert ((Get-ForgeSkillStatus $destination) -eq 'Conflict') 'Additional owner files were not protected.'
    }
    Test 'Launcher attempts automatic repair exactly once and stops when repair cannot finish' {
        $script:Repairs = 0; $script:CliReads = 0
        Mock Get-ForgeBootstrapReport { [pscustomobject]@{ Checks = @(); AutomatableActions = @('missing'); ExitCode = 2 } }
        Mock Invoke-ForgeBootstrapInstall { $script:Repairs++; [pscustomobject]@{ AutomatableActions = @('still missing'); ExitCode = 2 } }
        Mock Write-ForgeBootstrapReport { }
        Mock Read-ForgeInstallation { $script:CliReads++; throw 'CLI must not enter.' }
        Invoke-ForgeCommand $TestRoot @('implement', '--plan', 'unread')
        Assert ($script:Repairs -eq 1 -and $script:CliReads -eq 0 -and $script:ForgeCommandExitCode -eq 2) 'Unbounded repair or premature CLI effect.'
    }
    Test 'Authentication-only failure never invokes bootstrap installation or CLI' {
        Mock Get-ForgeBootstrapReport { [pscustomobject]@{ AutomatableActions = @(); ExitCode = 2 } }
        Mock Invoke-ForgeBootstrapInstall { throw 'Authentication must not cause installation.' }
        Mock Read-ForgeInstallation { throw 'CLI must not enter before sign-in.' }
        Mock Write-ForgeBootstrapReport { }
        Invoke-ForgeCommand $TestRoot @('implement')
        Assert ($script:ForgeCommandExitCode -eq 2) 'Authentication failure exit was lost.'
    }
    Test 'Actual selected native launcher preserves argv and failed command enters exactly once' {
        $tool = Join-Path $TestRoot 'actual original command.ps1'
        $harness = Join-Path $TestRoot 'actual selected launcher harness.ps1'
        $count = Join-Path $TestRoot 'actual-command-entered.txt'
        [IO.File]::WriteAllText($tool, @'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Values)
[IO.File]::AppendAllText($env:FORGE_BOOTSTRAP_FIXTURE_COUNT, "entered`n")
[Console]::Write((@{Values=@($Values); Cpu=$env:DOTNET_PROCESSOR_COUNT; Heap=$env:DOTNET_GCHeapHardLimit}|ConvertTo-Json -Compress))
exit 17
'@)
        [IO.File]::WriteAllText($harness, @'
param([string]$Bootstrap,[string]$Native,[string]$HostPath,[string]$Tool,[string]$Payload,[int]$Budget=1200,[int]$ProbeDelay=0)
. $Bootstrap
function Get-ForgeBootstrapReport {
    if($ProbeDelay){Start-Sleep -Milliseconds $ProbeDelay}
    [pscustomobject]@{Checks=@();AutomatableActions=@();ExitCode=0}
}
function Read-ForgeInstallation { [pscustomobject]@{DotNetHost=$HostPath;ToolAssembly=$Tool;NativeLauncher=$Native} }
function Find-ForgeCommand { param($Name) if($Name -eq 'pwsh'){$HostPath}else{throw 'Unexpected prerequisite discovery in ready fixture.'} }
function Invoke-ForgeBootstrapInstall { throw 'An entered command must never trigger a repair or replay.' }
$env:DOTNET_PROCESSOR_COUNT='9'
Invoke-ForgeCommand $PSScriptRoot @((ConvertFrom-Json $Payload).Arguments) $Budget
[Console]::Error.Write('RESTORED='+$env:DOTNET_PROCESSOR_COUNT)
exit $script:ForgeCommandExitCode
'@)
        $native = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/delivery-forge.ps1'
        $hostPath = (Get-Process -Id $PID).Path
        $values=@('text with spaces & $() `ticks` < > ; quotes "here"','','trailing slash\','-MaximumSeconds','--repo')
        $payload=@{Arguments=$values}|ConvertTo-Json -Compress
        $before=$env:FORGE_BOOTSTRAP_FIXTURE_COUNT
        try {
            $env:FORGE_BOOTSTRAP_FIXTURE_COUNT=$count
            $result=Invoke-ForgeProcess $harness @((Join-Path (Split-Path -Parent $PSScriptRoot) 'bootstrap-delivery-forge.ps1'),$native,$hostPath,$tool,$payload) -TimeoutSeconds 20
            Assert ($result.ExitCode -eq 17) 'Actual command exit code was lost.'
            $observed=$result.Stdout|ConvertFrom-Json
            Assert (($observed.Values|ConvertTo-Json -Compress) -ceq ($values|ConvertTo-Json -Compress)) 'Original argv was interpreted or changed.'
            Assert ($observed.Cpu -eq '2' -and $observed.Heap -eq '0x100000000') 'Accepted prebirth process settings were lost.'
            Assert ($result.Stderr -match 'RESTORED=9') 'Caller environment was not restored.'
            Assert (@(Get-Content -LiteralPath $count).Count -eq 1) 'Original command did not enter exactly once.'
            [IO.File]::Delete($count)
            $expired=Invoke-ForgeProcess $harness @((Join-Path (Split-Path -Parent $PSScriptRoot) 'bootstrap-delivery-forge.ps1'),$native,$hostPath,$tool,$payload,'1','1100') -TimeoutSeconds 20
            Assert ($expired.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $count)) 'Preflight consumed the budget but original command still entered.'
            $windowsPowerShell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
            Assert (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf) 'Windows PowerShell 5.1 acceptance host is missing.'
            $ps51=Invoke-ForgeProcess $windowsPowerShell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$harness,(Join-Path (Split-Path -Parent $PSScriptRoot) 'bootstrap-delivery-forge.ps1'),$native,$hostPath,$tool,$payload) -TimeoutSeconds 30
            Assert ($ps51.ExitCode -eq 17) 'Windows PowerShell bootstrap did not preserve actual command exit.'
            $observed51=$ps51.Stdout|ConvertFrom-Json
            Assert (($observed51.Values|ConvertTo-Json -Compress) -ceq ($values|ConvertTo-Json -Compress)) 'Windows PowerShell origin changed original argv.'
            Assert ($observed51.Cpu -eq '2' -and $observed51.Heap -eq '0x100000000') 'Windows PowerShell hop bypassed resource-aware launcher.'
            Assert ($ps51.Stderr -match 'RESTORED=9' -and @(Get-Content -LiteralPath $count).Count -eq 1) 'Windows PowerShell command replayed or caller environment changed.'
        } finally { $env:FORGE_BOOTSTRAP_FIXTURE_COUNT=$before }
    }
    Test 'Real generated facade preserves Unicode dates common options and zero argv from PS7 PS51 and OEM input' {
        $fixtureRoot=Join-Path $TestRoot 'generated facade actual'
        $null=New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'bin'),(Join-Path $fixtureRoot 'bootstrap')
        $actualBootstrap=Join-Path (Split-Path -Parent $PSScriptRoot) 'bootstrap-delivery-forge.ps1'
        $selectedBootstrap=Join-Path $fixtureRoot 'bootstrap/selected.ps1'
        $footer=@'
function Get-ForgeBootstrapReport { [pscustomobject]@{Checks=@();AutomatableActions=@();ExitCode=0} }
function Read-ForgeInstallation { Get-Content -LiteralPath $env:DF_FIXTURE_RECORD -Raw|ConvertFrom-Json }
function Find-ForgeCommand {param($Name) if($Name -eq 'pwsh'){$env:DF_FIXTURE_PWSH}else{throw 'Unexpected prerequisite probe in selected fixture.'}}
function Invoke-ForgeBootstrapInstall {throw 'An entered failed command must not be repaired or replayed.'}
'@
        [IO.File]::WriteAllText($selectedBootstrap,[IO.File]::ReadAllText($actualBootstrap)+"`r`n"+$footer)
        $tool=Join-Path $fixtureRoot 'inert tool.ps1'
        [IO.File]::WriteAllText($tool,@'
[IO.File]::AppendAllText($env:DF_FIXTURE_COUNTER,"entered`n")
[Console]::Write((@{Values=@($args);Count=$args.Count;Cpu=$env:DOTNET_PROCESSOR_COUNT;Heap=$env:DOTNET_GCHeapHardLimit}|ConvertTo-Json -Compress))
exit 17
'@)
        $hostPath=(Get-Process -Id $PID).Path
        $record=@{NativeStartReceiptSupported=$true;BootstrapScript=$selectedBootstrap;BootstrapScriptSha256=(Get-FileHash -LiteralPath $selectedBootstrap).Hash.ToLowerInvariant();DotNetHost=$hostPath;ToolAssembly=$tool;NativeLauncher=(Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/delivery-forge.ps1')}
        $recordPath=Join-Path $fixtureRoot 'installation.json'
        $record|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $recordPath -Encoding utf8NoBOM
        $facade=Join-Path $fixtureRoot 'bin/delivery-forge.ps1'
        [IO.File]::WriteAllText($facade,(Get-ForgeLauncherContent))
        $counter=Join-Path $fixtureRoot 'entries.txt'
        $environment=@{DF_FIXTURE_RECORD=$recordPath;DF_FIXTURE_PWSH=$hostPath;DF_FIXTURE_COUNTER=$counter;DOTNET_PROCESSOR_COUNT='9'}
        $unicode='path-'+[char]0x65E5+[char]0x672C+'-'+[char]::ConvertFromUtf32(0x1F680)
        $values=@($unicode,'2026-01-15T10:00:00Z','2026-01-15T10:00:00+13:00','','trailing slash','--body','-Verbose','-Debug','-ErrorAction','SilentlyContinue','-?','-f','-ForgeArguments','--')
        foreach($origin in @($hostPath,(Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'))){
            $result=Invoke-ForgeProcess $origin (@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$facade)+$values) -Environment $environment -TimeoutSeconds 30
            Assert ($result.ExitCode -eq 17) 'Generated facade lost actual failed-command exit.'
            $seen=$result.Stdout|ConvertFrom-Json
            Assert ($seen.Count -eq $values.Count -and ($seen.Values|ConvertTo-Json -Compress) -ceq ($values|ConvertTo-Json -Compress)) 'Generated facade changed Unicode/date/option/empty argv.'
            Assert ($seen.Cpu -eq '2' -and $seen.Heap -eq '0x100000000') 'Generated facade bypassed selected native resource settings.'
        }
        $oem=Join-Path $fixtureRoot 'oem caller.ps1'
        [IO.File]::WriteAllText($oem,@'
param([string]$SelectedFacade,[string]$Wire)
[Console]::InputEncoding=[Text.Encoding]::GetEncoding(437)
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$encoded=$Wire|ConvertFrom-Json
$decoded=@(foreach($item in $encoded.Values64){[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($item))})
& $SelectedFacade @decoded
exit $LASTEXITCODE
'@)
        $wire=@{Values64=@($values|ForEach-Object{[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))})}|ConvertTo-Json -Compress
        $result=Invoke-ForgeProcess $hostPath @('-NoProfile','-NonInteractive','-File',$oem,$facade,$wire) -Environment $environment -TimeoutSeconds 30
        Assert ($result.ExitCode -eq 17) 'OEM-origin command exit changed.'
        $seen=$result.Stdout|ConvertFrom-Json
        Assert (($seen.Values|ConvertTo-Json -Compress) -ceq ($values|ConvertTo-Json -Compress)) 'OEM input encoding changed actual argv.'
        $empty=Invoke-ForgeProcess $hostPath @('-NoProfile','-NonInteractive','-File',$facade) -Environment $environment -TimeoutSeconds 30
        Assert ($empty.ExitCode -eq 17) 'No-argument command did not enter exactly once.'
        $seen=$empty.Stdout|ConvertFrom-Json
        Assert ($seen.Count -eq 0) 'Generated facade manufactured an empty argument for no argv.'
        Assert (@(Get-Content -LiteralPath $counter).Count -eq 4) 'One generated-facade invocation entered more than once.'
        $receipts=@(Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'bootstrap') -Recurse -Filter 'native-start.json' -File)
        Assert ($receipts.Count -eq 4) 'Native PID/start receipts were not retained for each actual entry.'
        foreach($receiptFile in $receipts){
            $receipt=Get-Content -LiteralPath $receiptFile.FullName -Raw|ConvertFrom-Json
            Assert ($receipt.Kind -eq 'DeliveryForgeNativeStart-v1' -and $receipt.NativePid -gt 0 -and $receipt.LauncherPid -gt 0 -and [DateTimeOffset]::Parse($receipt.NativeStartUtc) -le [DateTimeOffset]::UtcNow) 'Actual native PID/start receipt is incomplete.'
        }
        Assert (-not(Test-Path -LiteralPath (Join-Path $fixtureRoot 'bootstrap.lock'))) 'Known failed command retained an UNKNOWN lock.'
    }
    Test 'Actual native deadline records PID and attempts termination while retaining UNKNOWN125' {
        $case=Join-Path $TestRoot 'actual native deadline';$null=New-Item -ItemType Directory -Path $case
        $tool=Join-Path $case 'bounded sleeper.ps1'
        [IO.File]::WriteAllText($tool,'Start-Sleep -Seconds 15; exit 0')
        $receiptPath=Join-Path $case 'native-start.json'
        $hostPath=(Get-Process -Id $PID).Path
        $native=Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/delivery-forge.ps1'
        $result=Invoke-ForgeProcess $hostPath @('-NoProfile','-NonInteractive','-File',$native,'-DotNetHost',$hostPath,'-ToolAssembly',$tool,'-MaximumSeconds','1','-NativeStartReceipt',$receiptPath) -TimeoutSeconds 15
        Assert ($result.ExitCode -eq 125 -and $result.Stderr -match 'DFCLI_LAUNCHER_UNKNOWN' -and $result.Stderr -match 'killAttempted=True') 'Deadline did not preserve UNKNOWN and termination evidence.'
        $receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
        Assert ($receipt.NativePid -gt 0 -and $receipt.NativeStartUtc) 'Deadline lacks the actual entered PID/start.'
        $sameProcessAlive=$true
        for($i=0;$i -lt 10 -and $sameProcessAlive;$i++){
            $observed=Get-Process -Id $receipt.NativePid -ErrorAction SilentlyContinue
            $sameProcessAlive=$observed -and $observed.StartTime.ToUniversalTime().ToString('o') -eq $receipt.NativeStartUtc
            if($sameProcessAlive){Start-Sleep -Milliseconds 100}
        }
        Assert (-not $sameProcessAlive) 'This actual single-process fixture survived best-effort termination.'
        # This finite fixture proves its observed root exited; it does not turn
        # arbitrary process-tree UNKNOWN outcomes into a closure certificate.
    }
    Test 'Invalid UTF16 surrogate is refused before original native entry' {
        $state=Join-Path $TestRoot 'invalid-surrogate-state';$null=New-Item -ItemType Directory -Path $state
        $script:InvalidEntries=0
        Mock Get-ForgeBootstrapReport {[pscustomobject]@{Checks=@();AutomatableActions=@();ExitCode=0}}
        Mock Read-ForgeInstallation {[pscustomobject]@{DotNetHost=(Get-Process -Id $PID).Path;ToolAssembly=$PSCommandPath;NativeLauncher=(Join-Path (Split-Path -Parent $PSScriptRoot) 'delivery-forge.ps1')}}
        Mock Find-ForgeCommand {(Get-Process -Id $PID).Path}
        Mock Invoke-ForgeProcess {$script:InvalidEntries++;throw 'Invalid scalar must not enter a native process.'}
        try { Invoke-ForgeCommand $state @([string][char]0xD800) } catch { Assert ($_.Exception.ToString() -match 'EncoderFallback|Unable to translate') 'A different error hid invalid UTF16 refusal.' }
        Assert ($script:InvalidEntries -eq 0 -and $script:ForgeCommandExitCode -eq 1) 'Invalid surrogate was silently replaced or entered.'
        Assert (-not(Test-Path -LiteralPath (Join-Path $state 'bootstrap.lock'))) 'Known pre-entry validation refusal retained a lock.'
    }
    Test 'Failed UNKNOWN persistence keeps exit125 and the pre-entry replay barrier' {
        $state=Join-Path $TestRoot 'unknown-save-failure';$null=New-Item -ItemType Directory -Path $state
        $script:UnknownEntries=0
        Mock Get-ForgeBootstrapReport {[pscustomobject]@{Checks=@();AutomatableActions=@();ExitCode=0}}
        Mock Read-ForgeInstallation {[pscustomobject]@{DotNetHost=(Get-Process -Id $PID).Path;ToolAssembly=$PSCommandPath;NativeLauncher=(Join-Path (Split-Path -Parent $PSScriptRoot) 'delivery-forge.ps1')}}
        Mock Find-ForgeCommand {(Get-Process -Id $PID).Path}
        Mock Invoke-ForgeProcess {$script:UnknownEntries++;[pscustomobject]@{ExitCode=125;Stdout='';Stderr=''}}
        Mock Save-ForgeUnknown {throw 'Injected unavailable disk for UNKNOWN receipt.'}
        Invoke-ForgeCommand $state @('implement')
        Assert ($script:ForgeCommandExitCode -eq 125 -and $script:UnknownEntries -eq 1) 'Failed UNKNOWN save replaced status125 or replayed command.'
        Assert (Test-Path -LiteralPath (Join-Path $state 'bootstrap.lock')) 'No persistent pre-entry replay barrier remains.'
        # Model the next fresh caller; the on-disk barrier must work independently
        # of the previous caller's in-memory sticky flag and failed receipt write.
        $script:ForgeBootstrapUnsettled=$false
        Set-Item Function:script:Get-ForgeBootstrapReport $originals['Get-ForgeBootstrapReport']
        Mock Find-ForgeCommand {throw 'Retained lock must refuse before command discovery.'}
        Mock Write-ForgeBootstrapReport {}
        Invoke-ForgeCommand $state @('implement')
        Assert ($script:ForgeCommandExitCode -eq 2 -and $script:UnknownEntries -eq 1) 'A new caller replayed after failed UNKNOWN persistence.'
    }
    Test 'Native quoting handles empty values, spaces, embedded quotes and trailing slashes' {
        Assert ((ConvertTo-ForgeNativeArgument '') -eq '""') 'Empty argument lost.'
        Assert ((ConvertTo-ForgeNativeArgument 'plain') -eq 'plain') 'Plain argument changed.'
        Assert ((ConvertTo-ForgeNativeArgument 'a b') -eq '"a b"') 'Spaced argument not quoted.'
        Assert ((ConvertTo-ForgeNativeArgument 'a"b') -eq '"a\"b"') 'Embedded quote not escaped.'
        Assert ((ConvertTo-ForgeNativeArgument 'a b\') -eq '"a b\\"') 'Trailing slash consumed closing quote.'
    }
    Test 'Actual native runner preserves argument data including shell metacharacters' {
        $shim = Join-Path $TestRoot 'argv fixture.ps1'
        [IO.File]::WriteAllText($shim, 'param([string]$Value) [Console]::Write(($Value|ConvertTo-Json -Compress)); exit 7')
        $text = 'text with spaces & $() `ticks` < > ; quotes "here"'
        $result = Invoke-ForgeProcess $shim @($text) -TimeoutSeconds 15
        Assert ($result.ExitCode -eq 7 -and ($result.Stdout | ConvertFrom-Json) -eq $text) 'Native argv data was interpreted or changed.'
    }
    Test 'Generated launcher parses and always uses the shared bootstrap boundary' {
        $tokens = $null; $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseInput((Get-ForgeLauncherContent), [ref]$tokens, [ref]$errors)
        Assert ($errors.Count -eq 0) 'Generated launcher does not parse.'
        Assert ((Get-ForgeLauncherContent) -match 'Invoke-ForgeCommand') 'Generated launcher bypasses bootstrap.'
    }
    Test 'Non-JSON entrypoint retains UNKNOWN exit 125 before any native prerequisite probe' {
        $sourceFile=Join-Path (Split-Path -Parent $PSScriptRoot) 'bootstrap-delivery-forge.ps1'
        $actual=[IO.File]::ReadAllText($sourceFile)
        $entry="if (`$MyInvocation.InvocationName -ne '.') {"
        Assert (@([regex]::Matches($actual,[regex]::Escape($entry))).Count -eq 1) 'Actual entrypoint was not uniquely identified.'
        # Fault injection only: no unknown native process is created. Exercise
        # the complete real entrypoint, report and catch with sticky refusal.
        $scratch=Join-Path $TestRoot 'unknown-entrypoint.INJECTED.ps1'
        [IO.File]::WriteAllText($scratch,$actual.Replace($entry,"`$script:ForgeBootstrapUnsettled = `$true`r`n"+$entry))
        $readonlyState=Join-Path $TestRoot 'unknown-readonly-state'
        $result=Invoke-ForgeProcess (Get-Process -Id $PID).Path @('-NoProfile','-NonInteractive','-File',$scratch,'-Mode','Check','-StateRoot',$readonlyState) -TimeoutSeconds 15
        Assert ($result.ExitCode -eq 125 -and $result.Stderr -match 'FORGE_BOOTSTRAP_UNKNOWN') 'Non-JSON catch lost the original UNKNOWN status.'
        Assert (-not(Test-Path -LiteralPath $readonlyState)) 'Pre-probe Check refusal created installation state.'
    }
    Test 'Actual held installation lock prevents every native prerequisite probe' {
        $root=Join-Path $TestRoot 'real-held-lock';$null=New-Item -ItemType Directory -Path $root
        $lock=[IO.File]::Open((Join-Path $root 'bootstrap.lock'),'CreateNew','ReadWrite','None')
        Mock Find-ForgeCommand { throw 'No command discovery belongs to a competing locked attempt.' }
        Mock Invoke-ForgeProcess { throw 'No native probe belongs to a competing locked attempt.' }
        try {
            $report=Get-ForgeBootstrapReport $root $source
            Assert ($report.ExitCode -eq 2 -and @($report.Checks|Where-Object Id -eq 'bootstrap-lock').Count -eq 1) 'Held lock was not an immediate precise refusal.'
            Assert ($report.AutomatableActions.Count -eq 0) 'A competing lock proposed automatic repair.'
        } finally {$lock.Dispose()}
    }
    Test 'Uncertain prerequisite probe cannot be swallowed into automatic repair' {
        $script:Repairs=0
        Mock Find-ForgeCommand {param($Name) Join-Path $TestRoot ($Name+'.exe')}
        Mock Invoke-ForgeProcess {$script:ForgeBootstrapUnsettled=$true;throw 'Fixture entered probe has unknown output disposition.'}
        Mock Invoke-ForgeBootstrapInstall {$script:Repairs++;throw 'Unknown probe must never start installation.'}
        Mock Write-ForgeBootstrapReport {}
        try {Invoke-ForgeCommand (Join-Path $TestRoot 'unknown-probe') @('implement')} catch {}
        Assert ($script:ForgeBootstrapUnsettled -and $script:Repairs -eq 0) 'Unknown probe was reset or treated as repairable missing input.'
    }
    Test 'Existing Codex skill root is selected without creating a competing skill' {
        $legacy=Join-Path $env:USERPROFILE '.codex/skills/delivery-forge'
        $null=New-Item -ItemType Directory -Path $legacy -Force
        [IO.File]::WriteAllText((Join-Path $legacy 'SKILL.md'),'Owner fixture; not installer-managed.')
        Assert ((Get-ForgeSkillDestination 'codex') -eq $legacy) 'Existing Codex skill root was ignored.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.agents/skills/delivery-forge'))) 'A competing discovery root was created.'
        # Retain this unrelated owner fixture; later full-install control uses
        # its own isolated profile rather than overwriting an unmanaged skill.
    }
    Test 'Full install orchestration creates CLI selection, generated launcher and both skills without login' {
        $env:USERPROFILE=Join-Path $TestRoot 'full-install-user'
        $root = Join-Path $TestRoot 'full-install'; $payload = Join-Path $TestRoot 'full-source'
        $null = New-Item -ItemType Directory -Path (Join-Path $payload 'components/tool/DeliveryForge.Tool'), (Join-Path $payload 'references'), (Join-Path $payload 'docs'), (Join-Path $payload 'scripts') -Force
        [IO.File]::WriteAllText((Join-Path $payload 'global.json'), '{"sdk":{"version":"10.0.401"}}')
        [IO.File]::WriteAllText((Join-Path $payload 'components/tool/DeliveryForge.Tool/DeliveryForge.Tool.csproj'), '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>')
        [IO.File]::WriteAllText((Join-Path $payload 'SKILL.md'), "---`nname: delivery-forge`ndescription: Bootstrap test fixture`n---`nFixture only.")
        [IO.File]::WriteAllText((Join-Path $payload 'docs/test.md'), 'Fixture only.')
        [IO.File]::Copy((Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/delivery-forge.ps1'), (Join-Path $payload 'scripts/delivery-forge.ps1'))
        $script:LauncherFixtureBytes=[IO.File]::ReadAllBytes((Join-Path $payload 'scripts/delivery-forge.ps1'))
        $script:FakeDotnet = Join-Path $TestRoot 'fake-dotnet.exe'; [IO.File]::WriteAllText($script:FakeDotnet, 'INERT TEST FILE, NOT AN EXECUTABLE')
        $script:FullCommands = [Collections.Generic.List[string]]::new()
        Mock Find-ForgeCommand { param($Name) Join-Path $TestRoot ($Name + '.exe') }
        Mock Get-ForgeBootstrapReport {
            param($Root, $SelectedSource)
            [pscustomobject]@{ Checks = @('git', 'gh', 'codex', 'claude', 'pwsh' | ForEach-Object { New-ForgeCheck $_ 'Ready' 'Fixture selection only' $false }); DotNetHost = $script:FakeDotnet; ExitCode = 0; AutomatableActions = @(); UserActions = @(); InstallationReady = $true; AuthenticationReady = $true }
        }
        Mock Invoke-ForgeProcess {
            param($Executable, $Arguments, $WorkingDirectory, $TimeoutSeconds, $Environment, $LogPrefix)
            $script:FullCommands.Add(($Arguments -join ' '))
            $out = ''
            if ($Arguments -contains 'rev-parse') { $out = 'a' * 40 }
            if ($Arguments[0] -eq 'pack') {
                $package=[IO.File]::Open((Join-Path $Arguments[-1] 'deliveryforge.tool.0.1.0.nupkg'),'CreateNew')
                $archive=[IO.Compression.ZipArchive]::new($package,[IO.Compression.ZipArchiveMode]::Create)
                try { $entry=$archive.CreateEntry('tools/net10.0/any/content/scripts/delivery-forge.ps1.txt');$stream=$entry.Open();try{$stream.Write($script:LauncherFixtureBytes,0,$script:LauncherFixtureBytes.Length)}finally{$stream.Dispose()} } finally {$archive.Dispose();$package.Dispose()}
            }
            if ($Arguments[0] -eq 'tool') {
                $at = [Array]::IndexOf($Arguments, '--tool-path'); $prefix = $Arguments[$at + 1]
                $null = New-Item -ItemType Directory -Path (Join-Path $prefix '.store/tool') -Force
                [IO.File]::WriteAllText((Join-Path $prefix '.store/tool/DeliveryForge.Tool.dll'), 'INERT DLL FIXTURE')
                [IO.File]::WriteAllText((Join-Path $prefix '.store/tool/dependency.dll'), 'INERT DEPENDENCY FIXTURE')
            }
            [pscustomobject]@{ ExitCode = 0; Stdout = $out; Stderr = '' }
        }
        $result = Invoke-ForgeBootstrapInstall $root $payload 'main' 120 -RequiredAuthentication @()
        Assert ($result.ExitCode -eq 0) ('Install fixture failed: ' + $result.Failure)
        Assert (-not $result.AuthenticationChecked -and $null -eq $result.AuthenticationReady) 'Installation falsely certified unobserved sign-ins.'
        $record = Read-ForgeInstallation $root
        Assert ($null -ne $record -and -not $record.RuntimeCertified) 'Installation selection missing or falsely runtime-certified.'
        Assert (Test-Path -LiteralPath $record.BootstrapScript) 'Immutable shared bootstrap not copied.'
        Assert ((Get-Content -LiteralPath $record.BootstrapScript -Raw) -match 'function Invoke-ForgeBootstrapInstall') 'Wrong source copied as bootstrap.'
        Assert ((Get-Content -LiteralPath (Join-Path $root 'bin/delivery-forge.ps1') -Raw) -match 'Invoke-ForgeCommand') 'Generated launcher missing.'
        foreach ($location in @('.agents', '.claude')) { Assert (Test-ForgeSkill (Join-Path $env:USERPROFILE ($location + '/skills/delivery-forge'))) ('Skill missing: ' + $location) }
        Assert (-not (($script:FullCommands -join "`n") -match 'auth login|login --')) 'Install attempted authentication.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'bootstrap.lock'))) 'Settled fixture kept its lock.'
        [IO.File]::AppendAllText((Join-Path (Split-Path -Parent $record.ToolAssembly) 'dependency.dll'), 'changed')
        Assert ($null -eq (Read-ForgeInstallation $root)) 'Changed non-entry assembly was accepted as installed.'
    }
    Test 'Existing bootstrap lock stops automatic repair before native or CLI entry' {
        $script:Repairs = 0
        Mock Get-ForgeBootstrapReport { [pscustomobject]@{ Checks = @(New-ForgeCheck 'bootstrap-lock' 'UserAction' 'Unresolved attempt' $false); AutomatableActions = @('missing'); ExitCode = 2 } }
        Mock Invoke-ForgeBootstrapInstall { $script:Repairs++; throw 'Must not enter repair.' }
        Mock Write-ForgeBootstrapReport { }
        Mock Read-ForgeInstallation { throw 'Must not enter CLI.' }
        Invoke-ForgeCommand $TestRoot @('implement')
        Assert ($script:Repairs -eq 0 -and $script:ForgeCommandExitCode -eq 2) 'Unresolved install was replayed.'
    }
} finally {
    $env:USERPROFILE = $savedProfile; $env:LOCALAPPDATA = $savedLocal
    foreach ($name in $savedOverrides.Keys) { [Environment]::SetEnvironmentVariable($name, $savedOverrides[$name]) }
}
$summary = @{ Kind = 'DeliveryForgeBootstrapTests-v1'; Tests = @($results.ToArray()); Passed = @($results | Where-Object Passed).Count; Failed = @($results | Where-Object { -not $_.Passed }).Count; Scope = 'PowerShell behavior and local filesystem/process fixture controls; no prerequisite installation, model request, login, source build or runtime certification.' }
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $TestRoot 'results.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
if ($summary.Failed -gt 0) { exit 1 }
