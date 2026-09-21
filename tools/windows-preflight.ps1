#requires -Version 5.1
<#
.SYNOPSIS
  Run the same read-only readiness checks as the installed GameDirector product.
.DESCRIPTION
  The native CLI owns media resolution and capability validation. This wrapper
  intentionally does not duplicate installation paths, tool requirements or ports.
  Pass -Gd with the gd.exe from an extracted distribution when it is not on PATH.
#>
[CmdletBinding()]
param(
    [string]$Gd = 'gd.exe',
    [string]$Endpoint
)
$ErrorActionPreference = 'Stop'
try {
    $application = Get-Command $Gd -CommandType Application -ErrorAction Stop
    $doctorArguments = @('doctor')
    if ($Endpoint) { $doctorArguments += @('--endpoint', $Endpoint) }
    & $application.Source @doctorArguments
    exit $LASTEXITCODE
} catch {
    Write-Error "Cannot run GameDirector diagnostics. Pass -Gd with the full path to the packaged gd.exe. $($_.Exception.Message)"
    exit 1
}
