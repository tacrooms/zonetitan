# Spec addendum — input-device zone routing

*Status: proposed, not implemented. Depends on M2 (engine) and M3 (message-only window).*

**Request.** When the machine wakes, pick the zone from which input device was used:
wireless mouse/keyboard → `studio`; wired mouse/keyboard → `game`, and launch the game.

---

## 1. Hardware found on this machine

Enumerated 2026-08-01 via `Get-PnpDevice`.

Only devices reporting `Status: OK` are actually present. Mice, in full:

| Device | Transport | Identity | Likely role |
| --- | --- | --- | --- |
| Areson receiver | 2.4 GHz dongle | VID `25A7` PID `FA61` | wireless — candidate Tecknet |
| Areson receiver | 2.4 GHz dongle | VID `25A7` PID `FA70` | wireless — candidate Tecknet |
| Holtek composite | Wired USB | VID `04D9` PID `FC38` — mouse `MI_00`, keyboard `MI_01` | the wired Redragon |

Keyboards additionally include a Corsair (VID `1B1C` PID `1B3D`) plus the keyboard
interfaces exposed by the Areson receivers and the Holtek composite.

**There is no Bluetooth mouse connected.** The machine's complete Bluetooth pairing list is
a Logitech LIFT, an iPhone, and two DualSense controllers — no Tecknet record of any kind.
The LIFT's mouse node reads `Status: Unknown` (absent); only its radio-level pairing entry
lingers. The user reports the LIFT was removed, which matches.

So the Tecknet (MG2601 or similar) is reaching the machine through **one of the two 2.4 GHz
Areson receivers**, not Bluetooth. Which of the two is unresolved — determine it by running
`Get-PnpDevice -Class Mouse -Status OK` with the mouse on, then off, and diffing.

The Redragon is likewise an **inference**: `04D9` is Holtek, a controller vendor Redragon
commonly uses, and the device exposes the mouse + keyboard composite interface a wired
gaming mouse presents. Confirm by unplugging it and re-running before binding a rule.

### Consequence for rule matching

The stale LIFT entry is a warning worth heeding: a rule bound to "the Bluetooth mouse"
would have matched a ghost device that is no longer on the desk. Input-device rules need
the same discipline §4 imposes on monitors — **match only against devices currently
reporting present, and surface a clear error when a rule's device is missing rather than
silently doing nothing.** Rules should be created from a picker that lists live devices,
never typed from memory.

## 2. The wake-source approach does not work here

The obvious implementation — ask Windows which device woke the machine — was tested and
**fails on this hardware**:

```
powercfg /lastwake        →  Wake History [0]: Wake Source Count - 0
Event log, last 8 wakes   →  WakeSourceType=0, DeviceFriendlyName='', DeviceInstancePath=''
```

Eight consecutive wake events spanning 2026-07-17 to 2026-08-01, every one unattributed.
Windows knows the machine woke; it does not record what woke it. This is common for
HID wake from S3 — the firmware signals a generic controller wake rather than naming the
endpoint — and no amount of application code recovers information the OS never captured.

The system is otherwise in the *good* configuration for this (real S3 standby, not modern
standby; wake-armed HID devices present), so this is not a fixable settings problem.

**Conclusion: do not build on `powercfg` / Power-Troubleshooter wake attribution.**

## 3. Proposed design — first-input routing

Route on **which device you actually touch on resume**, not on what woke the box. This is
both implementable and a closer match to the real intent: the zone should follow the input
device in your hand.

1. ZoneTitan's hidden message-only window (already required by §6 for hotkeys) registers for
   raw input: `RegisterRawInputDevices` for HID mouse and keyboard usage pages, with
   `RIDEV_INPUTSINK` so input is received without focus.
2. It listens for `WM_POWERBROADCAST` / `PBT_APMRESUMEAUTOMATIC` — resume from sleep.
3. On resume, arm a **routing window** of ~10 seconds.
4. The first `WM_INPUT` message during that window carries an `hDevice`; resolve it via
   `GetRawInputDeviceInfo(RIDI_DEVICENAME)` to a device instance path.
5. Match that path against a user-defined rule list, `{ deviceInstancePath → zone }`, using
   the same matching discipline as monitors: exact instance path first, VID/PID fallback.
6. Activate the mapped zone. If no rule matches, or the window expires with no input, do
   nothing — the current zone stands.

