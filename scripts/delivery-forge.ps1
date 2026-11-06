#requires -Version 7.0
# Selected installation launcher. No PATH, authentication or persistent settings change.
[CmdletBinding(PositionalBinding=$false)]
param(
    [Parameter(Mandatory)][string]$DotNetHost,
    [Parameter(Mandatory)][string]$ToolAssembly,
    [ValidateRange(1,1200)][int]$MaximumSeconds = 1200,
    [string]$NativeStartReceipt,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$ForgeArguments
)
$ErrorActionPreference='Stop'
$clock=[Diagnostics.Stopwatch]::StartNew()
$maximumMilliseconds=[long]$MaximumSeconds*1000
$strictUtf8=[Text.UTF8Encoding]::new($false,$true)
foreach($selected in @($DotNetHost,$ToolAssembly)) {
    $null=$strictUtf8.GetByteCount($selected)
    if(-not [IO.Path]::IsPathFullyQualified($selected) -or -not [IO.File]::Exists($selected)) { throw 'Select the existing absolute installed runtime and tool assembly.' }
}
if($null -eq $ForgeArguments) { $ForgeArguments=[string[]]@() }
foreach($argument in $ForgeArguments) { if($null -eq $argument) { throw 'Null is not a native argument.' };$null=$strictUtf8.GetByteCount($argument) }
if($NativeStartReceipt) {
    $null=$strictUtf8.GetByteCount($NativeStartReceipt)
    if(-not [IO.Path]::IsPathFullyQualified($NativeStartReceipt) -or [IO.File]::Exists($NativeStartReceipt) -or -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($NativeStartReceipt))) { throw 'Select a fresh absolute native-start receipt in an existing directory.' }
    $cursor=$NativeStartReceipt
    while($cursor) { if(Test-Path -LiteralPath $cursor) { if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Native-start receipt path is redirected.' } };$cursor=[IO.Path]::GetDirectoryName($cursor) }
}
$settings=@{
    DOTNET_PROCESSOR_COUNT='2'
    DOTNET_GCHeapHardLimit='0x100000000'
    DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1'
    MSBUILDDISABLENODEREUSE='1'
}
$previous=@{}
foreach($key in $settings.Keys) { $previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process') }
$exitCode=1;$process=$null;$entered=$false;$nativePid=$null;$nativeStart='unobserved';$unresolved=$false
try {
    foreach($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key,$settings[$key],'Process') }
    $start=[Diagnostics.ProcessStartInfo]::new($DotNetHost)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.ArgumentList.Add($ToolAssembly)
    foreach($argument in $ForgeArguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    if(-not $process.Start()) { throw 'The selected native tool did not start.' }
    $entered=$true;$nativePid=$process.Id;$nativeStart=$process.StartTime.ToUniversalTime().ToString('o')
    # No argv, account, task text or credentials are recorded.
    if($NativeStartReceipt) {
        $receipt=@{ Kind='DeliveryForgeNativeStart-v1';NativePid=$nativePid;NativeStartUtc=$nativeStart;LauncherPid=$PID;LauncherStartUtc=(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o') }|ConvertTo-Json -Compress
        $bytes=$strictUtf8.GetBytes($receipt)
        $file=[IO.File]::Open($NativeStartReceipt,'CreateNew','Write','None')
        try { $file.Write($bytes,0,$bytes.Length);$file.Flush($true) } finally { $file.Dispose() }
    }
    $stdout=$process.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput())
    $stderr=$process.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
    while(-not $process.WaitForExit(100)) {
        if($stdout.IsFaulted -or $stderr.IsFaulted -or $clock.ElapsedMilliseconds -ge $maximumMilliseconds) { throw 'Native deadline or output forwarding failed.' }
    }
    $drainMilliseconds=[int][Math]::Max(0,[Math]::Min(60000,$maximumMilliseconds-$clock.ElapsedMilliseconds))
    if(-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),$drainMilliseconds)) { throw 'Native output EOF was not observed.' }
    $exitCode=$process.ExitCode
} catch {
    if($entered) {
        $exitCode=125;$unresolved=$true;$killAttempted=$false
        try { if(-not $process.HasExited) { $killAttempted=$true;$process.Kill($true) } } catch { }
        try { [Console]::Error.WriteLine(('DFCLI_LAUNCHER_UNKNOWN: pid={0}; startUtc={1}; killAttempted={2}; receipt={3}. Native process/output completion is unverified; best-effort termination does not prove tree closure. Preserve this attempt; do not retry automatically.' -f $nativePid,$nativeStart,$killAttempted,$NativeStartReceipt)) } catch { }
    } else { try { [Console]::Error.WriteLine($_.Exception.Message) } catch { } }
} finally {
    if($process) { try { $process.Dispose() } catch { } }
    foreach($key in $settings.Keys) {
        try { [Environment]::SetEnvironmentVariable($key,$previous[$key],'Process') }
        catch { if($entered) { $exitCode=125 };try { [Console]::Error.WriteLine('Native launcher environment restoration failed; preserve the attempt.') } catch { } }
    }
}
exit $exitCode
