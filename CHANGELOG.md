# Changelog

## Unreleased

- Say which check turned a release down. A release newer than the running build
  that is not offered is the one case where silence is worst: the release is
  there on the page, the widget says it is up to date, and both cannot be true.
  Every way out of that loop now names the check and both sides of it - the tag
  against the version in the signed manifest, the size in the manifest against
  the size of the attached file, GitHub's digest against the signed one - so the
  answer is in the log rather than in a rebuild with print statements in it.

## 0.9.0-preview.96

- Stop the system drawing a shadow around the widget. That shadow is a window
  of its own, and the system puts it around the *shape* of the window it
  belongs to - which here is not the card: the hover bands are given up by
  cutting them out of the window region, so the shadow was laid along the line
  of that cut. Backgroundless, where there is no card for a shadow to belong
  to, it read as a dark strip across the readings and down one side, and
  changing the shape - resizing, or turning the background off and on - moved
  it. Nothing but the readings is on screen now.
- Check the update signing key this build carries against the fingerprint kept
  beside it, and that it is a public key of at least 2048 bits. The key is a
  string in the source, and a string in the source is a thing that can be
  changed in a pull request without anyone reading its digits; the fingerprint
  is the second place that would have to be changed to match. The self-test
  says so, and the build runs the self-tests now instead of leaving them to the
  release workflow, so a swapped key is a build that does not finish.
- Hash and check the signature of the downloaded PawnIO installer with the file
  held open and no writer allowed, so that what is checked and what is run are
  the same bytes.
- Write the release tag into the signed manifest, whichever way the version was
  typed, and refuse to sign an executable that is not the version being signed.
  The widget only accepts an update whose manifest names the tag exactly, so a
  manifest signed as `0.9.0-preview.96` where the tag reads `v0.9.0-preview.96`
  is a release nobody is ever offered - correctly signed, silently ignored, and
  with nothing anywhere to say why.
- Write down what every update check came back with, and put the update log in
  the problem report. A check that failed left no trace at all: the automatic
  one says nothing on purpose, and the manual one says it in a message box that
  is gone by the time anyone thinks to ask. "It never offers me anything" was
  therefore a report with no evidence behind it - a repository that does not
  exist, a name typed wrong and a machine with no network all look identical
  from the outside, and all three are one line in a log.
- Drop seven members nothing called, including a card layout for two cards that
  are never shown.

- Let the storage and memory panels of the full view take the colour chosen for
  them. Both were drawn with the default written out as a number in place,
  which is the one colour the palette can never change, so those two entries in
  "value colour" appeared to do nothing.
- Stop the widget blanking out when it steps aside for a click. Setting an
  extended style empties a layered window - nothing is on screen again until a
  whole frame is handed over, and a frame is only handed over when something
  asks for a repaint. Stepping out of the mouse's way asks for nothing, so the
  widget vanished and stayed vanished until the user happened to click, which
  is what brought it back. It asks for its own frame now, and the style is only
  written when the bit it is about actually has to move.
- Keep a pinned widget in the topmost band across the click it lets through.
  Stepping out of the mouse's way is a whole-word write of the extended style,
  and it carried the stale topmost bit back with it: the widget really did leave
  the band on the first click and was dug out again a frame later, which is the
  blink seen on clicking away from a pinned widget.

- Dismiss a menu on the button being down outside it rather than on the press
  being caught as it happens. A click lasts about a tenth of a second and the
  poll runs every fortieth, which is plenty until the widget stalls: the whole
  press then falls into the gap, no press is ever seen, and the menu stands
  there until something else closes it. Held down and outside says the same
  thing and cannot be missed the same way.
- Keep the widget on top while a menu is open, and put the menu back in front
  of it instead. The widget was held out of the topmost band for the life of
  the menu, which does settle the order between the two - but a menu here stays
  open across the switches used in it, and for all of that time the widget was
  behind every ordinary window on the screen. Clicking anything, or simply
  having clicked something before opening the menu, therefore made the widget
  vanish until the menu was dismissed, which is a far worse fault than the
  flicker it was fixing. The order is checked instead, and the menu re-stated
  only on the rare tick where the widget has actually climbed over it.
- Put the widget back on top when it has fallen out of the topmost band without
  being asked to, and let the tray icon do the same on the first click instead
  of hiding a widget that is on screen but behind everything. A widget in the
  wrong band looks gone, so the click meant to fetch it back hid it and the
  icon appeared to need two.
