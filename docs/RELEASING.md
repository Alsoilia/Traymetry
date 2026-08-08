# Releasing Traymetry

Public releases are built only by the tagged GitHub Actions workflow. Do not
upload a locally rebuilt executable under an existing version: the updater
trusts the release manifest signed by the Traymetry release key.

## One-time repository setup

1. Create an Actions secret named `TRAYMETRY_UPDATE_SIGNING_KEY`.
2. Store the private RSA key as base64-encoded UTF-8 XML. Keep the original key
   outside the repository and back it up offline. Losing it means existing
   installations cannot trust a replacement key without a manual update.
3. Enable private vulnerability reporting and protect the default branch.
4. Require the Windows build workflow before merging.

The matching public key is compiled into `ReleaseConfiguration.cs`. Never add
the private key, its base64 value, or an unredacted Actions log to Git.

## Release procedure

1. Update `AssemblyInformationalVersion` in `AssemblyInfo.cs` and
   `CHANGELOG.md`.
2. Verify the pinned dependency records in `dependencies.lock.json`.
3. Run locally:

   ```powershell
   ./build.ps1 -OutputPath ./release-test/Traymetry.exe
   ./release-test/Traymetry.exe --test-updater
   ./release-test/Traymetry.exe --test-frame-telemetry
   ./release-test/Traymetry.exe --test-mouse-hook
   ./package-preview.ps1 -OutputDirectory ./release-test `
     -PackageName Traymetry-win-x64 `
     -ExecutablePath ./release-test/Traymetry.exe
   ```

4. Merge the reviewed change, then create a tag exactly matching the compiled
   version with a leading `v`, for example `v0.9.0-preview.38`.
5. The release workflow builds the EXE once, packages that exact file, creates
   a canonical update manifest, signs it with RSA-SHA256, verifies it with the
   public key embedded in the new EXE, creates checksums and attestations, then
   publishes all assets.
6. Download the published ZIP on a clean Windows account and exercise the
   first-run sensor setup and one update from the previous version before
   promoting a preview to stable.

Preview installations only follow preview releases. Stable installations do
not automatically move to a prerelease channel.

## Dependency policy

PresentMon, LibreHardwareMonitor and PawnIO are replaceable adapters, not part
of Traymetry's product identity. Update a pinned engine only in a reviewed
Traymetry release, preserving its license and recording the exact upstream URL,
version, SHA-256 and signer where applicable. Never download an unpinned
`latest` dependency at runtime.
