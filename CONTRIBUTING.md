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
```

The build runs self-tests for the updater, frame telemetry and pinned mouse
input. The updater test checks the compiled public key against its recorded
fingerprint. GitHub Actions also checks a clean installation of the sensor
service and verifies that the client receives a snapshot.

For UI changes, check resizing, the compact and expanded breakpoints,
pin/click-through, backgroundless mode, multiple monitors, opacity and
restoration after restart. For changes to the cards, check opening and closing
details in both views and the double-click view switch.

## Pull requests

Keep changes focused, explain user-visible behaviour and list the hardware and
Windows versions used for testing. New dependencies require a license review,
an exact version, reproducible source and SHA-256 pinning.
