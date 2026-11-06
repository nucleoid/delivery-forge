#requires -Version 7.0
# Selected installation launcher. No PATH, authentication or persistent settings change.
[CmdletBinding(PositionalBinding=$false)]
param(
    [Parameter(Mandatory)][string]$DotNetHost,
    [Parameter(Mandatory)][string]$ToolAssembly,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$ForgeArguments
)
$ErrorActionPreference='Stop'
$clock=[Diagnostics.Stopwatch]::StartNew()
foreach($selected in @($DotNetHost,$ToolAssembly)) {
    if(-not [IO.Path]::IsPathFullyQualified($selected) -or -not [IO.File]::Exists($selected)) { throw 'Select the existing absolute installed runtime and tool assembly.' }
}
$settings=@{
    DOTNET_PROCESSOR_COUNT='2'
    DOTNET_GCHeapHardLimit='0x100000000'
    DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1'
    MSBUILDDISABLENODEREUSE='1'
}
$previous=@{}
foreach($key in $settings.Keys) { $previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process') }
$exitCode=1
try {
    foreach($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key,$settings[$key],'Process') }
    # Observe the actual process directly. PowerShell native-command forwarding
    # can return without LASTEXITCODE in a redirected/noninteractive host.
    $start=[Diagnostics.ProcessStartInfo]::new($DotNetHost)
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.ArgumentList.Add($ToolAssembly)
    foreach($argument in $ForgeArguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=$start
    if(-not $process.Start()) { throw 'The selected native tool did not start.' }
    $stdout=$process.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput())
    $stderr=$process.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
    $unresolved=$false
    while(-not $process.WaitForExit(100)) {
        if($stdout.IsFaulted -or $stderr.IsFaulted -or $clock.ElapsedMilliseconds -ge 1200000) { $unresolved=$true; break }
    }
    if(-not $unresolved) {
        $drainMilliseconds=[int][Math]::Max(0,[Math]::Min(60000,1200000-$clock.ElapsedMilliseconds))
        try { $unresolved=-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),$drainMilliseconds) }
        catch { $unresolved=$true }
    }
    if($unresolved) {
        [Console]::Error.WriteLine('DFCLI_LAUNCHER_UNKNOWN: native process/output completion is unverified; preserve this attempt and do not retry automatically.')
        $exitCode=125
    } else {
        $exitCode=$process.ExitCode
        $process.Dispose()
    }
} finally {
    foreach($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key],'Process') }
}
exit $exitCode
