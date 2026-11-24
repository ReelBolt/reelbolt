<#
  Package and sign one built Windows runner (WP E10b).

    package.ps1 -Version 1.4.2 -Dist <dir> -Out <dir> -SigningBackend none
    package.ps1 -Version 1.4.2 -Dist <dir> -Out <dir> -SigningBackend pfx
    package.ps1 -Version 1.4.2 -Dist <dir> -Out <dir> -SigningBackend trusted-signing

  -Dist holds what the build produced: reelbolt-runner.exe (the daemon),
  reelbolt-runner-desktop.exe (the tray), ffmpeg.exe, ffprobe.exe and
  THIRD_PARTY_NOTICES.md.

  ORDER MATTERS: the executables are signed BEFORE the MSI is built, so the MSI
  carries signed binaries and so that a user who extracts it by hand - or runs
  the runner's own self-update, which downloads the bare .exe - gets a file
  Authenticode accepts. WinVerifyTrust is what the runner itself uses before it
  installs a Windows artifact (runner/internal/update/apply_windows.go), so an
  unsigned artifact is not "unsigned but usable": it is one the update path
  refuses.

  Signing backends:

    none            package only, and mark everything unsigned (CI does this
                    when no certificate is configured, so the pipeline is
                    exercisable before E10a provisions one; the publish job
                    then refuses to publish)
    pfx             signtool with a code-signing PFX.
                    WINDOWS_SIGNING_PFX_BASE64, WINDOWS_SIGNING_PFX_PASSWORD
    trusted-signing Azure Trusted Signing, via signtool's dlib.
                    AZURE_TRUSTED_SIGNING_DLIB, AZURE_TRUSTED_SIGNING_METADATA

  Nothing here holds a certificate. The workflow passes it in from the GitHub
  environment "release"; see ../README.md.
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [Parameter(Mandatory = $true)][string]$Dist,
  [Parameter(Mandatory = $true)][string]$Out,
  [ValidateSet("none", "pfx", "trusted-signing")][string]$SigningBackend = "none",
  [string]$Arch = "x64",
  [string]$UpgradeCode = "6C1E4E4B-4B2E-4C31-9E4B-1E0C9C2F0C21",
  [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+') {
  throw "package.ps1: -Version $Version is not a dotted version (1.4.2)"
}

foreach ($tool in @("wix")) {
  if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
    throw "package.ps1: $tool is not on PATH. The workflow installs WiX v4 with: dotnet tool install --global wix"
  }
}

$Dist = (Resolve-Path $Dist).Path
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path

$name = "reelbolt-runner-$Version-windows-amd64"
foreach ($f in @("reelbolt-runner.exe", "reelbolt-runner-desktop.exe", "ffmpeg.exe", "ffprobe.exe", "THIRD_PARTY_NOTICES.md")) {
  if (-not (Test-Path (Join-Path $Dist $f))) {
    throw "package.ps1: $f is missing from $Dist; build it first"
  }
}

function Invoke-SignTool {
  param([string[]]$Files)
  if (-not (Get-Command signtool.exe -ErrorAction SilentlyContinue)) {
    throw "package.ps1: signtool.exe is not on PATH; install the Windows SDK"
  }
  switch ($SigningBackend) {
    "pfx" {
      $b64 = $env:WINDOWS_SIGNING_PFX_BASE64
      if ([string]::IsNullOrWhiteSpace($b64)) {
        throw "package.ps1: -SigningBackend pfx needs WINDOWS_SIGNING_PFX_BASE64 (the certificate, base64) and WINDOWS_SIGNING_PFX_PASSWORD"
      }
      $pfx = Join-Path $env:RUNNER_TEMP "codesign.pfx"
      [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($b64))
      foreach ($file in $Files) {
        & signtool.exe sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $pfx /p $env:WINDOWS_SIGNING_PFX_PASSWORD $file
        if ($LASTEXITCODE -ne 0) { throw "package.ps1: signtool failed on $file ($LASTEXITCODE)" }
      }
      Remove-Item $pfx -Force
    }
    "trusted-signing" {
      $dlib = $env:AZURE_TRUSTED_SIGNING_DLIB
      $metadata = $env:AZURE_TRUSTED_SIGNING_METADATA
      if ([string]::IsNullOrWhiteSpace($dlib) -or [string]::IsNullOrWhiteSpace($metadata)) {
        throw "package.ps1: -SigningBackend trusted-signing needs AZURE_TRUSTED_SIGNING_DLIB (path to Azure.CodeSigning.Dlib.dll) and AZURE_TRUSTED_SIGNING_METADATA (path to the metadata JSON the workflow writes from the Azure secrets)"
      }
      foreach ($file in $Files) {
        & signtool.exe sign /fd SHA256 /td SHA256 /tr $TimestampUrl /dlib $dlib /dmdf $metadata $file
        if ($LASTEXITCODE -ne 0) { throw "package.ps1: signtool (Azure Trusted Signing) failed on $file ($LASTEXITCODE)" }
      }
    }
    default { }
  }
}

function Assert-Signed {
  param([string[]]$Files)
  foreach ($file in $Files) {
    $sig = Get-AuthenticodeSignature $file
    if ($sig.Status -ne "Valid") {
      throw "package.ps1: $file is not validly signed ($($sig.Status)). An unsigned or unverifiable Windows artifact is one the runner refuses to install; it must not be published."
    }
    Write-Host "package.ps1: signed $([IO.Path]::GetFileName($file)) -> $($sig.SignerCertificate.Subject)"
  }
}

$binaries = @(
  (Join-Path $Dist "reelbolt-runner.exe"),
  (Join-Path $Dist "reelbolt-runner-desktop.exe"),
  (Join-Path $Dist "ffmpeg.exe"),
  (Join-Path $Dist "ffprobe.exe")
)

if ($SigningBackend -ne "none") {
  Write-Host "package.ps1: signing the executables before the MSI is built, so the MSI embeds signed files"
  Invoke-SignTool -Files $binaries
  Assert-Signed -Files $binaries
}

# The MSI. -d passes the values the .wxs declares as preprocessor variables.
$msi = Join-Path $Out "$name.msi"
& wix build -arch $Arch -d "Version=$Version" -d "DistDir=$Dist" -d "UpgradeCode=$UpgradeCode" -o $msi (Join-Path $PSScriptRoot "runner.wxs")
if ($LASTEXITCODE -ne 0) { throw "package.ps1: wix build failed ($LASTEXITCODE)" }
Write-Host "package.ps1: $msi"

if ($SigningBackend -ne "none") {
  Invoke-SignTool -Files @($msi)
  Assert-Signed -Files @($msi)
}

# The manifest payload for the "exe" format: the bare daemon, byte for byte
# what a Windows runner's self-update copies over itself (applyReplace).
$payload = Join-Path $Out "$name.exe"
Copy-Item (Join-Path $Dist "reelbolt-runner.exe") $payload -Force
Write-Host "package.ps1: $payload (raw daemon: the manifest exe payload)"

if ($SigningBackend -eq "none") {
  Write-Host "package.ps1: NOT SIGNED - no signing backend was selected. This output must not be published to a channel."
  if ($env:GITHUB_OUTPUT) { Add-Content $env:GITHUB_OUTPUT "signed=false" }
}
else {
  if ($env:GITHUB_OUTPUT) { Add-Content $env:GITHUB_OUTPUT "signed=true" }
}
