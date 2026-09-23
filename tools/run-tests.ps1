# Runs the regression suites.
#
#   tools\run-tests.ps1                 everything
#   tools\run-tests.ps1 -Offline        skip the suites that need the internet
#   tools\run-tests.ps1 loader mods     only suites whose name contains these
#   tools\run-tests.ps1 -List           names only
#   tools\run-tests.ps1 -ShowAll        a line per check even when piped
#   tools\run-tests.ps1 -Quiet          failures and the summary only
#
# Run in a terminal it prints a line per check, as it always has. Piped into
# anything — a file, a log, a tool that captures output — it prints only failures
# and the summary, because several hundred "ok" lines carry nothing the final
# count does not. -ShowAll and -Quiet force it either way.
#
# Exit code is 0 when everything passed, so this drops straight into anything
# that checks one.

param(
    [switch]$Offline,
    [switch]$List,
    [switch]$ShowAll,
    [switch]$Quiet,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Suites
)

$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'MinecraftLauncher.Tests'

$arguments = @()
if ($List) { $arguments += '--list' }
if ($Offline) { $arguments += '--offline' }
if ($ShowAll) { $arguments += '--verbose' }
if ($Quiet) { $arguments += '--quiet' }
if ($Suites) { $arguments += $Suites }

dotnet run --project $project --nologo -v q -- @arguments
exit $LASTEXITCODE
