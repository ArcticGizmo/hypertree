# Update checks as Windows notifications

## The problem

Checking for updates used to be a *surface*. "Check for updates" summoned a `NoticeContent` card onto
the shared `OverlayStage`: a full-screen dim over the desktop, a "Checking…" card that absorbed every
click, then a result card you had to dismiss. For an action whose answer is "you're up to date" nine
times out of ten, that's a modal interruption to be told nothing happened.

The check should be ambient. You ask, you carry on working, Windows tells you the answer.

## The shape

The flow reports through the Action Center. Nothing opens, nothing dims, nothing takes the pointer.

| State | Notification | Click |
| --- | --- | --- |
| Checking | "Checking for updates… / Looking for a newer release of Hypertree." (silent) | — |
| Up to date | "You're up to date / Hypertree vX is the latest release." | — |
| Available | "Update available — vY / You're on vX. Click to download it and restart Hypertree." | downloads + installs |
| Dev build | "Update checks need an installed build / …" | — |
| Feed unreachable | "Couldn't check for updates / …" | — |
| Downloading | "Downloading vY / Hypertree will restart itself once the update is installed." (silent) | — |

Two details make the sequence read as one event rather than a pile:

- **Every step shares a replace key** (`INotifier.Show(replaces:)` → toast tag + group). Checking →
  result → downloading update *one* notification in place, so a check leaves a single Action Center
  entry showing its outcome, not a trail of three.
- **The progress steps are silent.** A result follows moments later; one check shouldn't chime twice.

## Why WinRT toasts — and the balloon that looked cheaper

The obvious cheap route for a tray app is a balloon: `Shell_NotifyIcon` with `NIF_INFO`. Windows 10
rendered those as toasts, and the click comes back to the icon's own window as `NIN_BALLOONUSERCLICK` —
no AppUserModelID, no shortcut, no COM activator.

**It does not work on Windows 11 25H2.** Measured on build 26200, with a purpose-built probe:

- `NOTIFYICONDATAW` marshalled to exactly 976 bytes with every field offset matching the native layout,
  so the struct was not the problem.
- `NIM_ADD`, `NIM_SETVERSION`, `NIM_MODIFY | NIF_INFO` and `NIM_DELETE` all returned `TRUE`, last error 0.
- The shell went as far as auto-creating a `NotifyIconGeneratedAumid_*` sender identity for us, with
  default (enabled) settings — so the notification platform genuinely received the balloon.
- Nothing rendered. Four variants — icon version 4 with a custom icon, version 4 with the system glyph,
  legacy version, legacy with icon/tip resent — produced no banner in any frame of a 1.1s-interval
  sweep, and no Action Center entry.
- Not environmental: Do Not Disturb was off and other apps' notifications arrived throughout.

Balloons are accepted and silently discarded. Anything relying on them is untestable-by-inspection —
every return code says success.

So the update flow uses `Microsoft.Toolkit.Uwp.Notifications` (`ToastNotificationManagerCompat`),
matching perch. For an unpackaged Win32 app the shim registers the AppUserModelID *and* a COM activator
on first use, which removes what used to make this route expensive: no Start Menu shortcut to
hand-write, no registry setup, and no clash with the shortcut Velopack installs. Subscribing to
`OnActivated` is what stands the activator up, so a click reaches the already-running tray instance.

Costs, for the record:

- **TFM**: `Hypertree.App` moved to `net10.0-windows10.0.19041.0` (the toolkit's floor). Only the app
  head — Core and Platform.Windows are untouched.
- **A pinned `System.Drawing.Common`**: the toolkit pulls 4.7.0 transitively, which carries
  GHSA-rxg9-xrhp-64gj. A direct reference to a current version overrides it away (as perch does).
  Hypertree doesn't use the package itself.
- **`WindowsToastNotifier` lives in the app head**, not `Hypertree.Platform.Windows`, because the
  package needs the Windows 10 SDK target that project doesn't carry. `INotifier` is in Core, so the
  seam for a future non-Windows head is still where it should be.

Avalonia was never a candidate: it ships `WindowNotificationManager` (in-app cards) and no OS
notification API.

## The sender's name

The shim derives the toast sender name from the executable, which yields a lowercase "hypertree" on
every notification. There's no API for it, so `NameTheSender` corrects `DisplayName` on the
`AppUserModelId` key the shim just wrote — our own app's identity, under HKCU. If the shim ever changes
its AUMID derivation, the write finds nothing and the name stays lowercase; nothing else depends on it.

## Surfaces

One flow, four ways in, and they agree:

- **Tray menu** — "Check for updates", retitled to "Update now — vY" once a check has found one.
  Avalonia's `NativeMenu` has no "about to open" hook, so `RefreshUpdateMenuItem` updates the item when
  the state changes rather than when the menu opens.
- **Command palette** — unchanged behaviour, now notifying instead of carding.
- **Notification click** — applies the update directly (`action=update`, routed via `INotifier.Activated`).
- **Settings** — its buttons drive the *same* flow, so a check started there raises the same
  notifications; the window also mirrors each state in its inline caption, because a window you
  deliberately opened is a fine place to read the detail. `UpdateHooks` is the seam.

The result of a check is remembered in `_lastUpdate` either way, so a notification you never saw still
leaves "Update now — vY" waiting in the tray menu and the palette.

## Known limits

- **Notifications off, or Focus Assist on** → nothing is shown. Unlike balloons, toasts at least
  persist in the Action Center, and the tray menu, palette and Settings all still offer the update.
- **The AUMID is the executable path**, so it changes if the exe moves. An installed build lives at a
  stable `current\hypertree.exe`, but a dev build's path shifts with the TFM or configuration — when it
  does, Windows treats it as a new app: fresh sender identity, and the tray icon starts in the overflow
  again.
- **Click-to-install can only be exercised in an installed build.** A dev build isn't Velopack-installed,
  so a check there always lands on `NotApplicable` by design.
