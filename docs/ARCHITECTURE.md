# Traymetry architecture

Traymetry owns the product UI, responsive layout, user settings, update flow,
sensor-service protocol and aggregation logic. Hardware- and presentation-event
engines are isolated behind adapters so that a pinned upstream version can be
updated, forked or replaced without rewriting the interface.

## Trust boundaries

- The normal user process renders the UI and reads only aggregated values.
- The local sensor service performs privileged hardware access and exposes no
  generic read/write hardware API to the UI.
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
- **FPS (parser stage):** the official PresentMon console is isolated behind
  `IFrameTelemetryProvider`. `PresentMonStdoutTelemetryAdapter` currently parses
  and aggregates bounded CSV input only; it cannot start a process, elevate,
  install software or write captures to disk. PresentMon process lifecycle and
  service IPC remain deliberately disabled until their own review stage.

If an upstream project stops, Traymetry can pin its final open-source release,
maintain a license-compliant fork and swap the adapter implementation. Upstream
copyright and license notices must always remain intact; Traymetry must never
claim third-party engine code as its own.

## Four-card model

The compact dashboard is modelled as four ordered slots. A slot points to a
metric provider, not to a concrete WinForms control. Responsive layout decides
how many slots fit and the cycle button rotates that ordered list.

Planned presets:

1. **System:** CPU, GPU, memory, network.
2. **Gaming:** FPS/1% low, frame time, GPU, CPU.
3. **Cooling:** CPU temperature, GPU temperature, CPU fan, GPU fan.
4. **Custom:** any four supported providers, with reordering.

Every provider supplies a primary value, optional secondary values, colour,
unit, availability and accessibility text. Tiny cards show only the primary
value; larger cards progressively reveal secondary values without changing the
slot identity or order.

## FPS integration plan

1. **Completed foundation:** PresentMon 2.5.1 is pinned in
   `dependencies.lock.json`; its asset hash, Authenticode identity and MIT
   license are recorded. The runtime and distribution gates remain `false`.
2. **Completed parser stage:** consume CSV columns by normalized names rather
   than by position; tolerate reordered columns and `NA`; reject malformed,
   oversized and non-finite input; bound processes and sample history.
3. Add a process-lifecycle adapter in the sensor service. It must run the
   verified binary only from the protected Traymetry directory, redirect
   stdout, place the child in a kill-on-close job and use a unique ETW session.
4. Version the sensor pipe before adding FPS fields. A new client must be able
   to fall back to the current sensor protocol while an older service is still
   running during an update.
5. Let the user-session UI report the foreground PID. The service must validate
   it as numeric process metadata and must not accept a path or arbitrary
   PresentMon arguments from the user process.
6. Aggregate displayed, presented and application FPS separately, plus frame
   time, p95 frame time and 1% low. Select the most active recent swap chain for
   the requested PID; return an explicit waiting/stale status instead of zero.
7. Start capture only while an FPS-dependent card is enabled, and stop it after
   the final subscriber disappears. A PresentMon failure must never restart or
   stall the existing CPU/GPU sensor loop.

The parser keeps at most 32 processes and 4096 samples per process, uses a
short window for live FPS and a bounded 30-second window for percentiles. It
does not expose automatic process guessing: foreground/manual target selection
belongs to the user-session UI where window ownership can be determined
correctly.

The dependency policy intentionally separates source compatibility from
shipping. A new upstream version is first added as a reviewed lock entry,
tested against recorded and live workloads, and only then enabled in a
Traymetry release. There is no independent `latest` updater for low-level
engines. If an upstream project is abandoned, the adapter boundary allows a
license-compliant fork or a new engine without changing cards or responsive UI.

No MSI Afterburner, RTSS or NVIDIA FrameView installation is required. Optional
compatibility adapters may use their documented shared-memory APIs when already
installed, but they must never be the only source.