- Stop the menu shivering as the pointer runs down it. The order was re-stated
  on the menu and on every open sub-menu twenty-five times a second, which was
  needed only while the widget could climb over them between ticks; out of the
  band there is nothing to re-state, so the order is now checked and left alone.
- Put the widget back on top the hard way when the polite way does not take,
  and ask the z-order rather than the window's own style word whether it did.
  Requesting the topmost band is a no-op on a window the system already counts
  as being in it, and the style bit is stale in exactly that case, so the
  question "am I still on top" was being answered wrongly on every menu close.
  Answered from the bit, the widget was dropped out of the band and back to
  correct a fault that was not there, which is a blink; answered not at all,
  it really did stay behind everything, and a click in another program buried
  it. Everything in front of a window that is really topmost is topmost too,
  so one normal window ahead of it settles the question and nothing else is
  moved.
- Stop a menu closing on an activation change. This program gives up the
  foreground on purpose - the click catcher takes a click without activating,
  and a pinned widget passes the click through to whatever is underneath - so
  activation moves away constantly while a menu is plainly still in use, and
  the menu's own entries cause it as well. This was guarded only for the
  duration of the entry's work until the log showed the notice arriving 367ms
  after the click, long after that had finished. Nothing is lost by ignoring
  it: the menu is dismissed by a press anywhere off it, which is polled from
  the buttons themselves and does not care who holds the foreground.
- Let a left click through a pinned widget everywhere, including over the
  readings. The click used to be caught and then posted on to the window
  underneath, which only works for a program that reads its message queue; a
  game reads the device, so the click was swallowed by an overlay that told
  nobody. Nothing of this program stands in the mouse's way now, so the click
  lands on whatever is underneath as ordinary input, by the same route it would
  have taken had the widget not been there.
- Take the whole widget out of the mouse's way rather than answer for it. Two
  things were in the way and each needed its own answer. The widget itself was
  answering from its own window procedure, and a window is only asked about the
  pixels it owns - the readings are controls, windows in their own right, so
  over the one place the numbers are, nothing of the widget was ever asked. The
  click catcher was shaped like those same readings and had to hold the right
  button, so it went on taking every button; declaring a press "not mine" does
  not help there either, because the search that answer restarts only walks
  windows belonging to the same program, and the window the click was meant for
  belongs to another one. Both are now stood down by a window flag that the
  system reads before any of this, and which covers a window's whole tree.
- Keep the right click on a pinned widget without keeping a window to catch it,
  by taking that press from the mouse itself and swallowing it, so the program
  underneath does not open a menu of its own at the same time. The catcher is
  cut down to the pin, which stays clickable as the way back out; the wheel
  over a pinned widget now belongs to whatever is underneath, as the buttons
  already did.
- Come back the size it was left, with the top bar as it was left. Restoring
  settings marks itself as such so that replaying a stored value is not counted
  as the user changing it - and the opacity slider cleared that mark instead of
  putting it back, halfway through the restore. Everything replayed after
  opacity therefore looked like a hand on the window, and the compact size that
  had just been read was saved over with whatever the default layout came out
  at, before it was ever applied: a widget that came back a little taller after
  every restart, showing captions that had been put away.
- Keep the middle click on a pinned widget, so opacity can still be set there,
  and let the wheel set it while the card that click opens is up. Pinned,
  nothing of this program is in the mouse's way any more, so both are taken
  from the mouse itself - and swallowed, so the program underneath does not
  also act on them. With the card closed the wheel goes on belonging to
  whatever is underneath, as the buttons do.
- Keep the menus out of the taskbar. A menu is a window of its own, and these
  were not marked as belonging to the program that owns them, so opening one
  put a second Traymetry button in the taskbar for as long as it stood.
- Stop a pinned widget re-cutting its click catcher on every layout pass.
  Setting a window region repaints everything behind the window; the unpinned
  shape was compared before being applied and the pinned one was not, so
  pinning made the desktop underneath repaint twenty-five times a second for a
  shape that had not changed.
- Record how much processor time a stall actually cost, and what the machine's
  memory looked like at that moment. A gap of a second that cost twenty
  milliseconds of processor time is a widget that was not being run, which is a
  different fault from a widget that was busy, and the log could not tell them
  apart.

## 0.9.0-preview.95

