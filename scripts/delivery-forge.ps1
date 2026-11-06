# Selected installation launcher. No PATH, authentication or persistent settings change.
[CmdletBinding(PositionalBinding=$false)]
param(
    [Parameter(Mandatory)][string]$DotNetHost,
    [Parameter(Mandatory)][string]$ToolAssembly,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$ForgeArguments
)
$ErrorActionPreference='Stop'
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
    $start.ArgumentList.Add($ToolAssembly)
    foreach($argument in $ForgeArguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=$start
    if(-not $process.Start()) { throw 'The selected native tool did not start.' }
    $process.WaitForExit()
    $exitCode=$process.ExitCode
    $process.Dispose()
} finally {
    foreach($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key],'Process') }
}
exit $exitCode
