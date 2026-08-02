param(
    [string]$OutputDirectory = "dist",
    [string]$PackageName = "Traymetry-Preview-win-x64",
    [string]$ExecutablePath = "Traymetry.exe"
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $projectDirectory $OutputDirectory))
}
$projectRoot = [IO.Path]::GetFullPath($projectDirectory).TrimEnd('\') + '\'
if (-not $resolvedOutput.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The preview output directory must stay inside the Traymetry project.'
}
$resolvedExecutable = if ([IO.Path]::IsPathRooted($ExecutablePath)) {
    [IO.Path]::GetFullPath($ExecutablePath)
} else {
    [IO.Path]::GetFullPath((Join-Path $projectDirectory $ExecutablePath))
}
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw "The already-built executable was not found: $resolvedExecutable"
}

$stagingRoot = Join-Path $projectDirectory 'tmp\package-preview'
$staging = Join-Path $stagingRoot $PackageName
$archive = Join-Path $resolvedOutput ($PackageName + '.zip')

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
New-Item -ItemType Directory -Path $staging -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $staging 'LICENSES') -Force | Out-Null

$stagedExecutable = Join-Path $staging 'Traymetry.exe'
Copy-Item -LiteralPath $resolvedExecutable -Destination $stagedExecutable
$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedExecutable).Hash
$stagedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $stagedExecutable).Hash
if (-not [String]::Equals($sourceHash, $stagedHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The packaged executable does not match the already-built executable.'
}

$rootFiles = @(
    'Traymetry.exe.config',
    'README-FIRST.txt',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md'
)
foreach ($name in $rootFiles) {
    Copy-Item -LiteralPath (Join-Path $projectDirectory $name) -Destination $staging
}

Copy-Item -LiteralPath (Join-Path $projectDirectory 'LICENSES\MPL-2.0.txt') -Destination (Join-Path $staging 'LICENSES')
Copy-Item -LiteralPath (Join-Path $projectDirectory 'LICENSES\Apache-2.0-HidSharp.txt') -Destination (Join-Path $staging 'LICENSES')
Copy-Item -LiteralPath (Join-Path $projectDirectory 'LICENSES\Microsoft-DotNet-MIT.txt') -Destination (Join-Path $staging 'LICENSES')
Copy-Item -LiteralPath (Join-Path $projectDirectory 'vendor\PawnIO\COPYING.txt') -Destination (Join-Path $staging 'LICENSES\PawnIO-GPL-2.0.txt')
Copy-Item -LiteralPath (Join-Path $projectDirectory 'vendor\PawnIO\LICENSE-NOTICE.md') -Destination (Join-Path $staging 'LICENSES\PawnIO-LICENSE-NOTICE.md')
Copy-Item -LiteralPath (Join-Path $projectDirectory 'vendor\PresentMon\LICENSE.txt') -Destination (Join-Path $staging 'LICENSES\PresentMon-MIT.txt')

$hash = $stagedHash
$hashLine = $hash + '  Traymetry.exe' + [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $staging 'SHA256SUMS.txt'), $hashLine,
    (New-Object Text.UTF8Encoding($false)))

$required = @(
    'Traymetry.exe',
    'Traymetry.exe.config',
    'README-FIRST.txt',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'SHA256SUMS.txt',
    'LICENSES\MPL-2.0.txt',
    'LICENSES\Apache-2.0-HidSharp.txt',
    'LICENSES\Microsoft-DotNet-MIT.txt',
    'LICENSES\PawnIO-GPL-2.0.txt',
    'LICENSES\PawnIO-LICENSE-NOTICE.md',
    'LICENSES\PresentMon-MIT.txt'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $staging $relative))) {
        throw "Missing package file: $relative"
    }
}

Compress-Archive -LiteralPath $staging -DestinationPath $archive -CompressionLevel Optimal
Remove-Item -LiteralPath $stagingRoot -Recurse -Force
Write-Host "Package: $archive"
Write-Host "Traymetry.exe SHA-256: $hash"
