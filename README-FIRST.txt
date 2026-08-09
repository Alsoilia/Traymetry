TRAYMETRY

A compact hardware monitor for Windows: FPS, temperatures, CPU and GPU load,
memory, disks and network, over a game or on a second monitor.

Russian version of this file: README-FIRST.ru.txt

Requirements: 64-bit Windows 10 or 11, an Intel or AMD processor,
.NET Framework 4.7.2 or newer.


RUNNING IT

1. Unpack the whole archive into an ordinary folder.
2. Run Traymetry.exe.
3. Windows will show it as an unknown publisher. That is accurate: the
   executable itself carries no Authenticode signature. What is signed is the
   update manifest, with a key whose public half is compiled into the
   executable - see UPDATES below. SHA256SUMS.txt in this archive is the hash
   of the Traymetry.exe you have; the same hash is published with the release.


THE SENSOR DRIVER, ON FIRST RUN

Low-level CPU sensors - temperature and power - go through the PawnIO driver.
Without it those two readings may be unavailable no matter what rights
Traymetry runs with; everything else keeps working.

On first run Traymetry explains this and offers to set it up. Only if you
agree does it download the official signed installer from the PawnIO 2.2.0
GitHub Release and run it through UAC. Before anything is started it checks
both a pinned SHA-256 and the publisher's certificate (namazso.eu). The
installer is not bundled in this archive, and an internet connection is
needed for that one step.

Decline and nothing breaks - you lose CPU temperature and power, and keep the
rest. Some machines ask for a reboot after the driver is installed.


CONTROLS

- F1 opens the help window with every key and gesture.
- The % button opens a separate opacity slider. A click outside it or Escape
  closes it.
- The mouse wheel over the window changes opacity without the slider. One
  click on the window is needed first, so that Windows starts sending it the
  scroll; on a pinned window the scroll goes to whatever is underneath until
  that click happens.
- The bottom strip expands the full statistics and returns the exact previous
  size. A double click on the window does the same.
- Pin fixes the window in place and makes clicks pass through it. In the
  pinned state only the pin button is left: a left click goes to whatever is
  underneath, both on empty space and straight on the numbers, while a right
  or middle click still reaches the widget.
- The triangle at the top right hides it in the notification area.
- A right click opens the menu: cards, graphs, colours, language, hotkeys,
  always on top, start with Windows, quit, and removing the sensor service.
- Default hotkeys: Alt+~ pin, Alt+H hide, F1 help, Escape dismiss. Any of them
  can be reassigned or cleared under "Hotkeys".


UPDATES

Traymetry never installs an update without permission. At most once a day it
checks GitHub Releases in the background; a manual check sits in the right
click menu.

Before the executable is replaced, its SHA-256 is compared against a release
manifest signed with the project's key, the current file is kept as a backup,
and the replacement is atomic. Updating the sensor service may show UAC once.

A stable installation follows stable releases and ignores prereleases. If you
want preview builds, install one by hand from the releases page.


IF SOMETHING GOES WRONG

- Right click -> "Collect a problem report...". The file lands on the desktop:
  version, settings, sensor state and the log. Attach it to a GitHub issue.
- If the window will not start at all, the same report comes from the command
  line: Traymetry.exe --report
- The log lives in %LOCALAPPDATA%\Traymetry\traymetry.log


IF TEMPERATURE SHOWS A DASH

- Quit Traymetry from the right click menu and start it again.
- Make sure the sensor service setup was confirmed.
- If the installer asked for a reboot, reboot.
- Still nothing: report the CPU/GPU model, the Windows version and the problem
  report.

If temperature only appears when Traymetry runs as administrator, choose
"Check and repair sensors..." in the right click menu and confirm UAC once.
After that it should run normally again.


IF THE WINDOW DRAWS STRANGELY

The window uses per-pixel transparency, which is what keeps the edges of the
digits clean on any background. If that comes out wrong on your graphics card,
start it with

   Traymetry.exe --classic

which returns the older colour-key mode - and please report it, because that
switch exists as a way out, not as a setting anybody should need.


REMOVING IT

1. Right click on Traymetry, turn off "Start with Windows" if it was on.
2. Right click -> "Remove the system sensor service..." and confirm UAC.
3. Close Traymetry and delete the unpacked folder.
4. PawnIO is deliberately left installed: other programs may use it. Remove it
   separately through Windows settings if you want it gone.


WHERE IT CAME FROM

https://github.com/Alsoilia/Traymetry

Free and open source, MIT licensed. Bug reports and questions belong in the
repository's issues.
