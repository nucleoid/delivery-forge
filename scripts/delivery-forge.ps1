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
    & $DotNetHost $ToolAssembly @ForgeArguments
    $exitCode=$LASTEXITCODE
} finally {
    foreach($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key],'Process') }
}
exit $exitCode
