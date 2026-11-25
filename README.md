# Damage Tracker

[简体中文](README.zh-CN.md)

Damage Tracker is a Slay the Spire 2 mod (community fork based on [BAIGUANGMEI/STS2-DamageTracker](https://github.com/BAIGUANGMEI/STS2-DamageTracker)) that shows a draggable, resizable in-combat overlay with per-player damage statistics for the current run.

The overlay is designed for multiplayer and solo runs and supports:

- expanded mode, compact mode, and side-hidden mode with a restore tab
- clickable column sorting (`%`, `Total`, `Combat`, `Turn`, `Last`, `Max`)
- damage source breakdown (`Direct`, `Poison`, `Doom`, `Pet / Orb`, `Other`) and Top 3 highest-damage cards in row hover tooltips
- completed combat history switcher (`Run` + each completed fight with encounter name)
- one-click battle report copy to clipboard (with Damage MVP and Finisher callouts)
- anti-misclick controls: click-through lock (`F9`), master visibility toggle (`F8`), out-of-combat auto-dim, and background opacity presets
- free window resizing via a bottom-right drag grip and `Ctrl + Mouse Wheel` UI scaling (`0.7x` – `2.5x`)
- persistent UI layout settings and backward-compatible run state checkpoints
- resilient teammate row rendering even when character textures unload

Current version: `0.5.0`

## Features

- Track total run damage, current combat damage, current turn damage, last hit, max hit, kills, and overkill damage per player
- Classify damage by source (`Direct`, `Poison`, `Doom`, `Pet / Orb`, `Other`) — including turn-start & card-triggered Poison (`Outbreak`), Necrobinder pets (`Osty`), and orb passive/evoke damage — while filtering out player self-damage
- Show Top 3 highest-damage cards and source shares in row hover tooltips (for both Run and Combat views)
- Switch between live `Run` stats and completed combat history snapshots (`‹` / `›`)
- Click any stat column header in expanded mode to sort descending/ascending
- Copy a clean plain-text battle report (with Damage MVP & Finisher awards) to the clipboard in one click
- Highlight the currently active player with character theme colors, damage share bars, and a brief gold border flash on new personal max hits
- Lock overlay in full click-through mode (`F9` or lock button), toggle visibility (`F8`), enable out-of-combat auto-dim, and cycle background opacity (`0.70` / `0.45` / `0.88`)
- Drag-and-drop repositioning, bottom-right grip resizing (double-click to reset), and `Ctrl + Mouse Wheel` UI scaling (`0.7x` – `2.5x`)
- Persist UI layout (`scale`, `width`, `compact_width`, `height`, `x`, `y`, `expanded`, `locked`, `auto_dim`, `background_opacity`) in `user://damage_tracker_ui.json`
- Built-in English & Simplified Chinese fallback localization (overrides still loaded from `assets/localization/{lang}/damage_tracker.json`)

## Installation

Download the latest release from the [Releases](https://github.com/dsadsada429/STS2-DamageTracker/releases) page and extract it into the game's `mods` folder.

Included files:

- `DamageTracker.dll`
- `DamageTracker.pck`
- `DamageTracker.json`

Expected layout:

```text
Slay the Spire 2/
	mods/
		DamageTracker/
			DamageTracker.dll
			DamageTracker.pck
			DamageTracker.json
```

## Usage

- Enter a combat to create the overlay
- **Move & Resize**: drag the panel with left mouse button; drag the bottom-right grip to resize (double-click grip to reset defaults); hold `Ctrl` + scroll wheel over the panel to scale (`0.7x` – `2.5x`)
- **Hotkeys**: press `F8` to show/hide the overlay; press `F9` to lock/unlock click-through mode
- **Header buttons**:
  - `Cp`: copy formatted battle report for the active view (Run or selected combat) to clipboard
  - `Op`: cycle background opacity (`70%` → `45%` → `88%`)
  - `Dm`: toggle out-of-combat auto-dim (`35%` alpha outside combat)
  - `Lk`: toggle click-through lock mode (`F9` unlocks)
  - `▼` / `▶`: switch between expanded and compact mode
  - `»` / `«`: collapse to the left or right screen edge (click the side tab to restore)
- **Scope switcher (`‹ [scope] ›`)**: browse between `Run` and completed fights in chronological order
- **Sorting & Tooltips**: click any column header (`%`, `Total`, `Combat`, `Turn`, `Last`, `Max`) to sort; hover any player row for exact numbers, kills, overkill, source breakdown, and Top 3 cards

## Damage Tracking Rules

- Direct damage is recorded from the game's actual `DamageResult.UnblockedDamage` on enemy receivers (player self-damage such as `Offering` / `Bloodletting` is excluded)
- Poison damage is captured via `PoisonPower.Trigger()`, covering both turn-start ticks and card-triggered Poison (e.g. `Outbreak`)
- Doom damage is tracked from the actual `DoomPower.DoomKill` / `Hook.AfterDiedToDoom` flow using pre-death HP
- Pet (`Creature.IsPet` / `PetOwner`) and Orb (`OrbModel` evoke & turn-end passive) damage is classified under `Pet / Orb`
- Player ownership uses typed STS2 v0.113.0 APIs (`ICombatState`, `DamageResult`, `CardModel`, `PlayerChoiceContext`) first, with `NetId == 0` hash fallback and reflection fallback when needed
- Pre-combat checkpoints prevent Save & Quit mid-combat reloads from double-counting an aborted attempt

## Project Structure

- `src/ModEntry.cs`: mod entry point, `ICombatState` Harmony patches, and Poison/Doom/Orb async context tracking
- `src/RunDamageTrackerService.cs`: damage aggregation, source/card breakdowns, combat history, checkpoints, and atomic persistence
- `src/BattleReportFormatter.cs`: plain-text battle report & MVP/Finisher formatter
- `src/ReflectionHelpers.cs`: runtime object, card title, encounter name, and damage-event resolution helpers
- `src/DamageTrackerOverlay.cs`: overlay UI, sorting, history switcher, lock/hotkeys, opacity/auto-dim, resizing, scaling, built-in localization fallback, and resilient rendering
- `assets/localization/{eng,zhs}/damage_tracker.json`: English and Simplified Chinese localization files
- `DamageTracker.csproj`: project file and game reference configuration
- `DamageTracker.json` & `mod_manifest.json`: mod metadata and manifest
- `project.godot`: Godot project configuration

## Build Requirements

- Windows
- .NET SDK 9 / 10
- Godot .NET SDK 4.5.1
- Slay the Spire 2 installed locally

Before building, set the game directory in `DamageTracker.csproj`:

```xml
<Sts2Dir>C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2</Sts2Dir>
```

The project references game assemblies from:

```text
$(Sts2Dir)\data_sts2_windows_x86_64
```

Required assemblies include:

- `sts2.dll`
- `0Harmony.dll`
- `Steamworks.NET.dll`

## Build

Run this in the repository root:

```powershell
dotnet build
```

Successful output:

```text
.godot/mono/temp/bin/Debug/DamageTracker.dll
```

The project is also configured to copy the built DLL into:

```text
Slay the Spire 2\mods\DamageTracker\
```

## Changelog

### v0.5.0

- Added damage source breakdown (`Direct`, `Poison`, `Doom`, `Pet / Orb`, `Other`) and per-player Top 3 highest-damage cards in row hover tooltips
- Added per-turn damage (`Turn`), kills, overkill tracking, and clickable column header sorting (`%`, `Total`, `Combat`, `Turn`, `Last`, `Max`)
- Added completed combat history switcher (`Run` + each completed fight with encounter name) and one-click clipboard battle report copy (`BattleReportFormatter`) with Damage MVP & Finisher awards
- Added click-through lock mode (`F9`, including internal scrollbars and docked side tab), master visibility toggle (`F8`), out-of-combat auto-dim, background opacity presets (`0.70` / `0.45` / `0.88`), and personal max-hit gold border flash
- Updated Harmony hooks to exact STS2 v0.113.0 `ICombatState` signatures, captured attribution in `AfterDamageGiven` prefix, patched `PoisonPower.Trigger()` (fixing `Outbreak`), filtered out player self-damage, and added pre-combat checkpoints to prevent Save & Quit double-counting
- Embedded built-in `eng` and `zhs` localization fallbacks alongside updated JSON files so all new UI strings work even without re-exporting `DamageTracker.pck`

### v0.4.0

- Added resizable overlay window via bottom-right drag grip (with double-click to reset defaults)
- Added `Ctrl + Mouse Wheel` UI scaling (`0.7x` – `2.5x`) with automatic UI rebuild
- Added persistent UI layout settings (`user://damage_tracker_ui.json`) remembering scale, expanded/compact widths, height, position, and expanded state
- Fixed teammate rows disappearing when character/portrait textures unload mid-run by validating `Texture2D` lifetimes, evicting stale icon cache entries, and isolating per-row errors with a fallback row
- Added compact number formatting (`k` / `M`), ellipsis text truncation, hover tooltips with exact numbers, and automatic viewport resize handling
- Fixed player key fallback when `Player.NetId == 0` to avoid collisions in offline/local sessions

### v0.3.0

- Fixed missing overlay rows after starting a new run following a previous one
- Improved run and player ownership resolution using typed STS2 APIs where possible
- Fixed character color highlighting issues
- Reworked Poison and Doom tracking to use actual resolved damage flows
- Improved overlay stability with compact mode, side-hide mode, and adaptive height

## Troubleshooting

### Build fails because game DLLs cannot be found

Verify that `Sts2Dir` in `DamageTracker.csproj` points to your actual game installation.

### Build fails because the output DLL is locked

If the game is running, `DamageTracker.dll` may be locked by `SlayTheSpire2.exe`. Close the game and build again.

### Locked panel won't respond to clicks

Press `F9` to unlock click-through mode.

### Resetting overlay position, size, or scale

Double-click the bottom-right resize grip to reset scale, width, and height, or edit/delete `damage_tracker_ui.json` in the game's user data directory while the game is closed.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.

The license applies to the mod source code and custom assets in this repository. It does not apply to the base game, commercial game assets, or third-party restricted content.
