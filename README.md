# Auto-Reforge

A mod for [TerrariaModder](https://github.com/nabagaca/terraria-modder) that upgrades the Goblin Tinkerer reforge UI with a modifier selector and auto-reforge loop.

## Features

- **Modifier selector** — browse every modifier valid for the item in the reforge slot, grouped and colour-coded by quality tier
- **Auto-reforge** — click one button and the mod pays for and reforges repeatedly until your chosen modifier is rolled
- **Vanilla feedback** — each reforge plays the tinkerer sound and shows the floating prefix text above your character, just like clicking manually
- **Speed control** — slider to set reforges per second (up to ~15/s)
- **Money floor** — set a minimum gold amount to keep; auto-reforge stops before spending you below it
- **Clear status** — the panel always tells you why it stopped (success, out of money, or below threshold) and shows attempt count and total spent

## Modifier tiers

| Colour | Meaning |
|--------|---------|
| 🌈 Rainbow | **Best** — the highest-value modifier for this item type (e.g. *Godly*, *Unreal*, *Mythical*) |
| 🟢 Green | **Good** — improves the item's value |
| ⬜ White | **Neutral** — no net change |
| 🔴 Red | **Bad** — reduces the item's value (e.g. *Broken*, *Slow*, *Weak*) |

The tier of each modifier is computed dynamically per item, so accessories, weapons, and summon weapons are all classified correctly.

## Usage

1. Talk to the Goblin Tinkerer and click **Reforge** — the Auto-Reforge panel opens automatically to the right of the vanilla UI
2. Place an item in the reforge slot
3. Click the modifier you want from the list
4. Adjust **Speed** and **Stop if low** to taste
5. Click **AUTO-REFORGE**

The panel can also be toggled manually with **F7** (rebindable via the Modder menu, F6).

## Installation

1. Build [TerrariaModder](https://github.com/nabagaca/terraria-modder) and have it injected into Terraria
2. Clone this repo and run:
   ```
   dotnet build AutoReforge.csproj
   ```
   The build target automatically copies `AutoReforge.dll` and `manifest.json` to:
   ```
   <Terraria>\TerrariaModder\mods\auto-reforge\
   ```

## Requirements

- [TerrariaModder](https://github.com/nabagaca/terraria-modder) `>=0.2.0`
- Terraria 1.4.x (Steam)
- .NET Framework 4.8
