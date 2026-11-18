param(
    [Parameter(Mandatory)]
    [string]$Executable
)

$ErrorActionPreference = 'Stop'

$serviceName = 'TraymetrySensorHost'
$hostPath = Join-Path $env:ProgramFiles 'Traymetry/Traymetry.SensorHost.exe'
$pawnIoKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO'
if ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue) -or
    (Test-Path (Split-Path -Parent $hostPath)) -or
    (Get-CimInstance Win32_SystemDriver -Filter "Name = 'PawnIO'") -or
    (Test-Path $pawnIoKey) -or
    (Test-Path ($pawnIoKey -replace 'SOFTWARE\\', 'SOFTWARE\WOW6432Node\'))) {
    throw 'Clean-install test requires a fresh Windows runner without Traymetry or PawnIO.'
}

$binary = (Resolve-Path -LiteralPath $Executable).Path
$windows = Get-CimInstance Win32_OperatingSystem
"Clean baseline on $($windows.Caption), build $($windows.BuildNumber)."
$setup = Start-Process -FilePath $binary -ArgumentList '--setup-machine' -Verb RunAs -Wait -PassThru
if ($setup.ExitCode -ne 0) {
    throw "Clean sensor setup returned $($setup.ExitCode)."
}
'Sensor setup exited with code 0.'

$driver = Get-CimInstance Win32_SystemDriver -Filter "Name = 'PawnIO'"
if ($driver.State -ne 'Running' -or (Get-Service $serviceName).Status -ne 'Running') {
    throw 'PawnIO and TraymetrySensorHost must both be running after setup.'
}
if ((Get-FileHash -LiteralPath $binary).Hash -ne (Get-FileHash -LiteralPath $hostPath).Hash) {
    throw 'The installed service executable does not match this build.'
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($binary))
$client = $assembly.GetType('Traymetry.SensorServiceClient', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$read = $client.GetMethod('TryReadSnapshot', $flags, $null,
    [type[]]@($assembly.GetType('Traymetry.SensorSnapshot', $true).MakeByRefType()), $null)
$reply = [object[]]@($null)
$deadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    if ($read.Invoke($null, $reply) -and $null -ne $reply[0]) {
        'PawnIO and TraymetrySensorHost are running. The client received a sensor snapshot.'
        return
    }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $deadline)

throw 'TraymetrySensorHost is running but the client received no sensor snapshot.'
