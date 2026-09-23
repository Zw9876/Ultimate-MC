# Runs the regression suites.
#
#   tools\run-tests.ps1                 everything
#   tools\run-tests.ps1 -Offline        skip the suites that need the internet
#   tools\run-tests.ps1 loader mods     only suites whose name contains these
#   tools\run-tests.ps1 -List           names only
#
# Exit code is 0 when everything passed, so this drops straight into anything
# that checks one.

param(
    [switch]$Offline,
    [switch]$List,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Suites
)

$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'MinecraftLauncher.Tests'

$arguments = @()
if ($List) { $arguments += '--list' }
if ($Offline) { $arguments += '--offline' }
if ($Suites) { $arguments += $Suites }

dotnet run --project $project --nologo -v q -- @arguments
exit $LASTEXITCODE
