# Security policy

## Supported versions

Until the first stable release, only the newest published preview is supported.

## Reporting a vulnerability

Please do not open a public issue for a vulnerability that could expose local
hardware access, privilege escalation or update integrity. Use GitHub private
vulnerability reporting for this repository. Include the Traymetry version,
Windows version, reproduction steps and relevant logs with secrets removed.

## Security model

- Traymetry's UI runs without administrator privileges.
- Privileged sensor access is isolated in a local Windows service.
- PawnIO and release downloads use fixed HTTPS origins and pinned integrity
  checks; PawnIO additionally requires the expected Authenticode signer.
- Traymetry updates require confirmation and keep a rollback copy.
- The project does not accept arbitrary third-party plugins or update feeds.
