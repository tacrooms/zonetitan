# Spec addendum — generic game detection (revises §7)

> ## ⚠ SHELVED — not being built
>
> **Decided 2026-08-01: process-based game detection is dropped entirely**, along with §7's
> manual watchlist. Input-device routing on wake
> ([addendum-input-device-routing.md](addendum-input-device-routing.md)) is ZoneTitan's sole
> automation trigger.
>
> Rationale: everything below is heuristic. Store paths move, anti-cheat blocks image-path
> resolution, and false positives need ongoing pruning forever. Device routing achieves
> most of the same outcome with an exact device-path match and no guessing. Trading a
> permanent maintenance burden for a deterministic signal is a good trade.
>
> This document is retained for its analysis — chiefly §1, on why fullscreen-D3D detection
> is the wrong signal — in case the decision is ever revisited.

*Original status: proposed, not implemented. Decided 2026-08-01.*

**Request.** A watchlist of individual exes is too narrow. Any game launching should
activate the `game` zone — and, most importantly, quitting any game should revert.

---

## 1. The problem

Windows has **no API that answers "is this process a game."** Nothing to call, no flag on the
process. Every tool that does this is running heuristics, and each heuristic has a
characteristic failure mode. This section is deliberately blunt about that, because the
design below is a layered guess, not a lookup.

The naive candidate — detect a fullscreen Direct3D app via `SHQueryUserNotificationState`
returning `QUNS_RUNNING_D3D_FULL_SCREEN` — is attractive and cheap, and it is **the wrong
primary signal** here. It fires only once the game has already gone fullscreen exclusive,
and spec §14 warns exactly against that: changing topology underneath a fullscreen game
makes it minimise or mode-flip. Detecting at fullscreen means always switching at the worst
possible moment.

**Detection must therefore happen at process start, before the game initialises its
swapchain.** That constraint drives everything below.

## 2. Proposed detection ladder

Evaluated when a *new* process appears. First match wins.

1. **Manual rules** — the §7 watchlist, kept as an explicit override in both directions
   (always-game, never-game). This stays the escape hatch when heuristics get it wrong.
2. **Install-path match** — the process image lives under a known game root. Defaults
   detected once and then user-editable:
   `Steam\steamapps\common`, `Epic Games`, `GOG Galaxy\Games`, `Battle.net`, `XboxGames`,
   `Riot Games`. This is **path-prefix matching, not catalogue parsing** — no manifest
   readers, no launcher APIs, no per-store logic.
3. **Windows' own game classification** — `HKCU\System\GameConfigStore\Children`, which is
   where Game Bar records titles Windows has classified as games. Free signal, no admin.
4. **Fullscreen D3D** — `QUNS_RUNNING_D3D_FULL_SCREEN`, **opt-in only**, and useful mainly
   as a late confirmation for a game that slipped through 1–3. Off by default, for the
   ordering reason above.

### Why this shape

Layer 2 does the heavy lifting: essentially every real game lives under a store directory,
it is knowable at process start, and prefix matching is trivial to implement and to debug.
Layer 3 catches games installed outside those roots that Windows already knows about.
Layer 1 fixes whatever the other two get wrong, permanently.

## 3. Handling false positives — the important UX

Generic detection *will* misfire: a launcher's own updater under a game root, a mod tool,
a video player Windows classified oddly. Rather than making you curate a list up front,
make correction a single click at the moment it happens.

When a newly-seen exe first trips the heuristic, the OSD toast announces the switch and
offers a dismissal that means *never again*:

```
  🎮 game  ·  Frostpunk3.exe
  Not a game?  [Never switch for this]
```

Clicking it writes a never-game rule (layer 1) and reverts immediately. One click, one
permanent fix, no settings dive. This inverts §7's original model: instead of building a
watchlist by hand, you prune a heuristic on the rare occasions it is wrong.

## 4. Implementation notes

- **Don't poll `GetProcessesByName`** — that only works when you already know the name.
  Poll `Process.GetProcesses()` on the §7 interval and diff by PID to find *new* processes;
  resolve the image path only for those. Cost stays flat as the watch set grows to "everything."
- **`Process.MainModule` throws on many games.** Anti-cheat and protected processes deny the
  handle rights it needs. Use `QueryFullProcessImageName` with
  `PROCESS_QUERY_LIMITED_INFORMATION`, which succeeds in most cases where `MainModule` fails.
  Treat an unresolvable path as "not a game" rather than guessing.
- **PID reuse is real.** Track sessions by (PID, process start time), not PID alone, or a
  recycled PID can suppress the revert.
- **Exit and revert are unchanged from §7** — triggering process exits → 10s grace →
  revert to `revertTarget`. This is the behaviour that matters most day to day, and it gets
  *more* reliable here, not less: the session is bound to the specific process that
  triggered it, so quitting the game reverts even if a launcher lingers.

## 5. Spec impact

- **§7** — detection section replaced by the ladder above. Session, idempotence,
  manual-override-wins, grace period and pause-from-tray semantics all carry over unchanged.
- **§2** — "no launcher library scanning in v1" needs softening. Path-prefix matching
  against store directories is much lighter than the catalogue parsing that non-goal was
  written to exclude, but it is honestly a step toward it. Recommend restating the non-goal
  as "no launcher catalogue/manifest parsing" and explicitly permitting install-path
  heuristics.
- **§13** — acceptance test 9 gains cases: a game installed under a store root activates
  the zone without any rule existing; a never-game rule created from the toast suppresses
  it permanently; a game whose path cannot be resolved does not activate the zone.
- **Milestone** — still M5, alongside input-device routing.
