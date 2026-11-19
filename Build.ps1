[CmdletBinding()]
param([switch]$Verify, [switch]$Probe, [switch]$Package)

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
$app = Join-Path $out 'silik-vpn-watcher.exe'
$icon = Join-Path $source 'Watcher.ico'
& $compiler @common '/target:winexe' "/out:$app" "/win32icon:$icon" "/resource:$icon,Watcher.ico" '/reference:System.Windows.Forms.dll,System.Drawing.dll,System.Core.dll' $ras $schedule (Join-Path $source 'AssemblyInfo.cs') (Join-Path $source 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw "App compilation failed (exit $LASTEXITCODE)." }

if ($Verify -or $Probe -or $Package) {
    $scratch = Join-Path $PSScriptRoot '.tmp'
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $testExe = Join-Path $scratch ('VpnWatch.Tests.' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        & $compiler @common '/target:exe' "/out:$testExe" '/reference:System.Core.dll' $ras $schedule (Join-Path $source 'Tests.cs')
        if ($LASTEXITCODE -ne 0) { throw "Test compilation failed (exit $LASTEXITCODE)." }
        if ($Verify -or $Package) {
            & $testExe
            if ($LASTEXITCODE -ne 0) { throw "Verification failed (exit $LASTEXITCODE)." }
        }
        if ($Probe) {
            & $testExe --probe
            if ($LASTEXITCODE -ne 0) { throw "Probe failed (exit $LASTEXITCODE)." }
        }
    }
    finally { if (Test-Path -LiteralPath $testExe -PathType Leaf) { Remove-Item -LiteralPath $testExe } }
}
Get-Item -LiteralPath $app | Select-Object FullName, Length

if ($Package) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zipName = 'silik-vpn-watcher-windows-x64.zip'
    $zipPath = Join-Path $out $zipName
    $temporaryZip = Join-Path $scratch ([guid]::NewGuid().ToString('N') + '.zip')
    try {
        $zip = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $files = [ordered]@{
                'silik-vpn-watcher.exe' = $app
                'LICENSE.txt' = Join-Path $PSScriptRoot 'LICENSE'
                'README.txt' = Join-Path $PSScriptRoot 'docs\README.txt'
            }
            foreach ($name in $files.Keys) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $files[$name], $name)
            }
        }
        finally { $zip.Dispose() }
        Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Value "$hash  $zipName" -Encoding Ascii
    }
    finally { if (Test-Path -LiteralPath $temporaryZip -PathType Leaf) { Remove-Item -LiteralPath $temporaryZip } }
    Get-Item -LiteralPath $zipPath | Select-Object FullName, Length
}
