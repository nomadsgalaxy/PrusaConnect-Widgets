#Requires -RunAsAdministrator
# Sideload Prusa Connect Widget: trust the dev cert, then install the package.
# Run from the unzipped folder that holds this script, the .cer, and the .msix:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$cer  = Get-ChildItem -Path $here -Filter *.cer  | Select-Object -First 1
$msix = Get-ChildItem -Path $here -Filter *.msix | Select-Object -First 1
if (-not $cer -or -not $msix) {
    throw "Couldn't find a .cer and .msix next to this script. Run it from the unzipped installer folder."
}

Write-Host "Trusting certificate $($cer.Name) ..."
Import-Certificate -FilePath $cer.FullName -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null

Write-Host "Installing $($msix.Name) ..."
Add-AppxPackage -Path $msix.FullName

Write-Host ""
Write-Host "Done. Open the Widgets Board (Win+W), add a 'Prusa Connect - 1..4' tile,"
Write-Host "then set each tile in the Prusa Connect Widget app from the Start menu."
