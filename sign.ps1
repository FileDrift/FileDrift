# SPDX-License-Identifier: GPL-3.0-or-later
#requires -Version 7
<#
.SYNOPSIS
  Authenticode-signs the published FileDrift executables, with RFC-3161 timestamping so the
  signatures remain valid after the certificate expires. Uses Set-AuthenticodeSignature, so no
  Windows SDK / signtool is required.
.DESCRIPTION
  Sign with a code-signing certificate from your store (your enterprise CA cert is ideal — it's
  already trusted on domain machines), referenced by thumbprint:

      ./sign.ps1 -Thumbprint A1B2C3...

  Or generate/reuse a self-signed dev certificate just to exercise the pipeline (NOT trusted by
  other machines unless you deploy it via Group Policy):

      ./sign.ps1 -SelfSigned

  SignPath (the cloud service for public OSS releases) signs in CI, not here — see README.
#>
[CmdletBinding(DefaultParameterSetName = 'Thumbprint')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Thumbprint')]
    [string]$Thumbprint,

    [Parameter(Mandatory, ParameterSetName = 'SelfSigned')]
    [switch]$SelfSigned,

    [string]$Path         = "publish",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)
$ErrorActionPreference = "Stop"

function Get-SigningCert {
    if ($SelfSigned) {
        $subject = 'CN=FileDrift Dev (self-signed)'
        $existing = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } | Select-Object -First 1
        if ($existing) { return $existing }
        Write-Host "Creating a self-signed code-signing certificate ($subject)..." -ForegroundColor Yellow
        return New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
            -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature -KeyExportPolicy Exportable
    }
    foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') {
        $c = Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $Thumbprint }
        if ($c) { return $c }
    }
    throw "No certificate with thumbprint '$Thumbprint' found in CurrentUser\My or LocalMachine\My."
}

$cert  = Get-SigningCert
$dir   = Join-Path $PSScriptRoot $Path
$files = Get-ChildItem $dir -Filter *.exe -ErrorAction SilentlyContinue
if (-not $files) { throw "No .exe files found in '$dir'. Run ./publish.ps1 first." }

Write-Host "Signing with: $($cert.Subject)  [$($cert.Thumbprint)]" -ForegroundColor Cyan
foreach ($f in $files) {
    $r = Set-AuthenticodeSignature -FilePath $f.FullName -Certificate $cert `
        -TimestampServer $TimestampUrl -HashAlgorithm SHA256

    if ($r.Status -eq 'Valid') {
        Write-Host ("  {0,-20} Valid (signed, trusted, timestamped)" -f $f.Name) -ForegroundColor Green
    }
    elseif ($r.SignerCertificate) {
        # The signature WAS written; it just doesn't chain to a root trusted on THIS machine. Normal for
        # a self-signed cert, or a CA cert whose root isn't installed where you're signing. The file is
        # correctly signed and will validate on machines that trust the issuing CA (or where it's deployed).
        Write-Host ("  {0,-20} Signed (cert not trusted on this machine — expected for self-signed / uninstalled CA root)" -f $f.Name) -ForegroundColor Yellow
    }
    else {
        throw "Signing failed for $($f.Name): $($r.Status) — $($r.StatusMessage)"
    }
}
