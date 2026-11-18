[CmdletBinding()]
param([switch]$Verify, [switch]$Probe)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The inbox 64-bit .NET Framework compiler was not found.'
}

$source = Join-Path $PSScriptRoot 'src'
$out = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$common = @('/nologo', '/utf8output', '/codepage:65001', '/platform:x64', '/unsafe+', '/optimize+', '/warnaserror+')
$ras = Join-Path $source 'Ras.cs'
$schedule = Join-Path $source 'RetrySchedule.cs'
$app = Join-Path $out 'VpnWatch.exe'
& $compiler @common '/target:winexe' "/out:$app" '/reference:System.Windows.Forms.dll,System.Drawing.dll,System.Core.dll' $ras $schedule (Join-Path $source 'AssemblyInfo.cs') (Join-Path $source 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw "App compilation failed (exit $LASTEXITCODE)." }

if ($Verify -or $Probe) {
    $scratch = Join-Path $PSScriptRoot '.tmp'
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $testExe = Join-Path $scratch ('VpnWatch.Tests.' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        & $compiler @common '/target:exe' "/out:$testExe" '/reference:System.Core.dll' $ras $schedule (Join-Path $source 'Tests.cs')
        if ($LASTEXITCODE -ne 0) { throw "Test compilation failed (exit $LASTEXITCODE)." }
        if ($Probe) { & $testExe --probe } else { & $testExe }
        if ($LASTEXITCODE -ne 0) { throw "Verification failed (exit $LASTEXITCODE)." }
    }
    finally { if (Test-Path -LiteralPath $testExe -PathType Leaf) { Remove-Item -LiteralPath $testExe } }
}
Get-Item -LiteralPath $app | Select-Object FullName, Length
