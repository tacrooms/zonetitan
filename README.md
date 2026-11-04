# ZoneTitan

Monitor zones with device-aware wake routing, for Windows 11.

Define **zones** — named subsets of your monitors — and switch between them instantly with a
global hotkey. Wake the machine with your wired mouse and the `game` zone comes up; wake it
with the wireless one and you get `studio`. Monitors that aren't in the active zone are
detached from the desktop topology, so they drop to standby and stay dark, which keeps an
OLED safe from burn-in.

> **Status: M1–M6 complete.** Tray app, global hotkeys, config UI, wake routing and
> single-file publish are all in. See [Milestones](#milestones) for what is verified and what
> is not.

---

## Everyday use

Run `ZoneTitan.exe` and it lives in the tray. Switching zones is a hotkey, the tray menu, or
a shortcut — the config window is for setup only.

| | Default | |
| --- | --- | --- |
| `game` | `Ctrl+Alt+Shift+G` | OLED alone, 4K 240 Hz, HDR on |
| `studio` | `Ctrl+Alt+Shift+S` | side monitors, ULTRAGEAR primary |
| `all` | `Ctrl+Alt+Shift+D` | everything on |
| failsafe | `Ctrl+Alt+F12` | force every monitor back on |

All rebindable in the config window. A combination another app already owns is reported at
bind time, never left silently dead.

Because a second invocation forwards its arguments to the running instance over a named pipe,
any external trigger works without extra plumbing — AutoHotkey, a Stream Deck button, a
desktop shortcut:

```bash
ZoneTitan.exe --zone game
```

## Build

Requires the .NET 8 SDK.

```bash
dotnet build ZoneTitan.sln -c Release
```

Single-file, self-contained executables:

```bash
dotnet publish src/ZoneTitan.App -c Release -r win-x64 -p:PublishSingleFile=true --self-contained -o publish/app
```

## Run the M1 enumeration spike

```bash
dotnet run --project src/ZoneTitan.Cli -c Release
```

Or run the built binary directly:

```bash
.\src\ZoneTitan.Cli\bin\Release\net8.0-windows\win-x64\zonetitan.exe
```

| Flag | Effect |
| --- | --- |
| *(none)* | Print the attached-monitor report |
| `--json` | Emit the same data as JSON |
| `--modes` | List every mode each attached monitor offers |
| `--init [--force]` | Generate `config.json` from current hardware |
| `--zones` | List configured zones |
| `--zone <name>` | Activate a zone |
| `--failsafe` | Turn every attached monitor back on |
| `--restore-last` | Reapply the last known good layout |
| `--hide-taskbar` | Re-enable taskbar auto-hide once |
| `--guard-taskbar` | Keep re-enabling it whenever Windows drops it |
| `--help` | Usage |

### Taskbar auto-hide — off by default, and why

Windows 11 drops the taskbar auto-hide flag on sleep/wake and display changes. On an OLED
that matters: a permanently visible taskbar burns into the same rows of pixels.

ZoneTitan can put it back, but **this is disabled by default** and should stay that way
unless you have tested it. Setting taskbar state through `SHAppBarMessage` can make Windows
raise a modal dialog — *"A toolbar is already hidden on this side of your screen. You can
have only one auto-hide toolbar per side."* On this machine that dialog appeared seconds
after ZoneTitan re-asserted auto-hide, and did **not** appear across two zone switches with
the feature off. The evidence points at the re-assert itself as the trigger.

If you enable it anyway (`"enforceTaskbarAutoHide": true`, or the tray/footer toggle), the
guard is deliberately timid: a 60-second heartbeat, a 30-second cooldown between attempts,
and it switches itself off entirely after three restores in five minutes on the assumption
that something is fighting it. `--guard-taskbar` in the CLI behaves the same way.

### If your screens go dark

```bash
.\src\ZoneTitan.Cli\bin\Release\net8.0-windows\win-x64\zonetitan.exe --failsafe
```

This path does no config parsing and no snapshot loading — it queries attached monitors and
turns them all on. It is deliberately the simplest code in the engine.

The report lists every **attached** monitor, active or detached, with its device path,
current and preferred mode, position, rotation and HDR state.

### Verifying monitor identity

ZoneTitan identifies monitors by `monitorDevicePath` — never by adapter LUID, GDI name
(`\\.\DISPLAY1`), or Windows' display numbering, all of which change across reboots and
driver restarts.

To confirm this yourself: run the tool, reboot, and run it again. The **device paths must be
byte-identical**; the adapter LUIDs, GDI names and source/target ids are free to change. The
report prints both groups side by side, labelled, so the comparison is easy to eyeball.

Verified on this machine across a reboot (2026-08-01):

| | Before reboot | After reboot | |
| --- | --- | --- | --- |
| Device paths | `…UID4352 / 4353 / 4355` | `…UID4352 / 4353 / 4355` | **identical** |
| Adapter LUID | `00000000:00014CE4` | `00000000:00014AF0` | **changed** |
| GDI names | reassigned | reassigned | **changed** |

That is the entire argument for device-path identity in one table: the thing ZoneTitan
persists held still, and both things it refuses to persist moved.

---

## Repository layout

```
src/ZoneTitan.Core/    Display engine + automation. No UI dependencies, unit-testable.
src/ZoneTitan.Cli/     Console harness (M1/M2).
reference/             Upstream clones for reading only. Git-ignored, never distributed.
```

## Milestones

| | Milestone | State |
| --- | --- | --- |
| M1 | Enumeration spike (console) | **done** |
| M2 | Engine — ApplyZone, identity re-mapping, snapshots, failsafe | **done** |
| M3 | Tray + hotkeys + OSD | **done** |
| M4 | Config UI + design language | **done** |
| M5 | [Device-aware wake routing](docs/addendum-input-device-routing.md) | **done, not yet verified across a real sleep/wake** |
| M6 | Polish, single-file publish | **done** |

---

## Recommended Windows setting

ZoneTitan deliberately does **not** manage window positions. Windows 11 already does this
well — enable it once and forget it:

**Settings → System → Display → Multiple displays → "Remember window locations based on
monitor connection"**

With that on, windows return to where you left them when a zone re-attaches a monitor.

---

## Credits & lineage

ZoneTitan stands on two excellent open-source projects:

- **[DisplayProfileManager](https://github.com/zac15987/DisplayProfileManager)** by zac15987 (MIT) —
  ZoneTitan's display engine ports and adapts DPM's Windows CCD API helpers.
  See THIRD-PARTY-LICENSES.md for the full license and copyright notice.
- **[DisplayMagician](https://github.com/terrymacdonald/DisplayMagician)** by Terry MacDonald (GPL-3.0) —
  ZoneTitan's game-aware automation (apply → launch → revert) is an independent,
  clean-room implementation inspired by DisplayMagician's workflow. No DisplayMagician
  code is used. DisplayMagician itself continues the lineage of HelioDisplayManagement
  by Soroush Falahati — respect to the whole chain.

ZoneTitan is a personal project and is not affiliated with either upstream author.
