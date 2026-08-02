# Contributing to Traymetry

Thanks for helping improve Traymetry.

## Before changing code

1. Open an issue for behaviour changes or new sensor engines.
2. Keep the normal UI process unprivileged.
3. Do not add manufacturer binaries, copied proprietary code or unpinned
   downloads.
4. Preserve all third-party notices and license texts.

## Build and test

Run from 64-bit Windows:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 `
  -OutputPath .\artifacts\Traymetry.exe

$process = Start-Process .\artifacts\Traymetry.exe `
  -ArgumentList --test-updater -PassThru -Wait
if ($process.ExitCode -ne 0) { throw 'Updater self-test failed.' }
```

For UI changes, manually check resizing from every edge and corner, the compact
and expanded breakpoints, pin/click-through, backgroundless mode, multiple
monitors, opacity popup and restoration after restart.

## Pull requests

Keep changes focused, explain user-visible behaviour and list the hardware and
Windows versions used for testing. New dependencies require a license review,
an exact version, reproducible source and SHA-256 pinning.
