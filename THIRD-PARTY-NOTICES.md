# Third-party notices

Traymetry uses the unmodified `LibreHardwareMonitorLib` package, version 0.9.6,
to read hardware sensors. LibreHardwareMonitor is distributed under the
Mozilla Public License 2.0. Its source code, license and complete third-party
license list are available in the upstream repository:

- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6
- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LICENSE
- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6/THIRD-PARTY-LICENSES

The NuGet package brings its own runtime dependencies. Traymetry embeds the
following unmodified assemblies:

- DiskInfoToolkit 1.1.2, RAMSPDToolkit-NDD 1.4.2 and BlackSharp.Core 1.0.7 — MPL-2.0.
- HidSharp 2.6.4 — Apache-2.0.
- System.Buffers, System.Memory, System.Numerics.Vectors and
  System.Runtime.CompilerServices.Unsafe — the applicable Microsoft/.NET MIT notices.

Their copyright notices, source links and exact license references are maintained in
the upstream third-party license list linked above; all rights remain with their
respective authors. A public binary distribution must also include the complete
corresponding license texts in its `LICENSES` directory.

## PawnIO 2.2.0

After explicit user consent, Traymetry downloads the unmodified official
`PawnIO_setup.exe` version 2.2.0 directly from its upstream GitHub Release.
Traymetry verifies the pinned SHA-256 and Authenticode signer before launching
it. The public Traymetry binary and release archive do not redistribute the
PawnIO installer or copy PawnIO source code.

- Official release: https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0
- Corresponding source: https://github.com/namazso/PawnIO/tree/2.2.0
- License and special exception: https://github.com/namazso/PawnIO/blob/2.2.0/README.md
- Full GPL-2.0 text: `vendor/PawnIO/COPYING.txt`
- SHA-256: `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`
- Authenticode signer: `namazso.eu` / certificate thumbprint
  `F380DCC9F706E2756A5047B832FFE719E1BC35F5`

## PresentMon Console 2.5.1 (staged, runtime integration disabled)

Traymetry is preparing an optional FPS adapter around the unmodified official
64-bit PresentMon console application. PresentMon captures presentation events
through Windows ETW and is distributed by Intel under the MIT License. The
adapter and aggregation code are Traymetry code; PresentMon remains an
independent third-party engine.

The exact upstream asset is staged for parser and integration testing, but it
is not launched by Traymetry and is not enabled for public distribution yet.
Those gates stay disabled until the service isolation, process selection,
shutdown and cross-game regression tests are complete.

- Source and project: https://github.com/GameTechDev/PresentMon
- Official release: https://github.com/GameTechDev/PresentMon/releases/tag/v2.5.1
- Official asset: https://github.com/GameTechDev/PresentMon/releases/download/v2.5.1/PresentMon-2.5.1-x64.exe
- MIT license: `vendor/PresentMon/LICENSE.txt`
- Size: `956768` bytes
- SHA-256: `9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191`
- Authenticode signer: `Intel Corporation` / certificate thumbprint
  `4B923D748E9EBE27252FDBA244342C1888A2D23E`
- Machine-readable dependency record: `dependencies.lock.json`

Future PresentMon versions must receive a new reviewed lock entry. Traymetry
must never download an unpinned `latest` binary at runtime or silently replace
this engine independently of a tested Traymetry release.

Traymetry itself is independently developed and distributed under the MIT
License. No third-party trademarks, logos or proprietary application code are
included in this repository.
