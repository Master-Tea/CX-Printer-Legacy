[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration='Release'
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio 2022 Build Tools with .NET desktop development is required.' }
$installation=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
if (!$installation) { throw 'MSBuild was not found. Install Visual Studio 2022 Build Tools.' }
$msbuild=Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
$out=Join-Path $repo 'artifacts\CX-Printer-win7-x64'
$bridge=Join-Path $out 'Bridge'
New-Item -ItemType Directory -Path $bridge -Force | Out-Null

function Publish([string]$project,[string]$destination) {
    & $msbuild $project '/restore' '/t:Publish' '/nologo' '/verbosity:minimal' "/p:Configuration=$Configuration" '/p:Platform=x64' '/p:RuntimeIdentifier=win-x64' '/p:SelfContained=true' '/p:PublishSingleFile=false' '/p:PublishTrimmed=false' '/p:PublishReadyToRun=false' "/p:PublishDir=$destination\"
    if ($LASTEXITCODE -ne 0) { throw "MSBuild publish failed: $project" }
}

Publish (Join-Path $repo 'src\CxPrint.Service\CxPrint.Service.csproj') $bridge
Publish (Join-Path $repo 'src\CxPrint.Win7\CxPrint.Win7.csproj') $out

# Exercise the actual published WinForms entry point before producing a distributable archive.
$testInfo=New-Object System.Diagnostics.ProcessStartInfo
$testInfo.FileName=Join-Path $out 'CX Printer.exe'
$testInfo.Arguments='--self-test'
$testInfo.UseShellExecute=$false
$testProcess=[System.Diagnostics.Process]::Start($testInfo)
if (!$testProcess.WaitForExit(30000)) {
    $testProcess.Kill()
    throw 'Published UI startup self-test timed out.'
}
if ($testProcess.ExitCode -ne 0) { throw 'Published UI startup self-test failed. See CX-Printer-Legacy-error.log in the publish directory.' }
$testReport=Join-Path $out 'startup-self-test.txt'
if (!(Test-Path -LiteralPath $testReport)) { throw 'Published UI startup self-test report is missing.' }
Write-Output (Get-Content -LiteralPath $testReport -Raw -Encoding UTF8)
Remove-Item -LiteralPath $testReport -Force

Copy-Item (Join-Path $repo 'README.md') $out -Force
Copy-Item (Join-Path $repo 'THIRD_PARTY_NOTICES.md') $out -Force
$licenseSource=Join-Path $repo 'third_party\licenses'
$licenseDestination=Join-Path $out 'Licenses'
if (Test-Path -LiteralPath $licenseDestination) { Remove-Item -LiteralPath $licenseDestination -Recurse -Force }
New-Item -ItemType Directory -Path $licenseDestination -Force | Out-Null
Copy-Item (Join-Path $licenseSource '*') $licenseDestination -Recurse -Force
$zip=Join-Path (Split-Path $out -Parent) 'CX-Printer-Legacy-win7-x64.zip'
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
Write-Output "Published Windows 7 x64 package: $out"
Write-Output "Archive: $zip"
