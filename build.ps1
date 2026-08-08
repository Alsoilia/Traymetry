param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $projectDirectory 'Traymetry.cs'
$interfaceSource = Join-Path $projectDirectory 'MonitorForm.cs'
$telemetrySource = Join-Path $projectDirectory 'HardwareTelemetry.cs'
$bootstrapSource = Join-Path $projectDirectory 'Bootstrap.cs'
$sensorServiceSource = Join-Path $projectDirectory 'SensorService.cs'
$releaseConfigurationSource = Join-Path $projectDirectory 'ReleaseConfiguration.cs'
$updateManagerSource = Join-Path $projectDirectory 'UpdateManager.cs'
$frameTelemetrySource = Join-Path $projectDirectory 'FrameTelemetry.cs'
$assemblyInfoSource = Join-Path $projectDirectory 'AssemblyInfo.cs'
$localizationSource = Join-Path $projectDirectory 'Localization.cs'
$helpWindowSource = Join-Path $projectDirectory 'HelpWindow.cs'
$layeredSurfaceSource = Join-Path $projectDirectory 'LayeredSurface.cs'
$hardwareLibrary = Join-Path $projectDirectory 'lib\LibreHardwareMonitorLib.dll'
$libraryDirectory = Join-Path $projectDirectory 'lib'
$pawnIoLicense = Join-Path $projectDirectory 'vendor\PawnIO\COPYING.txt'
$pawnIoLicenseNotice = Join-Path $projectDirectory 'vendor\PawnIO\LICENSE-NOTICE.md'
$presentMonExecutable = Join-Path $projectDirectory 'vendor\PresentMon\PresentMon-2.5.1-x64.exe'
$presentMonLicense = Join-Path $projectDirectory 'vendor\PresentMon\LICENSE.txt'
$thirdPartyNotices = Join-Path $projectDirectory 'THIRD-PARTY-NOTICES.md'
$manifest = Join-Path $projectDirectory 'app.manifest'
$output = if ([String]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $projectDirectory 'Traymetry.exe'
} elseif ([IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $projectDirectory $OutputPath
}
$outputDirectory = Split-Path -Parent $output
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}
$expectedLibraries = [ordered]@{
    'BlackSharp.Core.dll' = 'CAFB93AFCC8D8A367E21F619673D05C06887D8964867FED1371F02DED1CD3E23'
    'DiskInfoToolkit.dll' = '1ACBF51B3C10C51C986CF43021680D34A2E38D9A5BA652BCFA9A1B5F7FC09800'
    'HidSharp.dll' = 'D86690EFDE30EA9179F669320F39148853793B743A98B531AFEAF30598E22F54'
    'LibreHardwareMonitorLib.dll' = '6EBC194316536BA61AF5BE24508AD9FCBB2ECC685E716C12E787C79530F66BF0'
    'RAMSPDToolkit-NDD.dll' = 'B6882354C7C8EC186617E421507743DBFAE09C5C1FC24CEF76A1D0C0C26651DE'
    'System.Buffers.dll' = '2D78D770C9CB997199154AE8C018B9F1D1EFBC86729F7264DDE6DBAD2A12CAC3'
    'System.Memory.dll' = 'D5E8E4866F9CFA66F7765660F84B210198893E55335487AFE5EBDA342C0E913D'
    'System.Numerics.Vectors.dll' = '20C2FA81B8C70D651099D762954F285FD4F942E63B2D7217C145DAB8D4B2F4C9'
    'System.Runtime.CompilerServices.Unsafe.dll' = '08CBD7278B66F1E68425A82D4B97181A4130D93E3DD91831407ABA7212CCDACF'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The built-in .NET Framework compiler was not found.'
}

$actualLibraryNames = @(Get-ChildItem -LiteralPath $libraryDirectory -Filter '*.dll' | Select-Object -ExpandProperty Name)
$unexpectedLibraries = @($actualLibraryNames | Where-Object { -not $expectedLibraries.Contains($_) })
if ($unexpectedLibraries.Count -gt 0) {
    throw 'Unexpected libraries in lib: ' + ($unexpectedLibraries -join ', ')
}
foreach ($entry in $expectedLibraries.GetEnumerator()) {
    $path = Join-Path $libraryDirectory $entry.Key
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required library was not found: $($entry.Key)"
    }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    if ($actualHash -ne $entry.Value) {
        throw "Hash mismatch for $($entry.Key)."
    }
}

$expectedPresentMonHash = '9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191'
$expectedPresentMonSigner = '4B923D748E9EBE27252FDBA244342C1888A2D23E'
if (-not (Test-Path -LiteralPath $presentMonExecutable)) {
    throw 'The pinned PresentMon 2.5.1 executable was not found.'
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $presentMonExecutable).Hash -ne $expectedPresentMonHash) {
    throw 'Hash mismatch for PresentMon 2.5.1.'
}
$presentMonSignature = Get-AuthenticodeSignature -LiteralPath $presentMonExecutable
if ($null -eq $presentMonSignature.SignerCertificate -or
    $presentMonSignature.SignerCertificate.Thumbprint -ne $expectedPresentMonSigner) {
    throw 'Signer mismatch for PresentMon 2.5.1.'
}
if (-not (Test-Path -LiteralPath $presentMonLicense)) {
    throw 'The PresentMon MIT license was not found.'
}
$resourceArguments = @()
foreach ($libraryName in $expectedLibraries.Keys) {
    $libraryPath = Join-Path $libraryDirectory $libraryName
    $resourceArguments += "/resource:$libraryPath,Traymetry.Embedded.$libraryName"
}
$resourceArguments += "/resource:$pawnIoLicense,Traymetry.Licenses.PawnIO-COPYING.txt"
$resourceArguments += "/resource:$pawnIoLicenseNotice,Traymetry.Licenses.PawnIO-NOTICE.md"
$resourceArguments += "/resource:$presentMonExecutable,Traymetry.Embedded.PresentMon-2.5.1-x64.exe"
$resourceArguments += "/resource:$presentMonLicense,Traymetry.Licenses.PresentMon-MIT.txt"
$resourceArguments += "/resource:$thirdPartyNotices,Traymetry.Licenses.THIRD-PARTY-NOTICES.md"

$compilerArguments = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/warn:4',
    '/warnaserror+',
    '/platform:x64',
    "/win32manifest:$manifest",
    '/codepage:65001',
    "/out:$output",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Runtime.Serialization.dll',
    '/reference:System.ServiceProcess.dll',
    '/reference:System.Windows.Forms.dll',
    "/reference:$hardwareLibrary"
) + $resourceArguments + @($source, $interfaceSource, $telemetrySource, $bootstrapSource, $sensorServiceSource, $releaseConfigurationSource, $updateManagerSource, $frameTelemetrySource, $assemblyInfoSource, $localizationSource, $helpWindowSource, $layeredSurfaceSource)

& $compiler $compilerArguments

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

# A build is not finished until it has proved the two things a release rests
# on: that this executable will only accept an update signed by the key it
# carries, and that the frame counter still reads.  Both were run by the
# release workflow alone, which is the one place a mistake is expensive.
foreach ($selfTest in @('--test-updater', '--test-frame-telemetry')) {
    $process = Start-Process -FilePath $output -ArgumentList $selfTest -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Self-test $selfTest failed with exit code $($process.ExitCode)."
    }
}

Write-Host "Built: $output"
