[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $projectRoot 'src'
$dist = Join-Path $projectRoot 'dist'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "The built-in .NET Framework C# compiler was not found at $compiler"
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null

& $compiler `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    /win32manifest:"$(Join-Path $sourceRoot 'app.manifest')" `
    /out:"$(Join-Path $dist 'PrintInterceptor.exe')" `
    /reference:System.dll `
    /reference:System.Configuration.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Xml.dll `
    "$(Join-Path $sourceRoot 'Program.cs')" `
    "$(Join-Path $sourceRoot 'Version.cs')"

if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $sourceRoot 'App.config') -Destination (Join-Path $dist 'PrintInterceptor.exe.config') -Force

& (Join-Path $dist 'PrintInterceptor.exe') --self-test
if ($LASTEXITCODE -ne 0) {
    throw "The non-hardware self-test failed with exit code $LASTEXITCODE. See the audit log under %LOCALAPPDATA%\PrintInterceptor\logs."
}

Write-Host "Built and self-tested: $(Join-Path $dist 'PrintInterceptor.exe')"
Write-Host 'The self-test validated configuration, event parsing, PDF extraction, update metadata parsing, desktop-control signaling, and drawer-command generation without accessing printer hardware.'

$setupSource = Join-Path $projectRoot 'installer\Setup.cs'
$setupManifest = Join-Path $projectRoot 'installer\setup.manifest'
$setupOutput = Join-Path $dist 'PrintInterceptorSetup.exe'
$payloadResource = '/resource:{0},PrintInterceptor.Payload.exe' -f (Join-Path $dist 'PrintInterceptor.exe')

& $compiler `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    /win32manifest:"$setupManifest" `
    /out:"$setupOutput" `
    $payloadResource `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Management.dll `
    /reference:System.Security.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Xml.dll `
    "$setupSource" `
    "$(Join-Path $sourceRoot 'Version.cs')"

if ($LASTEXITCODE -ne 0) {
    throw "Setup compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Portable single-file installer: $setupOutput"
