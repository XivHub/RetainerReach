# RetainerReach

See everything your retainers hold in one window, and pull items into your bags in one click.

> **Warning:** this uses the Summoning Bell and moves items for you, which counts as automation under
> the FFXIV User Agreement. Use it at your own risk. You accept this once in Settings before Retrieve
> unlocks.

## What you need

- **AllaganTools** (also listed as Allagan Market or InventoryTools), installed and enabled.
  RetainerReach reads your retainers' inventories from it.

## How it works

Open the window to see your bags and every retainer's items in one grid. Tick what you want and
press Retrieve. Stand near a Summoning Bell: RetainerReach calls each retainer that holds something
you picked and moves it to your bags.

You get a preview before anything moves.

## Features

- Search by name, filter by category, or show crafting materials only.
- Sort by name, quantity, item level, vendor price or which retainer holds it.
- Group items by retainer, with a "Retrieve all from this retainer" button.
- Save a selection you pull often as a preset and apply it later.
- Right-click an item to link it in chat or retrieve just that one.
- Bigger or smaller icons, and the window remembers your filters and layout.

## Install

In `/xlsettings`, under Experimental, add this to Custom Plugin Repositories:

```
https://plugins.xivhub.net/pluginmaster.json
```

Then install RetainerReach from the plugin installer.

## Commands

| Command | What it does |
|---|---|
| `/retainerreach` or `/rtr` | Open the window |
| `/retainerreach stop` | Stop a retrieve in progress |
| `/retrieve <item> [amount or all]` | Retrieve an item without opening the window |

Building from source: see [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## License

AGPL-3.0-or-later. See `LICENSE` and `NOTICE`.
