# Releasing Traymetry

Public releases are built only by the tagged GitHub Actions workflow. Do not
upload a locally rebuilt executable under an existing version: the updater
trusts the release manifest signed by the Traymetry release key.

## One-time repository setup

1. Keep the RSA key outside the repository and back it up offline. Losing it
   means existing installations need a manual update to trust a replacement
   key. Automated publication requires the Actions secret
   `TRAYMETRY_UPDATE_SIGNING_KEY` as base64-encoded UTF-8 XML.
2. Enable private vulnerability reporting and protect the default branch.
3. Require the Windows build workflow before merging.

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
5. The release workflow builds, tests and attests the EXE and portable ZIP. If
   an Actions signing key is configured, it signs and verifies the update
   manifest, creates checksums and publishes the release. For offline signing,
   download `Traymetry-offline-signing` from the successful tag run, verify
   both build attestations for that tag, and run `sign-release.ps1` on that
   exact EXE. Create `SHA256SUMS.txt` covering the EXE, ZIP, manifest and
   signature, then publish those five files under the existing tag.
6. Download the published ZIP on a clean Windows account and exercise the
   first-run sensor setup and one update from the previous version before
   promoting a preview to stable.

A preview installation follows both preview and stable releases; a stable one
ignores prereleases entirely. Moving an installation to a stable build is
therefore a one-way door: nothing will offer it a preview again, and following
previews once more means installing one by hand.

## Dependency policy

PresentMon, LibreHardwareMonitor and PawnIO are replaceable adapters, not part
of Traymetry's product identity. Update a pinned engine only in a reviewed
Traymetry release, preserving its license and recording the exact upstream URL,
version, SHA-256 and signer where applicable. Never download an unpinned
`latest` dependency at runtime.
