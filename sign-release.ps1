# Produces the two files a Traymetry release needs beside the executable: a
# manifest naming the exact build, and an RSA signature over it.  The widget
# refuses any update whose manifest does not verify against the public key
# compiled into it, so this script is the only way a new version can reach an
# installation that is already out there.
#
# The private key never leaves the machine and is never printed.  Losing it
# means no installed copy can ever be updated again - the public key is baked
# into every EXE already shipped - so back up the key file itself, not this
# script.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$ExecutablePath = 'Traymetry.exe',
    [string]$OutputDirectory = 'dist',
    [string]$PrivateKeyPath = (Join-Path $env:USERPROFILE '.traymetry\update-signing-private-key.xml')
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
function Resolve-Local([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { [IO.Path]::GetFullPath($path) }
    else { [IO.Path]::GetFullPath((Join-Path $projectDirectory $path)) }
}

$executable = Resolve-Local $ExecutablePath
$output = Resolve-Local $OutputDirectory
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "The executable to sign was not found: $executable"
}
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
    throw "The update signing key was not found: $PrivateKeyPath"
}
if (-not (Test-Path -LiteralPath $output)) {
    New-Item -ItemType Directory -Path $output | Out-Null
}

# The version in the manifest has to be the release tag, character for
# character: the widget compares the two and ignores any release where they
# differ.  A tag is 'v' and the informational version, so that is what is
# written here whichever of the two forms was typed - and the executable being
# signed is asked what version it actually is, because signing yesterday's
# build with today's number is a release that installs the wrong thing and
# reports the right one.
$version = $Version.Trim()
if ($version.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
    $version = $version.Substring(1)
}
$built = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
if ($built -ne $version) {
    throw "That executable is version $built, not $version."
}
$tag = 'v' + $version

# Exactly the shape SignedUpdateManifest.VerifyAndParse accepts: the header
# line, four key=value lines, a trailing newline, UTF-8 without a BOM and LF
# line endings.  A CRLF here fails verification on the far side.
$assetName = 'Traymetry.exe'
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $executable).Hash.ToUpperInvariant()
$size = (Get-Item -LiteralPath $executable).Length
$manifestText = "TRAYMETRY-UPDATE-MANIFEST-V1`n" +
    "version=$tag`n" +
    "asset=$assetName`n" +
    "sha256=$hash`n" +
    "size=$size`n"
$manifestBytes = (New-Object Text.UTF8Encoding($false)).GetBytes($manifestText)

$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
try {
    $rsa.PersistKeyInCsp = $false
    $rsa.FromXmlString((Get-Content -LiteralPath $PrivateKeyPath -Raw))
    if ($rsa.PublicOnly) {
        throw 'That key file holds only the public half - it cannot sign.'
    }
    $signature = $rsa.SignData($manifestBytes,
        [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
} finally {
    $rsa.Dispose()
}

$manifestPath = Join-Path $output 'Traymetry-update-manifest.txt'
$signaturePath = Join-Path $output 'Traymetry-update-manifest.sig'
[IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
[IO.File]::WriteAllText($signaturePath, [Convert]::ToBase64String($signature), [Text.Encoding]::ASCII)

# Verified by the shipping code rather than by this script's own idea of the
# format: the only opinion that matters belongs to the EXE that will read it.
# Quoted: a release built under a path with a space in it would otherwise reach
# the verifier as four arguments instead of three.
$verifier = Start-Process -FilePath $executable `
    -ArgumentList @('--verify-update-manifest', "`"$manifestPath`"", "`"$signaturePath`"") `
    -PassThru -Wait -WindowStyle Hidden
if ($verifier.ExitCode -ne 0) {
    Remove-Item -LiteralPath $manifestPath, $signaturePath -Force
    throw "The signed manifest was rejected by Traymetry itself (exit $($verifier.ExitCode))."
}

Write-Host "Manifest : $manifestPath"
Write-Host "Signature: $signaturePath"
Write-Host "Tag      : $tag  (must be the tag the release is created under)"
Write-Host "SHA-256  : $hash"
Write-Host "Size     : $size bytes"
Write-Host 'Verified by Traymetry.exe --verify-update-manifest.'
Write-Host 'Upload all three - Traymetry.exe, the manifest and the signature - to the release.'
