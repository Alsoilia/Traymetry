# Traymetry architecture

Traymetry owns the product UI, responsive layout, user settings, update flow,
sensor-service protocol and aggregation logic. Hardware- and presentation-event
engines are isolated behind adapters so that a pinned upstream version can be
updated, forked or replaced without rewriting the interface.

## Trust boundaries

- The normal user process renders the UI, samples CPU and GPU process details
  through Windows APIs and reads service snapshots over a local named pipe.
- The local sensor service performs privileged hardware access and exposes no
  generic read/write hardware API to the UI. The client checks that the pipe
  server is the process registered with the Windows Service Control Manager.
- Low-level libraries and tools are pinned to an exact version and SHA-256.
- Downloaded installers and update assets are accepted only from fixed HTTPS
  origins and are verified before execution or replacement.
- Traymetry does not load DLLs or plugins from arbitrary user-writable paths.

## Current engines

- **Hardware sensors:** LibreHardwareMonitorLib 0.9.6 and its documented runtime
  dependencies. The adapter returns a Traymetry `SensorSnapshot`; UI code does
  not reference LibreHardwareMonitor types.
- **Low-level access:** official PawnIO 2.2.0, downloaded only after consent.
  Its installer SHA-256 and Authenticode signer are pinned.
- **FPS:** the bundled PresentMon 2.5.1 console runs under
  `PresentMonStdoutRunner` in the sensor service. Setup verifies and installs
  the pinned executable under the protected Traymetry directory. The runner
  verifies it before launch, uses a dedicated ETW session and a kill-on-close
  job, and reads CSV from stdout.
- **Network details:** the sensor service counts TCP/UDP transfer sizes with
  Windows ETW. It reports the five highest rates for the caller's Windows
  session. The UI can show established TCP connection counts when transfer
  rates are unavailable.

If an upstream project stops, Traymetry can pin its final open-source release,
maintain a license-compliant fork and swap the adapter implementation. Upstream
copyright and license notices must always remain intact; Traymetry must never
claim third-party engine code as its own.

## Four-card model

The dashboard has four ordered metric slots. Responsive layout decides how
many fit, and the cycle button rotates the list. CPU, GPU, FPS and network
slots can open a detail panel. Wide expanded views show it in the widget;
compact views place an owned window beside the widget when space permits.

Every provider supplies a primary value, optional secondary values, colour,
unit, availability and accessibility text. Tiny cards show only the primary
value; larger cards progressively reveal secondary values without changing the
slot identity or order.

## Sensor protocol and capture

The pipe carries hardware snapshots in v1, hardware and frame snapshots in
v2, and a separate request for network rates. The client falls back to v1 when
an older host is still running. Capture demand travels in v2 requests.
PresentMon stops after the demand expires, and network capture uses its own
short lease from the details panel.

`PresentMonStdoutTelemetryAdapter` parses columns by name, bounds sample
retention and aggregates displayed, presented and application FPS separately.
It also calculates frame times and 1% lows. The UI chooses an active frame
source from the snapshot and prefers the foreground application. The FPS
detail panel keeps its source while open.

The dependency policy intentionally separates source compatibility from
shipping. A new upstream version is first added as a reviewed lock entry,
tested against recorded and live workloads, and only then enabled in a
Traymetry release. There is no independent `latest` updater for low-level
engines. If an upstream project is abandoned, the adapter boundary allows a
license-compliant fork or a new engine without changing cards or responsive UI.

No MSI Afterburner, RTSS or NVIDIA FrameView installation is required. Optional
compatibility adapters may use their documented shared-memory APIs when already
installed, but they must never be the only source.
