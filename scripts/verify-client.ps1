[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [switch]$ExpectRestricted
)
$ErrorActionPreference='Stop'
$executable=Join-Path ([IO.Path]::GetFullPath($Directory)) 'CX Printer.exe'
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Published client executable was not found.' }
$started=Get-Date
$info=New-Object Diagnostics.ProcessStartInfo
$info.FileName=$executable
$info.Arguments='--diagnose-service'
# Run the actual executable and verify its effective token, rather than substituting a PowerShell pipe client.
$info.UseShellExecute=$false
$info.WorkingDirectory=Split-Path $executable -Parent
$process=[Diagnostics.Process]::Start($info)
if (!$process.WaitForExit(30000)) { throw "Client diagnostic remains active (PID $($process.Id)); no repeated launch was attempted." }
$candidates=@(
    (Join-Path $env:LOCALAPPDATA 'CX Printer Legacy\Logs\service-diagnostics.json'),
    (Join-Path $info.WorkingDirectory 'service-diagnostics.json'),
    (Join-Path ([IO.Path]::GetTempPath()) 'CX Printer Legacy\Logs\service-diagnostics.json')
)
$reportFile=$candidates | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | ForEach-Object {Get-Item -LiteralPath $_} |
    Where-Object {$_.LastWriteTime -ge $started.AddSeconds(-2)} | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (!$reportFile) { throw 'Client did not produce a fresh diagnostic report.' }
$report=Get-Content -LiteralPath $reportFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
if ($report.environment.executable -ne $executable) { throw 'Diagnostic report belongs to another executable.' }
if ($ExpectRestricted) {
    if ($process.ExitCode -ne 2 -or $report.environment.integrityLevel -ge 8192 -or !$report.startupProblem -or !$report.logWritable) {
        throw 'Restricted launch was not diagnosed correctly, or fallback logging failed.'
    }
} else {
    if ($process.ExitCode -ne 0 -or !$report.connected -or !$report.logWritable -or $report.environment.integrityLevel -lt 8192) {
        throw ('Regular-user client check failed. Read '+$reportFile.FullName)
    }
    if (@($report.checks).Count -ne 3 -or @($report.checks | Where-Object {!$_.connected}).Count) { throw 'Repeated UI connections failed.' }
}
"PASS client launch: exit=$($process.ExitCode), integrity=$($report.environment.integrityLevel), elevated=$($report.environment.elevated), connected=$($report.connected), log=$($report.logPath)"
"Report: $($reportFile.FullName)"
$report.checks | Select-Object connected,elapsedMs,serviceVersion,printerCount | Format-Table -AutoSize
