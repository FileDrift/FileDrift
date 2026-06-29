# SPDX-License-Identifier: GPL-3.0-or-later
#requires -Version 7
<#
.SYNOPSIS
  Publishes FileDrift.exe (GUI) and FileDrift-CLI.exe (console) as self-contained single-file release
  artifacts — no .NET install required on the target machine.
.EXAMPLE
  ./publish.ps1
  ./publish.ps1 -Runtime win-arm64 -OutDir publish-arm64
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime       = "win-x64",
    [string]$OutDir        = "publish"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out  = Join-Path $root $OutDir
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

$common = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-o", $out
)

Write-Host "Publishing GUI (FileDrift.exe)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src/FileDrift.App/FileDrift.App.csproj") @common
if ($LASTEXITCODE) { throw "GUI publish failed." }

Write-Host "Publishing CLI (FileDrift-CLI.exe)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src/FileDrift.Cli/FileDrift.Cli.csproj") @common
if ($LASTEXITCODE) { throw "CLI publish failed." }

Write-Host "`nArtifacts in $out :" -ForegroundColor Green
Get-ChildItem $out -Filter *.exe | ForEach-Object { "  {0,-20} {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
Write-Host "`nSign them with:  ./sign.ps1 -Thumbprint <cert-thumbprint>   (or -SelfSigned for a dev test)"