- Actually stop the widget flashing over the menu. Opening a sub-menu put the
  widget directly below that sub-menu, which is above the menu the sub-menu
  hangs off: running the pointer down the entries lifted the widget over the
  menu until the next tick put it back. The widget is placed below the root
  menu now, which is below every menu window there is.
- Give the widget the foreground back when the opacity card is closed while it
  holds it. Hiding the window that is in front left the program with no active
  window, and the next right click was spent getting one rather than opening
  the menu.

## 0.9.0-preview.94

- Stop the widget flashing over the menu as the pointer runs down entries that
  open a sub-menu. The tree was walked for handlers once at startup, and the
  card, graph and colour lists are rebuilt every time they open: each rebuilt
  branch appeared without one and spent a tick underneath the widget.
- Give a sub-menu the colours of the menu it belongs to. A drop-down is a
  window of its own and was opening pale against a dark parent.
- Make supporting Traymetry one entry that opens one page, rather than a
  sub-menu that asks which payment service to use before saying what any of
  them are.

## 0.9.0-preview.93

- Close the context menu when the click that dismisses it lands anywhere else.
  A drop-down goes when its owner loses activation, but a right click on the
  widget need not have made Traymetry the foreground program - over a
  backgroundless widget it deliberately does not - and a click in another
  program never enters this one's queue. The menu stood until the widget was
  clicked again. The opacity card had the same gap and closes the same way now.
- Let the wheel set opacity over a pinned widget once the widget is the thing
  that was clicked last. Pinned, the scroll still belongs to whatever is
  underneath until then.
- Open the opacity card on the first middle click rather than the second. The
  click that opens it also activates the widget, and the card was treating its
  own opening as a reason to close.

## 0.9.0-preview.91

- Start on the defaults when a stored setting cannot be read. Every value under
  `HKCU\Software\Traymetry` is now converted defensively and window sizes are
  held between their smallest working size and the desktop; one value of the
  wrong type used to throw out of the constructor, which is a widget that
  cannot be started again without regedit.
- Keep a setting that cannot be written from ending the session: the wheel saves
  from inside the message filter, where an exception was fatal.
- Leave the account name out of the problem report. The log and any stack trace
  carry the profile path, and the report is written to be handed to someone
  else.
- Stop leaking a window handle and a font for every row of the cheat sheet each
  time the language is switched.
- Only ever hand an `https` address to the shell from the donation entries.
- Read the processor name and clock without throwing where policy denies the
  hardware registry hive, and make disposing a telemetry session twice safe.
- Print the pin and hide combinations in the cheat sheet from what they are set
  to; two leftover strings still named the shipped defaults.
- Drop the debug rendering harness, the unused payload check and seven dead
  strings from the build.

## 0.9.0-preview.84

- Draw the window with per-pixel alpha instead of a colour key, so antialiased
  text keeps its soft edges on light desktops. This is now the default; the
  colour-keyed path stays available as `--classic`.
- Hold the layered frame buffer in a DIB section instead of allocating a new
  bitmap for every frame. The old path faulted in the whole window several times
  a second, which showed up as stalls after idle and, when expanded over a large
  screen, as GDI+ refusing to draw at all.
- Let a refused frame drop instead of closing the widget.
- Make every hotkey configurable, including Escape and F1, with a capture dialog
  that sees the keys the widget itself has registered, and a reset to defaults.
- Save presets on an explicit "Save" rather than capturing every hand edit, for
  cards, graphs and the colour palette alike.
- Keep the context menu above the widget in every mode, including backgroundless
  and while pinning from the menu itself.
- Show only the pin button while pinned, and let the top bar follow its own
  visibility setting regardless of the pin.
- Collect a problem report from the menu, or with `--report` when the widget will
  not start; keep a rotating log in `%LOCALAPPDATA%\Traymetry`.
- Drop "expand to full view" from the context menu: the bottom strip and a double
  click already do it.

## 0.9.0-preview.37

- Preserve both manual and automatic upper-panel visibility across restarts.
- Add background and manual GitHub Releases update checks.
- Verify update assets by SHA-256 and replace the EXE atomically with rollback.
- Add updater self-test to local and GitHub Actions builds.
- Add a support entry point for voluntary donations.
- Download the official PawnIO installer on first consent instead of
  redistributing it inside Traymetry; keep pinned hash and signer checks.
- Add reproducible release packaging, checksums and artifact attestation.
