#requires -Version 7.0
[CmdletBinding()]
param([string]$TestRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) ('.handoff/bootstrap-tests/' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'bootstrap-delivery-forge.ps1')
$null = New-Item -ItemType Directory -Path $TestRoot -Force
$results = [Collections.Generic.List[object]]::new()
$originals = @{}
foreach ($name in @('Find-ForgeCommand', 'Invoke-ForgeProcess', 'Read-ForgeInstallation', 'Get-ForgeBootstrapReport', 'Invoke-ForgeBootstrapInstall', 'Write-ForgeBootstrapReport', 'Get-ForgeSkillStatus', 'Get-ForgeSkillDestination', 'Save-ForgeUnknown', 'Move-ForgeInstallationDirectory', 'Install-ForgeSkill')) { $originals[$name] = (Get-Item ('Function:' + $name)).ScriptBlock }
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
            if ($Executable -like '*gh.exe' -and $Arguments[0] -eq '--version') { $out = 'gh version 2.99.0' }
            elseif ($Executable -like '*gh.exe' -and $Arguments -contains '--help') { $out = '  --no-refresh  Suppress automatic refresh' }
            elseif ($Arguments[0] -eq 'login') { $out = 'Logged in using ChatGPT private@example.invalid' }
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
        Mock Read-ForgeInstallation {[pscustomobject]@{NativeStartReceiptSupported=$true;NativeCompletionReceiptSupported=$true;RequiredSdkVersion='10.0.401';DotNetHost=(Join-Path $TestRoot 'dotnet.exe');ToolAssembly='fixture.dll'}}
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
    Test 'Fresh skill candidate refuses existing managed or modified discovery payload' {
        $payload = Join-Path $TestRoot 'skill-source'; $null = New-Item -ItemType Directory -Path (Join-Path $payload 'references'), (Join-Path $payload 'docs'), (Join-Path $payload 'scripts') -Force
        [IO.File]::WriteAllText((Join-Path $payload 'SKILL.md'), "---`nname: delivery-forge`ndescription: Test fixture only`n---`nTest fixture.")
        [IO.File]::WriteAllText((Join-Path $payload 'references/read.md'), 'Reference fixture.')
        [IO.File]::WriteAllText((Join-Path $payload 'docs/install.md'), 'Document fixture.')
        $destination = Join-Path $TestRoot 'skills/one'
        Install-ForgeSkill $payload $destination (Join-Path $TestRoot 'bin/delivery-forge.ps1') ('a' * 40)
        Assert (Test-ForgeSkill $destination) 'Installed skill failed readback.'
        $path = Join-Path $destination 'SKILL.md'; $before = [IO.File]::ReadAllBytes($path)
        $existingRefused=$false;try {Install-ForgeSkill $payload $destination (Join-Path $TestRoot 'bin/delivery-forge.ps1') ('a' * 40)}catch{$existingRefused=$true}
        Assert $existingRefused 'Fresh-candidate writer adopted an existing managed discovery directory.'
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
    Test 'Missing managed discovery file is preserved by fresh-candidate-only writer' {
        $payload = Join-Path $TestRoot 'skill-source'
        $destination = Join-Path $TestRoot 'repair/skills/delivery-forge'
        $launcher = Join-Path $TestRoot 'repair/bin/delivery-forge.ps1'
        Install-ForgeSkill $payload $destination $launcher ('a' * 40)
        [IO.File]::Delete((Join-Path $destination 'docs/install.md'))
        Assert ((Get-ForgeSkillStatus $destination) -eq 'Repairable') 'A missing owned file was not repairable.'
        $remaining=[IO.File]::ReadAllBytes((Join-Path $destination 'SKILL.md'))
        $refused=$false;try {Install-ForgeSkill $payload $destination $launcher ('a' * 40)}catch{$refused=$true}
        Assert ($refused -and (Get-ForgeSkillStatus $destination) -eq 'Repairable') 'Fresh-candidate writer performed discovery-directory repair.'
        Assert ([Convert]::ToBase64String($remaining) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $destination 'SKILL.md')))) 'Known missing-file refusal changed surviving owner bytes.'
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
function Read-ForgeInstallation { [pscustomobject]@{NativeStartReceiptSupported=$true;NativeCompletionReceiptSupported=$true;DotNetHost=$HostPath;ToolAssembly=$Tool;NativeLauncher=$Native} }
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
# Echo each received string as UTF8 Base64 so the fixture's own console code
# page and JSON date parser cannot alter the evidence being compared.
$utf8=[Text.UTF8Encoding]::new($false,$true)
# This inert target is itself hosted with PowerShell -File. Observe its actual
# native argv rather than introducing a second PowerShell argument parser.
$osWords=[Environment]::GetCommandLineArgs();$ownIndex=-1
for($j=0;$j -lt $osWords.Count;$j++){
    if([string]::Equals([string]$osWords[$j],$PSCommandPath,[StringComparison]::OrdinalIgnoreCase)){$ownIndex=$j;break}
}
if($ownIndex -lt 0){throw 'Inert native target path absent from actual OS argv.'}
$actualValues=@();if($ownIndex+1 -lt $osWords.Count){$actualValues=@($osWords[($ownIndex+1)..($osWords.Count-1)])}
$received64=@(foreach($value in $actualValues){[Convert]::ToBase64String($utf8.GetBytes([string]$value))})
[Console]::Write((@{Values64=$received64;Count=$actualValues.Count;Cpu=$env:DOTNET_PROCESSOR_COUNT;Heap=$env:DOTNET_GCHeapHardLimit}|ConvertTo-Json -Compress))
exit 17
'@)
        $hostPath=(Get-Process -Id $PID).Path
        $record=@{NativeStartReceiptSupported=$true;NativeCompletionReceiptSupported=$true;BootstrapScript=$selectedBootstrap;BootstrapScriptSha256=(Get-FileHash -LiteralPath $selectedBootstrap).Hash.ToLowerInvariant();DotNetHost=$hostPath;ToolAssembly=$tool;NativeLauncher=(Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/delivery-forge.ps1')}
        $recordPath=Join-Path $fixtureRoot 'installation.json'
        $record|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $recordPath -Encoding utf8NoBOM
        $facade=Join-Path $fixtureRoot 'bin/delivery-forge.ps1'
        [IO.File]::WriteAllText($facade,(Get-ForgeLauncherContent))
        $counter=Join-Path $fixtureRoot 'entries.txt'
        $environment=@{DF_FIXTURE_RECORD=$recordPath;DF_FIXTURE_PWSH=$hostPath;DF_FIXTURE_COUNTER=$counter;DOTNET_PROCESSOR_COUNT='9'}
        $unicode='path-'+[char]0x65E5+[char]0x672C+'-'+[char]::ConvertFromUtf32(0x1F680)
        $values=@($unicode,'2026-01-15T10:00:00Z','2026-01-15T10:00:00+13:00','',('trailing slash'+[char]92),'--body','-Verbose','-Debug','-ErrorAction','SilentlyContinue','-?','-f','-ForgeArguments','--','-x:y','-x:','--repo:C:\x','- fix: update docs')
        $expected64=@($values|ForEach-Object{[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))})
        foreach($origin in @($hostPath,(Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'))){
            $result=Invoke-ForgeProcess $origin (@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$facade)+$values) -Environment $environment -TimeoutSeconds 30
            $result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $fixtureRoot ([IO.Path]::GetFileNameWithoutExtension($origin)+'-observed.json')) -Encoding utf8NoBOM
            Assert ($result.ExitCode -eq 17) ('Generated facade lost actual failed-command exit: '+$result.ExitCode+'; '+$result.Stderr)
            $seen=$result.Stdout|ConvertFrom-Json
            Assert ($seen.Count -eq $values.Count -and ($seen.Values64|ConvertTo-Json -Compress) -ceq ($expected64|ConvertTo-Json -Compress)) 'Generated facade changed Unicode/date/option/empty argv.'
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
        # Trailing-colon tokens have no recoverable provenance through a direct
        # invocation. Raw native -File above still proves exact '-x:' support.
        $directValues=@($values|Where-Object {$_ -cne '-x:'})
        $directExpected64=@($directValues|ForEach-Object{[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))})
        $wire=@{Values64=$directExpected64}|ConvertTo-Json -Compress
        $result=Invoke-ForgeProcess $hostPath @('-NoProfile','-NonInteractive','-File',$oem,$facade,$wire) -Environment $environment -TimeoutSeconds 30
        Assert ($result.ExitCode -eq 17) 'OEM-origin command exit changed.'
        $seen=$result.Stdout|ConvertFrom-Json
        Assert (($seen.Values64|ConvertTo-Json -Compress) -ceq ($directExpected64|ConvertTo-Json -Compress)) 'OEM input encoding changed actual argv.'
        $utf8Caller=Join-Path $fixtureRoot 'PS51 UTF8 caller.ps1'
        [IO.File]::WriteAllText($utf8Caller,@'
param([string]$SelectedFacade,[string]$Wire)
[Console]::InputEncoding=[Text.Encoding]::GetEncoding(65001)
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$encoded=$Wire|ConvertFrom-Json
$decoded=@(foreach($item in $encoded.Values64){[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($item))})
[Console]::Error.Write('INPUT_CODE_PAGE='+[Console]::InputEncoding.CodePage)
& $SelectedFacade @decoded
exit $LASTEXITCODE
'@,[Text.UTF8Encoding]::new($false))
        $ps51Host=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
        $utf8Result=Invoke-ForgeProcess $ps51Host @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$utf8Caller,$facade,$wire) -Environment $environment -TimeoutSeconds 30
        Assert ($utf8Result.ExitCode -eq 17 -and $utf8Result.Stderr -match 'INPUT_CODE_PAGE=65001') 'Actual PS51 UTF8-input origin did not enter and preserve exit.'
        $utf8Seen=$utf8Result.Stdout|ConvertFrom-Json
        Assert (($utf8Seen.Values64|ConvertTo-Json -Compress) -ceq ($directExpected64|ConvertTo-Json -Compress)) 'PS51 input code page65001 mutated or rejected actual argv.'

        $empty=Invoke-ForgeProcess $hostPath @('-NoProfile','-NonInteractive','-File',$facade) -Environment $environment -TimeoutSeconds 30
        Assert ($empty.ExitCode -eq 17) 'No-argument command did not enter exactly once.'
        $seen=$empty.Stdout|ConvertFrom-Json
        Assert ($seen.Count -eq 0) 'Generated facade manufactured an empty argument for no argv.'
        Assert (@(Get-Content -LiteralPath $counter).Count -eq 5) 'One generated-facade invocation entered more than once.'
        $receipts=@(Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'bootstrap') -Recurse -Filter 'native-start.json' -File)
        Assert ($receipts.Count -eq 5) 'Native PID/start receipts were not retained for each actual entry.'
        foreach($receiptFile in $receipts){
            $receipt=Get-Content -LiteralPath $receiptFile.FullName -Raw|ConvertFrom-Json
            Assert ($receipt.Kind -eq 'DeliveryForgeNativeStart-v1' -and $receipt.NativePid -gt 0 -and $receipt.LauncherPid -gt 0 -and ([DateTimeOffset]$receipt.NativeStartUtc) -le [DateTimeOffset]::UtcNow) 'Actual native PID/start receipt is incomplete.'
        }
        Assert (-not(Test-Path -LiteralPath (Join-Path $fixtureRoot 'bootstrap.lock'))) 'Known failed command retained an UNKNOWN lock.'
    }
    Test 'Batch and explicit script-shim stdin are refused before native entry' {
        $caseRoot=Join-Path $TestRoot 'A1 preentry boundaries';$null=New-Item -ItemType Directory -Path $caseRoot
        foreach($extension in @('.cmd','.bat')){
            $target=Join-Path $caseRoot ('inert'+$extension)
            [IO.File]::WriteAllText($target,'INERT FILE; MUST NEVER EXECUTE')
            $refused=$false
            try{Invoke-ForgeProcess $target @('literal') -TimeoutSeconds 5|Out-Null}catch{$refused=$_.Exception.Message -match 'FORGE_BOOTSTRAP_UNSUPPORTED_BATCH'}
            Assert ($refused -and -not $script:ForgeBootstrapUnsettled) 'Batch target was entered or became UNKNOWN instead of known preentry refusal.'
        }
        $scriptTarget=Join-Path $caseRoot 'stdin target.ps1';$entered=Join-Path $caseRoot 'entered.txt'
        [IO.File]::WriteAllText($scriptTarget,'[IO.File]::WriteAllText($env:DF_STDIN_ENTERED,''entered'');exit 0')
        foreach($inputValue in @('','controlled ASCII fixture',$null)){
            $options=@{Executable=$scriptTarget;Arguments=@('literal');StandardInput=$inputValue;Environment=@{DF_STDIN_ENTERED=$entered};TimeoutSeconds=5}
            $refused=$false
            try{Invoke-ForgeProcess @options|Out-Null}catch{$refused=$_.Exception.Message -match 'FORGE_BOOTSTRAP_UNSUPPORTED_SHIM_STDIN'}
            Assert ($refused -and -not(Test-Path -LiteralPath $entered) -and -not $script:ForgeBootstrapUnsettled) 'Explicit shim stdin was lost or entered instead of known preentry refusal.'
        }
    }
    Test 'Batch-only prerequisite discovery is unsupported without version or authentication entry' {
        $script:BatchVersionCalls=0
        Mock Find-ForgeCommand {param($Name) if($Name -eq 'git'){Join-Path $TestRoot 'git.cmd'}elseif($Name -eq 'pwsh'){Join-Path $TestRoot 'pwsh.exe'}else{$null}}
        Mock Read-ForgeInstallation {$null}
        Mock Invoke-ForgeProcess {
            param($Executable,$Arguments)
            if($Executable -like '*.cmd' -or $Executable -like '*.bat'){$script:BatchVersionCalls++;throw 'Batch prerequisite must not start.'}
            [pscustomobject]@{ExitCode=0;Stdout='fixture version';Stderr=''}
        }
        $report=Get-ForgeBootstrapReport (Join-Path $TestRoot 'batch readiness') $source -RequiredTools @('git','pwsh') -RequiredAuthentication @()
        $gitRow=$report.Checks|Where-Object Id -eq 'git'
        Assert ($gitRow.Status -eq 'Unsupported' -and -not $gitRow.Automatic -and $script:BatchVersionCalls -eq 0 -and -not $report.AuthenticationChecked) 'Batch-only prerequisite was treated as executable/version-ready or silently replaced.'
    }
    Test 'Actual direct ambiguous arguments refuse while quoted colon and modern implicit File remain literal' {
        $fixtureRoot=Join-Path $TestRoot 'generated facade actual'
        $facade=Join-Path $fixtureRoot 'bin/delivery-forge.ps1'
        $counter=Join-Path $fixtureRoot 'entries.txt'
        $hostPath=(Get-Process -Id $PID).Path
        $ps51=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
        $environment=@{DF_FIXTURE_RECORD=(Join-Path $fixtureRoot 'installation.json');DF_FIXTURE_PWSH=$hostPath;DF_FIXTURE_COUNTER=$counter;DF_FIXTURE_X='controlled-fixture-value'}
        $caller=Join-Path $fixtureRoot 'A1 direct caller.ps1'
        [IO.File]::WriteAllText($caller,@'
param([string]$SelectedFacade,[string]$SelectedCase)
$ErrorActionPreference='Stop'
try {
    switch -Exact ($SelectedCase){
        'NestedLiteral' { & $SelectedFacade --tools a,b }
        'UnquotedColon' { & $SelectedFacade -foo:bar }
        'DirectTrailingColon' { $v=@('-x:'); & $SelectedFacade @v }
        'QuotedColon' { $v=@('-foo:bar'); & $SelectedFacade @v }
        default { throw 'Unknown finite fixture case.' }
    }
    exit $LASTEXITCODE
}catch{[Console]::Error.Write($_.Exception.Message);exit 91}
'@,[Text.UTF8Encoding]::new($false))
        foreach($origin in @($hostPath,$ps51)){
            foreach($selectedCase in @('NestedLiteral','UnquotedColon','DirectTrailingColon')){
                $before=@(Get-Content -LiteralPath $counter).Count
                $result=Invoke-ForgeProcess $origin @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$caller,$facade,$selectedCase) -Environment $environment -TimeoutSeconds 20
                Assert ($result.ExitCode -eq 91 -and $result.Stderr -match 'FORGE_BOOTSTRAP_AMBIGUOUS_ARGUMENT' -and @(Get-Content -LiteralPath $counter).Count -eq $before) 'Ambiguous direct input mutated or entered the original command.'
                Assert (-not(Test-Path -LiteralPath (Join-Path $fixtureRoot 'bootstrap.lock'))) 'Known ambiguous input left an entered-command barrier.'
            }
            $before=@(Get-Content -LiteralPath $counter).Count
            $quoted=Invoke-ForgeProcess $origin @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$caller,$facade,'QuotedColon') -Environment $environment -TimeoutSeconds 20
            $observed=$quoted.Stdout|ConvertFrom-Json
            $expected=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('-foo:bar'))
            Assert ($quoted.ExitCode -eq 17 -and $observed.Count -eq 1 -and $observed.Values64[0] -ceq $expected -and @(Get-Content -LiteralPath $counter).Count -eq $before+1) 'Ordinary quoted/splatted colon text failed exact literal forwarding.'
        }
        foreach($origin in @($hostPath,$ps51)){
            $before=@(Get-Content -LiteralPath $counter).Count
            $slash=Invoke-ForgeProcess $origin @('-NoProfile','-NonInteractive','/File',$facade,'-x:y','-x:') -Environment $environment -TimeoutSeconds 20
            $observed=$slash.Stdout|ConvertFrom-Json
            $expectedSlash=@('-x:y','-x:'|ForEach-Object{[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))})
            Assert ($slash.ExitCode -eq 17 -and $observed.Count -eq 2 -and ($observed.Values64|ConvertTo-Json -Compress) -ceq ($expectedSlash|ConvertTo-Json -Compress) -and @(Get-Content -LiteralPath $counter).Count -eq ($before+1)) 'Explicit slash-File failed to recover native colon-shaped argv.'
        }
        $before=@(Get-Content -LiteralPath $counter).Count
        $modern=Invoke-ForgeProcess $hostPath @('-NoProfile','-NonInteractive',$facade,'$env:DF_FIXTURE_X') -Environment $environment -TimeoutSeconds 20
        $seen=$modern.Stdout|ConvertFrom-Json
        Assert ($modern.ExitCode -eq 17 -and $seen.Count -eq 1 -and $seen.Values64[0] -ceq [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('$env:DF_FIXTURE_X')) -and @(Get-Content -LiteralPath $counter).Count -eq $before+1) 'Modern implicit File changed a literal environment-looking argument.'
        $before=@(Get-Content -LiteralPath $counter).Count
        $legacy=Invoke-ForgeProcess $ps51 @('-NoProfile','-NonInteractive',$facade,'$env:DF_FIXTURE_X') -Environment $environment -TimeoutSeconds 20
        Assert ($legacy.ExitCode -ne 0 -and $legacy.Stderr -match 'FORGE_BOOTSTRAP_AMBIGUOUS_FILE_ARGV' -and @(Get-Content -LiteralPath $counter).Count -eq $before) 'PS51 implicit Command was mistaken for a literal native File invocation.'
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
            $sameProcessAlive=$observed -and $observed.StartTime.ToUniversalTime() -eq ([DateTimeOffset]$receipt.NativeStartUtc).UtcDateTime
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
        Mock Read-ForgeInstallation {[pscustomobject]@{NativeStartReceiptSupported=$true;NativeCompletionReceiptSupported=$true;DotNetHost=(Get-Process -Id $PID).Path;ToolAssembly=$PSCommandPath;NativeLauncher=(Join-Path (Split-Path -Parent $PSScriptRoot) 'delivery-forge.ps1')}}
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
        Mock Read-ForgeInstallation {[pscustomobject]@{NativeStartReceiptSupported=$true;NativeCompletionReceiptSupported=$true;DotNetHost=(Get-Process -Id $PID).Path;ToolAssembly=$PSCommandPath;NativeLauncher=(Join-Path (Split-Path -Parent $PSScriptRoot) 'delivery-forge.ps1')}}
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
    # Reuse the preceding generated-facade fixture and its inert prerequisite
    # stubs; actual native process/receipt code remains production.
    function New-ActualFacadeCase([string]$Name,[string]$ToolText,[int]$Budget=0) {
        $state=Join-Path $TestRoot $Name
        $null=New-Item -ItemType Directory -Path (Join-Path $state 'bin'),(Join-Path $state 'bootstrap')
        $selected=Join-Path $state 'bootstrap/selected.ps1'
        $text=[IO.File]::ReadAllText((Join-Path $TestRoot 'generated facade actual/bootstrap/selected.ps1'))
        if($Budget){
            $text+=[Environment]::NewLine+@'
$script:ActualBoundedInvoke=(Get-Item Function:Invoke-ForgeCommand).ScriptBlock
function Invoke-ForgeCommand([string]$Root,[string[]]$Arguments) {
    & $script:ActualBoundedInvoke $Root $Arguments ([int]$env:DF_FIXTURE_MAXIMUM)
}
'@
        }
        [IO.File]::WriteAllText($selected,$text,[Text.UTF8Encoding]::new($false))
        $tool=Join-Path $state 'inert tool.ps1'
        [IO.File]::WriteAllText($tool,$ToolText,[Text.UTF8Encoding]::new($false))
        $hostPath=(Get-Process -Id $PID).Path
        $record=@{NativeStartReceiptSupported=$true;NativeCompletionReceiptSupported=$true;BootstrapScript=$selected;BootstrapScriptSha256=(Get-FileHash -LiteralPath $selected).Hash.ToLowerInvariant();DotNetHost=$hostPath;ToolAssembly=$tool;NativeLauncher=(Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/delivery-forge.ps1')}
        $recordPath=Join-Path $state 'installation.json'
        $record|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $recordPath -Encoding utf8NoBOM
        $facade=Join-Path $state 'bin/delivery-forge.ps1'
        [IO.File]::WriteAllText($facade,(Get-ForgeLauncherContent),[Text.UTF8Encoding]::new($false))
        $environment=@{DF_FIXTURE_RECORD=$recordPath;DF_FIXTURE_PWSH=$hostPath;DF_FIXTURE_COUNTER=(Join-Path $state 'entries.txt');DF_FIXTURE_STATE=$state;DF_FIXTURE_MAXIMUM=[string]$Budget}
        [pscustomobject]@{State=$state;Facade=$facade;Environment=$environment;HostPath=$hostPath}
    }
    Test 'Legacy completion capability refuses before original entry without source substitution' {
        $script:LegacyEntries=0
        Mock Get-ForgeBootstrapReport {[pscustomobject]@{Checks=@();AutomatableActions=@();ExitCode=0}}
        Mock Find-ForgeCommand {(Get-Process -Id $PID).Path}
        Mock Invoke-ForgeProcess {$script:LegacyEntries++;throw 'Legacy native entry is forbidden.'}
        foreach($capability in @($null,$false)){
            $script:SelectedLegacyCapability=$capability
            Mock Read-ForgeInstallation {[pscustomobject]@{NativeStartReceiptSupported=$true;NativeCompletionReceiptSupported=$script:SelectedLegacyCapability;DotNetHost=(Get-Process -Id $PID).Path;ToolAssembly=$PSCommandPath;NativeLauncher=(Join-Path (Split-Path -Parent $PSScriptRoot) 'delivery-forge.ps1')}}
            $state=Join-Path $TestRoot ('legacy-completion-'+[guid]::NewGuid().ToString('N'))
            Invoke-ForgeCommand $state @('implement')
            Assert ($script:ForgeCommandExitCode -eq 1 -and $script:LegacyEntries -eq 0) 'Legacy capability entered or was silently upgraded.'
            Assert (-not(Test-Path -LiteralPath (Join-Path $state 'bootstrap.lock'))) 'Known pre-entry legacy refusal left an entered-command lock.'
        }
    }
    Test 'Actual forwarder crash after native start retains UNKNOWN and blocks a second original' {
        $case=New-ActualFacadeCase 'actual forwarder crash' @'
[IO.File]::AppendAllText($env:DF_FIXTURE_COUNTER,'entered'+[Environment]::NewLine)
$receipt=$null
for($i=0;$i -lt 100 -and -not $receipt;$i++){
    $path=Get-ChildItem -LiteralPath (Join-Path $env:DF_FIXTURE_STATE 'bootstrap') -Recurse -Filter 'native-start.json' -File|Select-Object -First 1
    if($path){try{$receipt=Get-Content -LiteralPath $path.FullName -Raw|ConvertFrom-Json}catch{}}
    if(-not $receipt){Start-Sleep -Milliseconds 20}
}
if(-not $receipt){throw 'Actual native-start receipt was not published.'}
$launcher=Get-Process -Id $receipt.LauncherPid -ErrorAction Stop
if($launcher.Id -eq $PID -or $launcher.StartTime.ToUniversalTime() -ne ([DateTimeOffset]$receipt.LauncherStartUtc).UtcDateTime){throw 'Fixture launcher identity differs.'}
$launcher.Kill()
Start-Sleep -Seconds 3
exit 0
'@
        $result=Invoke-ForgeProcess $case.HostPath @('-NoProfile','-NonInteractive','-File',$case.Facade,'implement') -Environment $case.Environment -TimeoutSeconds 20
        Assert ($result.ExitCode -eq 125 -and (Test-Path -LiteralPath (Join-Path $case.State 'bootstrap.lock'))) 'Crashed forwarder removed the replay barrier or became an ordinary failure.'
        Assert (@(Get-Content -LiteralPath $case.Environment.DF_FIXTURE_COUNTER).Count -eq 1) 'Crash fixture did not enter exactly once.'
        $start=Get-ChildItem -LiteralPath (Join-Path $case.State 'bootstrap') -Recurse -Filter 'native-start.json' -File|Select-Object -First 1
        $receipt=Get-Content -LiteralPath $start.FullName -Raw|ConvertFrom-Json
        Assert (-not(Test-Path -LiteralPath (Join-Path $start.DirectoryName 'native-completion.json'))) 'Crashed forwarder fabricated completion.'
        $second=Invoke-ForgeProcess $case.HostPath @('-NoProfile','-NonInteractive','-File',$case.Facade,'implement') -Environment $case.Environment -TimeoutSeconds 20
        Assert ($second.ExitCode -ne 0 -and @(Get-Content -LiteralPath $case.Environment.DF_FIXTURE_COUNTER).Count -eq 1) 'A new caller replayed after forwarder crash.'
        $sameAlive=$true
        for($i=0;$i -lt 50 -and $sameAlive;$i++){
            $p=Get-Process -Id $receipt.NativePid -ErrorAction SilentlyContinue
            $sameAlive=$p -and $p.StartTime.ToUniversalTime() -eq ([DateTimeOffset]$receipt.NativeStartUtc).UtcDateTime
            if($sameAlive){Start-Sleep -Milliseconds 100}
        }
        Assert (-not $sameAlive) 'Bounded self-terminating native fixture remained alive.'
    }
    Test 'Actual PS51 facade deadline observes entered root and child while retaining UNKNOWN' {
        $case=New-ActualFacadeCase 'actual PS51 facade deadline' @'
$start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true
$start.ArgumentList.Add('-NoProfile');$start.ArgumentList.Add('-NonInteractive');$start.ArgumentList.Add('-Command');$start.ArgumentList.Add('Start-Sleep -Seconds 18')
$child=[Diagnostics.Process]::Start($start)
[IO.File]::WriteAllText((Join-Path $env:DF_FIXTURE_STATE 'child-start.json'),(@{Pid=$child.Id;StartUtc=$child.StartTime.ToUniversalTime().ToString('o')}|ConvertTo-Json -Compress))
Start-Sleep -Seconds 18
exit 0
'@ 30
        $origin=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
        $result=Invoke-ForgeProcess $origin @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$case.Facade,'implement') -Environment $case.Environment -TimeoutSeconds 45
        Assert ($result.ExitCode -eq 125 -and (Test-Path -LiteralPath (Join-Path $case.State 'bootstrap.lock'))) 'PS51 facade deadline lost UNKNOWN or replay barrier.'
        $start=Get-ChildItem -LiteralPath (Join-Path $case.State 'bootstrap') -Recurse -Filter 'native-start.json' -File|Select-Object -First 1
        Assert ($start -and (Test-Path -LiteralPath (Join-Path $case.State 'child-start.json'))) 'Deadline fired before actual root/child fixture entry.'
        $rootReceipt=Get-Content -LiteralPath $start.FullName -Raw|ConvertFrom-Json
        $childReceipt=Get-Content -LiteralPath (Join-Path $case.State 'child-start.json') -Raw|ConvertFrom-Json
        foreach($selected in @(@{Pid=$rootReceipt.NativePid;StartUtc=$rootReceipt.NativeStartUtc},@{Pid=$childReceipt.Pid;StartUtc=$childReceipt.StartUtc})){
            $sameAlive=$true
            for($i=0;$i -lt 20 -and $sameAlive;$i++){
                $p=Get-Process -Id $selected.Pid -ErrorAction SilentlyContinue
                $sameAlive=$p -and $p.StartTime.ToUniversalTime() -eq ([DateTimeOffset]$selected.StartUtc).UtcDateTime
                if($sameAlive){Start-Sleep -Milliseconds 100}
            }
            Assert (-not $sameAlive) 'This finite root/child fixture survived native deadline termination.'
        }
        # Fixture-specific observations do not certify arbitrary descendant closure.
    }

    Test 'Actual command requirement selection prevents irrelevant native probes' {
        $sdk=Join-Path $TestRoot 'scoped installed host.exe'
        [IO.File]::WriteAllText($sdk,'INERT HOST; version observations below are mocked.')
        $script:ScopedSdk=$sdk
        Mock Read-ForgeInstallation {[pscustomobject]@{NativeStartReceiptSupported=$true;NativeCompletionReceiptSupported=$true;RequiredSdkVersion='10.0.401';DotNetHost=$script:ScopedSdk;ToolAssembly='fixture.dll'}}
        Mock Find-ForgeCommand {
            param($Name)
            if($Name -notin $script:PermittedDiscovery){throw ('Unexpected irrelevant command discovery: '+$Name)}
            Join-Path $TestRoot ($Name+'.exe')
        }
        Mock Invoke-ForgeProcess {
            param($Executable,$Arguments)
            $script:ScopedCalls.Add(([IO.Path]::GetFileName($Executable)+' '+($Arguments -join ' ')))
            if($Arguments[0] -eq 'fixture.dll'){return [pscustomobject]@{ExitCode=0;Stdout='{"tool":"delivery-forge"}';Stderr=''}}
            [pscustomobject]@{ExitCode=0;Stdout='fixture version';Stderr=''}
        }
        $cases=@(@('--help'),@('--version'),@('implement','--help'),@('pr','--version'),@('resume','--help'),@('plan','--repo','root','--intent-file','intent'),@('setup','--repo','root','--repository','owner/repo'),@('capabilities'),@('resume','--run-dir','retained'))
        foreach($selectedArgs in $cases){
            $tools=@(Get-ForgeRequiredTools $selectedArgs)
            $auth=@(Get-ForgeRequiredAuthentication $selectedArgs)
            Assert ($auth.Count -eq 0) 'A diagnostic/local operation unexpectedly requires worker or GH authentication.'
            $script:PermittedDiscovery=@($tools)+@('winget')
            $script:ScopedCalls=[Collections.Generic.List[string]]::new()
            $report=Get-ForgeBootstrapReport (Join-Path $TestRoot 'scoped report') $source -RequiredTools $tools -RequiredAuthentication $auth
            Assert ($report.ExitCode -eq 0 -and -not $report.AuthenticationChecked -and $null -eq $report.AuthenticationReady) 'Local installed command was blocked or falsely authentication-certified.'
            Assert (-not(($script:ScopedCalls -join ' ') -match 'codex|claude|gh.exe|auth status|login status|--list-sdks')) 'An irrelevant native/auth/source-SDK probe entered.'
        }
    }
    Test 'GH auth-status help and exact version select only a verified non-refreshing probe' {
        $sdk=Join-Path $TestRoot 'adaptive installed host.exe'
        [IO.File]::WriteAllText($sdk,'INERT HOST; all version observations mocked.')
        $script:AdaptiveSdk=$sdk
        Mock Read-ForgeInstallation {[pscustomobject]@{NativeStartReceiptSupported=$true;NativeCompletionReceiptSupported=$true;RequiredSdkVersion='10.0.401';DotNetHost=$script:AdaptiveSdk;ToolAssembly='fixture.dll'}}
        Mock Find-ForgeCommand {param($Name) Join-Path $TestRoot ($Name+'.exe')}
        Mock Invoke-ForgeProcess {
            param($Executable,$Arguments)
            if($Arguments[0] -eq 'fixture.dll'){return [pscustomobject]@{ExitCode=0;Stdout='{"tool":"delivery-forge"}';Stderr=''}}
            $out='fixture version'
            if($Executable -like '*gh.exe' -and $Arguments[0] -eq '--version'){$out=$script:GhVersion}
            elseif($Executable -like '*gh.exe' -and $Arguments -contains '--help'){$out=$script:GhHelp}
            elseif($Executable -like '*gh.exe' -and $Arguments[0] -eq 'auth'){
                $script:GhStatusCalls.Add(($Arguments -join ' '))
                $out='private-account-canary'
            }
            [pscustomobject]@{ExitCode=0;Stdout=$out;Stderr=''}
        }
        foreach($case in @(@{Version='gh version 2.99.0';Help='  --no-refresh  Suppress refresh';Ready=$true;Flag=$true},@{Version='gh version 2.86.0 (2026-01-01)';Help='  --active  Active account';Ready=$true;Flag=$false},@{Version='gh version 2.87.0';Help='  --active  Active account';Ready=$false;Flag=$false})){
            $script:GhVersion=$case.Version;$script:GhHelp=$case.Help;$script:GhStatusCalls=[Collections.Generic.List[string]]::new()
            $report=Get-ForgeBootstrapReport (Join-Path $TestRoot 'adaptive report') $source -RequiredTools @('gh','pwsh') -RequiredAuthentication @('gh')
            $authRow=$report.Checks|Where-Object Id -eq 'gh-authentication'
            Assert (($authRow.Status -eq 'Ready') -eq $case.Ready) 'GH mutation-policy qualification disagrees with actual help/version observations.'
            if($case.Ready){
                Assert ($script:GhStatusCalls.Count -eq 1 -and (($script:GhStatusCalls[0] -match '--no-refresh') -eq $case.Flag)) 'GH auth probe omitted advertised suppression or used an unsupported flag.'
            }else{Assert ($script:GhStatusCalls.Count -eq 0) 'Unqualified GH version entered an authentication-status probe.'}
            Assert (-not(($report|ConvertTo-Json -Depth 30) -match 'private-account-canary')) 'GH account output escaped into report.'
        }
    }
    Test 'Immutable publication refuses an existing partial target and retains failed-stage evidence' {
        $case=Join-Path $TestRoot 'immutable partial';$null=New-Item -ItemType Directory -Path $case
        $target=Join-Path $case 'payload.ps1';$stale=$target+'.stage-owner-evidence'
        [IO.File]::WriteAllBytes($stale,[byte[]](1,2,3))
        $bytes=[Text.Encoding]::UTF8.GetBytes('complete selected payload')
        Write-ForgeImmutableBytes $target $bytes
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($target)) -ceq [Convert]::ToBase64String($bytes)) 'Immutable publication exposed a partial final target.'
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($stale)) -ceq [Convert]::ToBase64String([byte[]](1,2,3))) 'Prior partial stage evidence was deleted or adopted.'
        $partial=Join-Path $case 'previous partial target';[IO.File]::WriteAllBytes($partial,[byte[]](7,8))
        $refused=$false;try{Write-ForgeImmutableBytes $partial $bytes}catch{$refused=$true}
        Assert ($refused -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($partial)) -ceq [Convert]::ToBase64String([byte[]](7,8))) 'Existing partial target was overwritten or adopted.'
        # No injected disk-full write is claimed: this observes actual complete
        # publication plus preservation/refusal of existing partial evidence.
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
        $barrier=Join-Path $readonlyState 'bootstrap.lock'
        Assert (Test-Path -LiteralPath $barrier -PathType Leaf) 'UNKNOWN Check refusal lost its persistent barrier.'
        $saved=Get-Content -LiteralPath $barrier -Raw|ConvertFrom-Json
        Assert ($saved.Kind -eq 'DeliveryForgeBootstrapUnknown-v1' -and @(Get-ChildItem -LiteralPath $readonlyState -Force).Count -eq 1) 'Pre-probe refusal created installation payload instead of only UNKNOWN evidence.'
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
            Assert ($null -eq $report.AuthenticationReady -and -not $report.AuthenticationChecked -and $report.ReportComplete -eq $false) 'Held lock falsely certified authentication or a complete report.'
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
        $standard=Join-Path $env:USERPROFILE '.agents/skills/delivery-forge';$null=New-Item -ItemType Directory -Path $standard
        [IO.File]::WriteAllText((Join-Path $standard 'SKILL.md'),'Second owner fixture.')
        $duplicateRefused=$false;try {Get-ForgeSkillDestination 'codex'|Out-Null}catch{$duplicateRefused=$true}
        Assert ($duplicateRefused -and [IO.File]::ReadAllText((Join-Path $legacy 'SKILL.md')) -eq 'Owner fixture; not installer-managed.' -and [IO.File]::ReadAllText((Join-Path $standard 'SKILL.md')) -eq 'Second owner fixture.') 'Duplicate skill discovery moved or adopted owner files.'
        # Retain this unrelated owner fixture; later full-install control uses
        # its own isolated profile rather than overwriting an unmanaged skill.
    }
    Test 'Full install orchestration creates CLI selection, generated launcher and both skills without login' {
        $env:USERPROFILE=Join-Path $TestRoot 'full-install-user'
        $root = Join-Path $TestRoot ('full-install-'+[char]0x65E5); $payload = Join-Path $TestRoot 'full-source'
        $null = New-Item -ItemType Directory -Path (Join-Path $payload 'components/tool/DeliveryForge.Tool'), (Join-Path $payload 'references'), (Join-Path $payload 'docs'), (Join-Path $payload 'scripts') -Force
        [IO.File]::WriteAllText((Join-Path $payload 'global.json'), '{"sdk":{"version":"10.0.401"}}')
        [IO.File]::WriteAllText((Join-Path $payload 'components/tool/DeliveryForge.Tool/DeliveryForge.Tool.csproj'), '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>')
        [IO.File]::WriteAllText((Join-Path $payload 'SKILL.md'), "---`nname: delivery-forge`ndescription: Bootstrap test fixture`n---`nFixture only.")
        [IO.File]::WriteAllText((Join-Path $payload 'docs/test.md'), 'Fixture only.')
        [IO.File]::Copy((Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/delivery-forge.ps1'), (Join-Path $payload 'scripts/delivery-forge.ps1'))
        $script:LauncherFixtureBytes=[IO.File]::ReadAllBytes((Join-Path $payload 'scripts/delivery-forge.ps1'))
        $script:FakeDotnet = Join-Path $TestRoot 'fake-dotnet.exe'; [IO.File]::WriteAllText($script:FakeDotnet, 'INERT TEST FILE, NOT AN EXECUTABLE')
        $script:FullCommands = [Collections.Generic.List[string]]::new()
        $script:FixturePayload=$payload;$script:SimulatedDriftRoot=$null;$script:SimulatedDriftKind=$null;$script:ExplicitMismatchRef=$null
        Mock Find-ForgeCommand { param($Name) Join-Path $TestRoot ($Name + '.exe') }
        Mock Get-ForgeBootstrapReport {
            param($Root, $SelectedSource)
            [pscustomobject]@{ Checks = @('git', 'gh', 'codex', 'claude', 'pwsh' | ForEach-Object { New-ForgeCheck $_ 'Ready' 'Fixture selection only' $false }); DotNetHost = $script:FakeDotnet; ExitCode = 0; AutomatableActions = @(); UserActions = @(); InstallationReady = $true; AuthenticationReady = $true }
        }
        Mock Invoke-ForgeProcess {
            param($Executable, $Arguments, $WorkingDirectory, $TimeoutSeconds, $Environment, $LogPrefix)
            $script:FullCommands.Add(($Arguments -join ' '))
            $out = ''
            $selectedRepo=if($Arguments[0] -eq '-C'){$Arguments[1]}else{$null}
            if ($Arguments -contains 'rev-parse') {
                $out='a'*40
                if($script:ExplicitMismatchRef -and $Arguments -contains ($script:ExplicitMismatchRef+'^{commit}')){$out=$script:ExplicitMismatchRef}
                elseif($script:SimulatedDriftKind -eq 'moved' -and $selectedRepo -eq $script:SimulatedDriftRoot -and $Arguments -contains 'HEAD'){$out='b'*40}
            }
            if($Arguments -contains 'status' -and $script:SimulatedDriftKind -eq 'dirty' -and $selectedRepo -eq $script:SimulatedDriftRoot){$out=' M owner-preserved.txt'}
            if($Arguments[0] -eq 'clone'){
                $cloneRoot=$Arguments[-1]
                $null=New-Item -ItemType Directory -Path (Join-Path $cloneRoot 'references') -Force
                foreach($relative in @('global.json','components/tool/DeliveryForge.Tool/DeliveryForge.Tool.csproj','SKILL.md','docs/test.md','scripts/delivery-forge.ps1')){
                    $destination=Join-Path $cloneRoot $relative;$null=New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
                    [IO.File]::Copy((Join-Path $script:FixturePayload $relative),$destination)
                }
            }
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
        $ps51Read=Join-Path $TestRoot 'actual PS51 installation read.ps1'
        [IO.File]::WriteAllText($ps51Read,@'
param([string]$SelectedBootstrap,[string]$SelectedState)
. $SelectedBootstrap
$record=Read-ForgeInstallation $SelectedState
if(-not $record){[Console]::Error.Write('Exact installation manifest refused.');exit 1}
$u=New-Object Text.UTF8Encoding($false,$true)
[Console]::Write((@{ToolAssembly64=[Convert]::ToBase64String($u.GetBytes([string]$record.ToolAssembly));NativeCompletionReceiptSupported=$record.NativeCompletionReceiptSupported}|ConvertTo-Json -Compress))
exit 0
'@,[Text.UTF8Encoding]::new($false))
        $savedProcessInvoker=(Get-Item Function:Invoke-ForgeProcess).ScriptBlock
        try {
            Set-Item Function:script:Invoke-ForgeProcess $originals['Invoke-ForgeProcess']
            $ps51=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
            $read=Invoke-ForgeProcess $ps51 @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$ps51Read,(Join-Path (Split-Path -Parent $PSScriptRoot) 'bootstrap-delivery-forge.ps1'),$root) -TimeoutSeconds 20
            Assert ($read.ExitCode -eq 0) 'Actual PS51 failed strict UTF8 installation readback.'
            $selected=$read.Stdout|ConvertFrom-Json
            Assert ($selected.ToolAssembly64 -ceq [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$record.ToolAssembly))) 'PS51 installation read changed a non-ASCII selected path.'
            Assert ($selected.NativeCompletionReceiptSupported -eq $true) 'Current genuine launcher AST did not advertise completion-receipt support.'
        } finally {Set-Item Function:script:Invoke-ForgeProcess $savedProcessInvoker}

        $pointerPath=Join-Path $root 'installation.json'
        $currentPointer=[IO.File]::ReadAllBytes($pointerPath)
        $reportFunction=(Get-Item Function:Get-ForgeBootstrapReport).ScriptBlock
        $processFunction=(Get-Item Function:Invoke-ForgeProcess).ScriptBlock
        try {
            Set-Item Function:script:Get-ForgeBootstrapReport $originals['Get-ForgeBootstrapReport']
            foreach($legacyKind in @('missing','false')){
                $legacyRecord=Read-ForgeUtf8Text $pointerPath|ConvertFrom-Json
                if($legacyKind -eq 'missing'){$legacyRecord.PSObject.Properties.Remove('NativeCompletionReceiptSupported')}
                else{$legacyRecord.NativeCompletionReceiptSupported=$false}
                [IO.File]::WriteAllText($pointerPath,($legacyRecord|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
                Assert ($null -ne (Read-ForgeInstallation $root)) 'Legacy valid record unexpectedly became a malformed payload.'
                $script:LegacyCliProbes=0
                Mock Invoke-ForgeProcess {
                    param($Executable,$Arguments)
                    if($Arguments[0] -like '*.dll'){$script:LegacyCliProbes++;throw 'Legacy readiness must not start CLI.'}
                    [pscustomobject]@{ExitCode=0;Stdout='fixture version';Stderr=''}
                }
                $legacyReport=Get-ForgeBootstrapReport $root $payload -RequiredTools @('pwsh') -RequiredAuthentication @()
                $cliRow=$legacyReport.Checks|Where-Object Id -eq 'forge-cli'
                Assert ($legacyReport.ExitCode -eq 2 -and -not $legacyReport.InstallationReady -and $cliRow.Status -eq 'Unsupported' -and -not $cliRow.Automatic -and $script:LegacyCliProbes -eq 0) 'Legacy readiness claimed ready or proposed blind rebuild/probed native CLI.'
                [IO.File]::WriteAllBytes($pointerPath,$currentPointer)
            }
            $pinnedLauncher=[string]$record.NativeLauncher
            $launcherPreimage=[IO.File]::ReadAllBytes($pinnedLauncher)
            try {
                # A truthful hash does not substitute for the actual parameter AST.
                [IO.File]::WriteAllText($pinnedLauncher,'param([string]$NativeStartReceipt)')
                $inconsistent=Read-ForgeUtf8Text $pointerPath|ConvertFrom-Json
                $inconsistent.NativeCompletionReceiptSupported=$true
                $inconsistent.NativeLauncherSha256=(Get-FileHash -LiteralPath $pinnedLauncher).Hash.ToLowerInvariant()
                [IO.File]::WriteAllText($pointerPath,($inconsistent|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
                Assert ($null -eq (Read-ForgeInstallation $root)) 'Declared completion capability bypassed actual selected-launcher AST.'
            } finally {
                [IO.File]::WriteAllBytes($pinnedLauncher,$launcherPreimage)
                [IO.File]::WriteAllBytes($pointerPath,$currentPointer)
            }
        } finally {
            [IO.File]::WriteAllBytes($pointerPath,$currentPointer)
            Set-Item Function:script:Get-ForgeBootstrapReport $reportFunction
            Set-Item Function:script:Invoke-ForgeProcess $processFunction
        }

        Assert (Test-Path -LiteralPath $record.BootstrapScript) 'Immutable shared bootstrap not copied.'
        Assert ((Get-Content -LiteralPath $record.BootstrapScript -Raw) -match 'function Invoke-ForgeBootstrapInstall') 'Wrong source copied as bootstrap.'
        Assert ((Get-Content -LiteralPath (Join-Path $root 'bin/delivery-forge.ps1') -Raw) -match 'Invoke-ForgeCommand') 'Generated launcher missing.'
        foreach ($location in @('.agents', '.claude')) { Assert (Test-ForgeSkill (Join-Path $env:USERPROFILE ($location + '/skills/delivery-forge'))) ('Skill missing: ' + $location) }
        Assert (-not (($script:FullCommands -join "`n") -match 'auth login|login --')) 'Install attempted authentication.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'bootstrap.lock'))) 'Settled fixture kept its lock.'
        $priorAssembly=$record.ToolAssembly
        $priorToolInstalls=@($script:FullCommands|Where-Object {$_ -like 'tool install *'}).Count
        $codexSkill=Get-ForgeSkillDestination 'codex'
        [IO.File]::Delete((Join-Path $codexSkill 'docs/test.md'))
        Assert ((Get-ForgeSkillStatus $codexSkill) -eq 'Repairable') 'Fixture did not create a genuine managed missing-file repair.'
        $reused=Invoke-ForgeBootstrapInstall $root $payload 'HEAD' 120 -RequiredAuthentication @()
        Assert ($reused.ExitCode -eq 0) 'Same-commit whole-orchestration reuse/repair failed.'
        $record=Read-ForgeInstallation $root
        Assert ($record.ToolAssembly -eq $priorAssembly -and @($script:FullCommands|Where-Object {$_ -like 'tool install *'}).Count -eq $priorToolInstalls) 'Same-commit repair silently reinstalled the CLI payload.'
        Assert (Test-ForgeSkill $codexSkill) 'Whole orchestration failed to repair the managed missing file.'
        $backups=@(Get-ChildItem -LiteralPath (Join-Path $root 'bootstrap/skill-backups') -Directory)
        Assert ($backups.Count -ge 2) 'Whole orchestration failed to preserve prior discovery payloads.'
        $activationPlanPath=Join-Path $reused.AttemptDirectory 'activation-plan.json'
        $activationPlan=Read-ForgeUtf8Text $activationPlanPath|ConvertFrom-Json
        Assert ($activationPlan.Kind -eq 'DeliveryForgeBootstrapActivationPlan-v1' -and $activationPlan.SourceCommit -eq $record.SourceCommit -and @($activationPlan.Skills).Count -eq 2) 'Durable activation plan did not bind the actual selected source and two skills.'
        foreach($skillMove in $activationPlan.Skills){
            Assert ($skillMove.Destination -eq (Get-ForgeSkillDestination $skillMove.Agent) -and $skillMove.OldExists -and (Test-Path -LiteralPath $skillMove.Backup -PathType Container)) 'Durable plan did not map the selected skill to its actual prior backup.'
            Assert ((Get-FileHash -LiteralPath (Join-Path $skillMove.Backup '.forge-bootstrap-skill.json')).Hash.ToLowerInvariant() -ceq $skillMove.OldMarker) 'Actual skill backup differs from its planned old marker.'
            Assert ((Get-FileHash -LiteralPath (Join-Path $skillMove.Destination '.forge-bootstrap-skill.json')).Hash.ToLowerInvariant() -ceq $skillMove.NewMarker) 'Selected new skill differs from its durable planned marker.'
            Assert (-not(Test-Path -LiteralPath $skillMove.Candidate)) 'Success left the planned new skill candidate unmoved.'
        }
        foreach($role in @($activationPlan.Facade,$activationPlan.Pointer)){
            Assert ((Get-FileHash -LiteralPath $role.Path).Hash.ToLowerInvariant() -ceq $role.NewSha256) 'Selected facade/pointer is not its planned new bytes.'
            Assert ((Get-FileHash -LiteralPath $role.Backup).Hash.ToLowerInvariant() -ceq $role.OldSha256) 'Durable facade/pointer backup mapping does not match actual previous bytes.'
        }

        # Exercise known-no-effect failures after real candidate directory moves,
        # while keeping the source/SDK/Git calls in this existing mocked fixture.
        foreach($faultKind in @('old-skill','facade')){
            $script:InstallFaultKind=$faultKind;$script:InstallMoveCount=0;$script:FacadeReadHold=$null
            $script:InstallFacade=Join-Path $root 'bin/delivery-forge.ps1'
            $selectedFiles=@((Join-Path $root 'installation.json'),$script:InstallFacade,(Join-Path (Get-ForgeSkillDestination 'codex') '.forge-bootstrap-skill.json'),(Join-Path (Get-ForgeSkillDestination 'claude') '.forge-bootstrap-skill.json'))
            $beforeSelection=@{};foreach($path in $selectedFiles){$beforeSelection[$path]=(Get-FileHash -LiteralPath $path).Hash}
            Mock Move-ForgeInstallationDirectory {
                param($From,$To)
                $script:InstallMoveCount++
                if($script:InstallFaultKind -eq 'old-skill' -and $script:InstallMoveCount -eq 3){throw 'Injected ordinary no-effect old-skill move failure.'}
                & $originals['Move-ForgeInstallationDirectory'] $From $To
                if($script:InstallFaultKind -eq 'facade' -and $script:InstallMoveCount -eq 4){
                    # Permit exact preimage reads but deny replacement/delete.
                    $script:FacadeReadHold=[IO.File]::Open($script:InstallFacade,'Open','Read','Read')
                }
            }
            try {
                $selected=Read-ForgeInstallation $root
                $failed=Invoke-ForgeBootstrapInstall $root $selected.SourceRoot 'HEAD' 120 -SourceSelectionExplicit -RequiredAuthentication @()
                Assert ($failed.ExitCode -eq 1 -and -not $script:ForgeBootstrapUnsettled) 'Known-no-effect activation failure became UNKNOWN or succeeded.'
            } finally {
                if($script:FacadeReadHold){$script:FacadeReadHold.Dispose()}
                Set-Item Function:script:Move-ForgeInstallationDirectory $originals['Move-ForgeInstallationDirectory']
            }
            foreach($path in $selectedFiles){Assert ((Get-FileHash -LiteralPath $path).Hash -ceq $beforeSelection[$path]) 'Known rollback changed an exact previous pointer/facade/skill marker.'}
            foreach($agent in @('codex','claude')){Assert (Test-ForgeSkill (Get-ForgeSkillDestination $agent)) 'Known rollback lost previous complete skill payload.'}
            Assert (-not(Test-Path -LiteralPath (Join-Path $root 'bootstrap.lock'))) 'Known rollback retained an unresolved-operation lock.'
        }
        # Inject actual owner-file edits after the existing fixture has consumed
        # source, while Git/SDK observations retain their existing mocks.
        foreach($mutationKind in @('source','facade','pointer')){
            $selected=Read-ForgeInstallation $root
            $script:BMutationKind=$mutationKind;$script:BMutationDone=$false;$script:BActivationMoves=0
            $script:BSourceFile=Join-Path $selected.SourceRoot 'SKILL.md'
            $script:BFacadeFile=Join-Path $root 'bin/delivery-forge.ps1'
            $script:BPointerFile=Join-Path $root 'installation.json'
            $observedFiles=@($script:BSourceFile,$script:BFacadeFile,$script:BPointerFile)
            $savedBytes=@{};foreach($path in $observedFiles){$savedBytes[$path]=[IO.File]::ReadAllBytes($path)}
            $skillMarkers=@((Join-Path (Get-ForgeSkillDestination 'codex') '.forge-bootstrap-skill.json'),(Join-Path (Get-ForgeSkillDestination 'claude') '.forge-bootstrap-skill.json'))
            $savedMarkers=@{};foreach($path in $skillMarkers){$savedMarkers[$path]=(Get-FileHash -LiteralPath $path).Hash}
            $script:SimulatedDriftRoot=[string]$selected.SourceRoot;$script:SimulatedDriftKind=$null
            Mock Move-ForgeInstallationDirectory {
                param($From,$To)
                $script:BActivationMoves++
                & $originals['Move-ForgeInstallationDirectory'] $From $To
            }
            Mock Install-ForgeSkill {
                param($SelectedSource,$Destination,$Launcher,$SourceCommit)
                & $originals['Install-ForgeSkill'] $SelectedSource $Destination $Launcher $SourceCommit
                if(-not $script:BMutationDone){
                    $script:BMutationDone=$true
                    switch($script:BMutationKind){
                        'source' {[IO.File]::AppendAllText($script:BSourceFile,"`nOwner source edit after consumption.");$script:SimulatedDriftKind='dirty'}
                        'facade' {[IO.File]::AppendAllText($script:BFacadeFile,"`n# Owner facade edit during installation.")}
                        'pointer' {[IO.File]::AppendAllText($script:BPointerFile,"`n ")}
                    }
                }
            }
            try {
                $failed=Invoke-ForgeBootstrapInstall $root $selected.SourceRoot 'HEAD' 120 -SourceSelectionExplicit -RequiredAuthentication @()
                Assert ($failed.ExitCode -eq 1 -and $script:BMutationDone -and $script:BActivationMoves -eq 0 -and -not $script:ForgeBootstrapUnsettled) 'Post-consumption/preactivation mutation was activated or incorrectly classified.'
                $changedPath=switch($mutationKind){'source'{$script:BSourceFile}'facade'{$script:BFacadeFile}'pointer'{$script:BPointerFile}}
                Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($changedPath)) -cne [Convert]::ToBase64String($savedBytes[$changedPath])) 'Installation removed an owner edit from its selected path.'
                foreach($path in $skillMarkers){Assert ((Get-FileHash -LiteralPath $path).Hash -ceq $savedMarkers[$path]) 'Refused mutation still displaced an old skill.'}
                Assert (-not(Test-Path -LiteralPath (Join-Path $root 'bootstrap.lock'))) 'Known preactivation refusal left UNKNOWN.'
            } finally {
                # Explicit fixture restoration only; the installer must not do it.
                foreach($path in $observedFiles){[IO.File]::WriteAllBytes($path,$savedBytes[$path])}
                $script:SimulatedDriftRoot=$null;$script:SimulatedDriftKind=$null
                Set-Item Function:script:Install-ForgeSkill $originals['Install-ForgeSkill']
                Set-Item Function:script:Move-ForgeInstallationDirectory $originals['Move-ForgeInstallationDirectory']
            }
        }

        foreach($driftKind in @('dirty','moved')){
            $selected=Read-ForgeInstallation $root
            $script:SimulatedDriftRoot=[string]$selected.SourceRoot;$script:SimulatedDriftKind=$driftKind
            $ownerFile=Join-Path $script:SimulatedDriftRoot 'owner-preserved.txt'
            [IO.File]::WriteAllText($ownerFile,'retained source belongs to owner')
            $ownerHash=(Get-FileHash -LiteralPath $ownerFile).Hash
            $beforeCommands=$script:FullCommands.Count
            $repaired=Invoke-ForgeBootstrapInstall $root $null ('a'*40) 120 -RequiredAuthentication @()
            Assert ($repaired.ExitCode -eq 0) 'Automatic known dirty/moved source did not use exact-pin fresh clone.'
            $record=Read-ForgeInstallation $root
            Assert ($record.SourceRoot -ne $script:SimulatedDriftRoot -and $record.SourceCommit -eq ('a'*40) -and $record.ToolAssembly -eq $priorAssembly) 'Same-commit repair failed to refresh source selection without payload reinstall.'
            Assert ((Get-FileHash -LiteralPath $ownerFile).Hash -ceq $ownerHash) 'Automatic repair altered retained owner source.'
            $entered=@($script:FullCommands|Select-Object -Skip $beforeCommands)
            Assert (@($entered|Where-Object {$_ -like 'clone *'}).Count -eq 1 -and @($entered|Where-Object {$_ -like ('*checkout --detach '+('a'*40))}).Count -eq 1) 'Fresh fallback did not clone/check out exactly the recorded commit.'
            Assert (-not(($entered -join ' ') -match '(?:^|\s)(?:reset|clean|stash)(?:\s|$)')) 'Automatic repair reset/cleaned/stashed retained source.'
            $script:SimulatedDriftRoot=$null;$script:SimulatedDriftKind=$null
        }
        $selected=Read-ForgeInstallation $root
        $beforePointer=(Get-FileHash -LiteralPath (Join-Path $root 'installation.json')).Hash
        $script:ExplicitMismatchRef='b'*40
        $mismatched=Invoke-ForgeBootstrapInstall $root $selected.SourceRoot $script:ExplicitMismatchRef 120 -SourceSelectionExplicit -RequiredAuthentication @()
        $script:ExplicitMismatchRef=$null
        Assert ($mismatched.ExitCode -eq 1 -and (Get-FileHash -LiteralPath (Join-Path $root 'installation.json')).Hash -ceq $beforePointer) 'Explicit source/ref mismatch changed the selected installation.'
        Assert (-not(Test-Path -LiteralPath (Join-Path $root 'bootstrap.lock'))) 'Known explicit ref mismatch was misclassified as UNKNOWN.'

        # This is last: preserve an intentionally unresolved activation instead
        # of deleting its barrier or silently restoring its mixed state.
        $selected=Read-ForgeInstallation $root
        $script:BPointerFile=Join-Path $root 'installation.json'
        $script:BDoubleFaultMoves=0;$script:BPointerHashes=0;$script:BHashFaultObserved=$false;$script:BPointerHold=$null;$script:BPointerArmed=$false
        $script:BHashCommand=Get-Command Get-FileHash
        $priorHashFunction=if(Test-Path Function:Get-FileHash){(Get-Item Function:Get-FileHash).ScriptBlock}else{$null}
        Mock Get-FileHash {
            param([string]$LiteralPath,[string]$Algorithm='SHA256')
            if($script:BPointerArmed -and [string]::Equals($LiteralPath,$script:BPointerFile,[StringComparison]::OrdinalIgnoreCase)){
                $script:BPointerHashes++
                if($script:BPointerHashes -ge 2){$script:BHashFaultObserved=$true;throw 'Injected classification hash failure after pointer replacement refused.'}
            }
            & $script:BHashCommand -LiteralPath $LiteralPath -Algorithm $Algorithm
        }
        Mock Move-ForgeInstallationDirectory {
            param($From,$To)
            $script:BDoubleFaultMoves++
            & $originals['Move-ForgeInstallationDirectory'] $From $To
            if($script:BDoubleFaultMoves -eq 4){
                # Exact preimage read is allowed, but File.Replace is denied.
                $script:BPointerHold=[IO.File]::Open($script:BPointerFile,'Open','Read','Read')
                $script:BPointerArmed=$true
            }
        }
        try {
            $unknown=Invoke-ForgeBootstrapInstall $root $selected.SourceRoot 'HEAD' 120 -SourceSelectionExplicit -RequiredAuthentication @()
        } finally {
            if($script:BPointerHold){$script:BPointerHold.Dispose()}
            if($priorHashFunction){Set-Item Function:script:Get-FileHash $priorHashFunction}else{Remove-Item Function:script:Get-FileHash}
            Set-Item Function:script:Move-ForgeInstallationDirectory $originals['Move-ForgeInstallationDirectory']
            $script:BPointerArmed=$false
        }
        Assert ($unknown.ExitCode -eq 125 -and $script:ForgeBootstrapUnsettled -and $script:BHashFaultObserved -and $script:BDoubleFaultMoves -eq 4) 'Pointer publication/classification double fault became ordinary failure or destructively rolled back.'
        $barrierPath=Join-Path $root 'bootstrap.lock'
        Assert (Test-Path -LiteralPath $barrierPath) 'Unresolved pointer disposition lost its replay barrier.'
        $barrier=Read-ForgeUtf8Text $barrierPath|ConvertFrom-Json
        $unknownPlanPath=Join-Path $unknown.AttemptDirectory 'activation-plan.json'
        Assert ($barrier.Kind -eq 'DeliveryForgeBootstrapActivation-v1' -and $barrier.AttemptDirectory -eq $unknown.AttemptDirectory -and $barrier.ActivationPlan -eq $unknownPlanPath) 'Retained barrier does not point to its durable activation evidence.'
        $unknownPlan=Read-ForgeUtf8Text $unknownPlanPath|ConvertFrom-Json
        foreach($skillMove in $unknownPlan.Skills){
            Assert ((Get-FileHash -LiteralPath (Join-Path $skillMove.Backup '.forge-bootstrap-skill.json')).Hash.ToLowerInvariant() -ceq $skillMove.OldMarker -and (Get-FileHash -LiteralPath (Join-Path $skillMove.Destination '.forge-bootstrap-skill.json')).Hash.ToLowerInvariant() -ceq $skillMove.NewMarker) 'UNKNOWN activation lost its exact old/new skill mapping.'
        }

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