Advantages over wake-source: works regardless of what woke the machine; also covers
resume-from-display-off and unlock, which have no wake source at all; and it degrades
safely — no match simply means no change.

The honest tradeoff: if the wireless mouse wakes the machine but you then reach for the
Redragon, you get `game`. That is almost certainly the behaviour you want, but it is a
behaviour choice worth stating rather than discovering.

Note this design is indifferent to transport — it matches on device instance path, so it
works identically whether the wireless mouse is Bluetooth or on a 2.4 GHz receiver. The
wired/wireless split you asked for survives the correction above unchanged.

## 4. Decisions

**Auto-launching a game on wake is dropped** (decided 2026-08-01). The original request
paired device routing with launching the game; routing alone is the valuable half. This
keeps spec §2's "no game launching" non-goal intact, and removes the footgun where waking
at 03:00 with the wrong mouse would have started a game. ZoneTitan sets the stage; you
press play.

**Process-based game detection is dropped entirely** (decided 2026-08-01) — both §7's manual
watchlist and the generic-detection proposal. Device routing is now ZoneTitan's **sole
automation trigger**. See
[addendum-generic-game-detection.md](addendum-generic-game-detection.md) for why.

### Still open

- **Manual override.** A device-routed activation is an automated action, so a manual zone
  hotkey afterwards should stand and not be undone by anything later. With §7 gone there is
  no revert timer to cancel, so this reduces to: never move the zone except on a resume event.
- **Whether device routing should revert on sleep.** No — the next wake routes again anyway,
  so a revert would just cause an extra topology change. Default: no revert.

## 5. Complete behaviour

**The whole rule: zones change on wake, or when a hotkey is pressed. Never anything else.**
ZoneTitan does not know or care whether a game is running.

### Automatic — on wake, nothing to press

| Wake the PC using… | Zone activated | Monitors on |
| --- | --- | --- |
| **Wired** mouse or keyboard | `game` | OLED only |
| **Wireless** mouse or keyboard (2.4 GHz dongle) | `studio` | Left + Right |
| Nothing touched within the ~10s routing window | unchanged | whatever was last active |

### Manual — hotkeys, any time

| Press | Result |
| --- | --- |
| `Ctrl+Alt+G` | `game` — OLED only |
| `Ctrl+Alt+S` | `studio` — Left + Right |
| `Ctrl+Alt+A` | all three monitors |
| `Ctrl+Alt+F12` | failsafe — force every monitor back on |

### Worked example — a gaming session

| Step | User action | ZoneTitan |
| --- | --- | --- |
| 1 | Wakes the PC with the wired mouse | Activates `game` automatically |
| 2 | Plays | — |
| 3 | Quits the game | **Nothing.** No process is being watched |
| 4 | Presses `Ctrl+Alt+S` | Activates `studio` |
| 5 | Later presses `Ctrl+Alt+G` | Activates `game` |

A hotkey press is always final — nothing reverts it later. There is no revert timer anywhere
in the design.

## 6. Spec impact

- **§7 (Automation)** — replaced wholesale by this document. No process polling, no
  watchlist, no grace period, no revert timer.
- **§2 (Non-goals)** — simplifies. "No launcher library scanning" becomes moot; add
  "no process monitoring" as a stated non-goal.
- **§0 (Licensing)** — the clean-room concern largely evaporates. The apply → launch → watch
  → revert pattern was the one behavioural thing taken from DisplayMagician, and ZoneTitan no
  longer implements it. The courtesy credit stays (the lineage of thinking is real), but there
  is now no DisplayMagician-shaped behaviour in the product at all.
- **Mission statement** — "DisplayMagician's game-aware lifecycle" no longer describes this
  product. ZoneTitan is now *monitor zones with device-aware wake routing*. Worth restating
  in the spec header.
- **§13 (Acceptance tests)** — test 9 is replaced: wake on the wired mouse activates `game`
  within the routing window; wake on the wireless mouse activates `studio`; wake with no
  input inside the window changes nothing; a rule whose device is absent errors visibly.

## 7. Where this lands

**M5**, and M5 is now considerably smaller — one resume handler and a raw-input listener
rather than a process watcher with session lifecycle management. It needs the message-only
window from M3 and the engine from M2, so it should not be attempted before those exist.
