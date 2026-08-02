# Changelog

## 0.9.0-preview.37

- Preserve both manual and automatic upper-panel visibility across restarts.
- Add background and manual GitHub Releases update checks.
- Verify update assets by SHA-256 and replace the EXE atomically with rollback.
- Add updater self-test to local and GitHub Actions builds.
- Add a support entry point for voluntary donations.
- Download the official PawnIO installer on first consent instead of
  redistributing it inside Traymetry; keep pinned hash and signer checks.
- Add reproducible release packaging, checksums and artifact attestation.
