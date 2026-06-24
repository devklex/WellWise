# WellWise

WellWise is an ExileCore2 plugin for Path of Exile 2's Well of Souls. It reads the three revealed Well choices, identifies the item being modified, and draws compact tier/range badges above each choice row.

## What It Shows

- Current roll tier for each visible Well choice.
- Prefix or suffix side for matched choices.
- Current tier range, including multi-value and hybrid stats.
- Best tier/range available for the current item class and item level.
- Higher item-level T1 requirement when the current item cannot roll the absolute top tier.
- Adjacent overlapping tiers when an exact boundary value belongs to more than one tier.
- `Tier unknown` when the option text or item context cannot be matched safely.

Example labels:

```text
Prefix  T8  +19 (15-24)
Suffix  T1  11% (10-15%)
Prefix  T4  24% / +17 (21-26% / 17-20)
Item max T1 39-42% / +33-39
```

```text
Prefix  T2/T3  100% (87-100% / 100-119%)
Suffix  T3  +31 (31-35)
Item max T2 +36-40% | T1 needs ilvl 82 +41-45%
```

## Feature List

- Controller-friendly overlay for Well of Souls choices.
- Uses actual Well choice rows, not unrelated tooltip or chat text.
- Uses the item shown in the Well window for class and item level.
- Handles flat, percent, damage-range, and hybrid modifiers.
- Handles regular jewel choices, jewel desecrated choices, and Heart of the Well jewel choices.
- Handles Breach/Otherworldly accessory choices from Altered Collarbone.
- Shows whether a matched choice is a prefix or suffix.
- Shows exact overlapping tier boundaries, for example `T2/T3  100% (87-100% / 100-119%)`.
- Handles item-level locked top tiers.
- Clears stale choice overlays after a choice is confirmed or the Well returns to the reveal prompt.
- Includes local Well of Souls tier data only; no valuation, price checking, build-demand data, or trade API logic.
- Throttled UI scanning with cached/direct Well paths first and broad fallback scanning only occasionally.
- Partial Well reads are retried with backoff and cooldown so a stuck UI path cannot hammer ExileCore2's render loop.
- Diagnostic report export for troubleshooting missed or partial Well reads.

## Install

Copy the whole `WellWise` folder into your ExileCore2 plugin source directory:

```text
ExileCore2\Plugins\Source\WellWise
```

Then reload/compile source plugins in ExileCore2.

## Settings

- `Enable`: turns the plugin on/off.
- `ShowOptionText`: optionally includes the Well option text inside the small overlay badge for debugging. Off by default because the game already shows the option text.
- `ShowAreaDebugOverlay`: draws a small area-detection label showing whether WellWise thinks you are in The Well of Souls.
- `DebugMode`: logs throttled scan errors to ExileCore2 logs.
- `ReloadData`: reloads `data/well_of_souls_tiers.json` without restarting ExileCore2.
- `ExportDiagnosticReport`: writes a timestamped report to `WellWise/diagnostics/` with area info, visible Well roots, option text, UI paths, item context, and tier-match details.
- `LastStatus`, `LastContext`, `LastOptions`: read-only status fields for quick troubleshooting.

## Edge Cases To Watch

- If GGG changes the Well UI tree, WellWise may show no labels until the UI paths are updated.
- If only one or two choices are found, WellWise backs off and cools down instead of retrying every frame.
- If the Well item class or item level cannot be read, WellWise intentionally refuses to guess a confident tier.
- Some modifier families have overlapping numeric ranges. Exact overlaps are shown with multiple tiers, while non-exact fallback matches still choose the closest/best tier.
- Hybrid stats need per-component formatting. A flat component should stay flat, for example `+17-20`, not `+17-20%`.
- Added-damage rolls use multiple numbers and can overlap between adjacent tiers; exact boundaries should be checked with screenshots if something looks surprising.
- Jewel options use a separate raw data domain from normal equipment, so screenshots and debug dumps are useful if a jewel still shows `Tier unknown`.
- Breach/Otherworldly choices are validated from both the normalized local data and the raw game dump before being added.
- `Tier unknown` is safer than a fake tier. It usually means missing item context, unsupported option text, or a data coverage gap.
- If ExileCore2 hot reload shows old `Tier unknown` labels after an update, restart ExileCore2 so the plugin assembly and copied data reload cleanly.
- The data file may need regeneration after large PoE2 balance patches.
