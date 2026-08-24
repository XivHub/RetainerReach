# RetainerReach

A read-only unified inventory view (active character + all its retainers, sourced from AllaganTools
IPC) with one-click **Retrieve**: batch-summon each holding retainer at a nearby Summoning Bell and
pull selected items into the player's bags. See `PLAN.md` for the full design, architecture decision,
and phase-by-phase implementation history.

Namespace / `AssemblyName` / `InternalName`: **RetainerReach**. Shares primitives with InventoryCleaner
via XivHubPluginKit linked-source (`KitServices`, `Inventory/{ItemSheet,InventoryScan,SlotView}`).

This is gameplay automation; it is never eligible for the mainline/goatcorp Dalamud list (ToS). Per
the owner's decision it is served on the **public custom-repo master** (`plugins.xivhub.net/pluginmaster.json`),
i.e. NOT listed in `~/share/zhyra/.private-plugins`. The in-app disclaimer (`Windows/ConfigWindow.cs`)
must still be acknowledged once before Retrieve is usable.

## Features (0.2.0 QoL expansion)

Built on top of the 0.1.x base described above. See `PLAN_QOL.md` for the full design and
phase-by-phase implementation history.

- Freely resizable window with a Browse table that fills the available height.
- Config-driven item icon scale (1.0x-2.5x slider), applied across Browse/Preview/Results.
- Per-row quantity controls: `-`/`+` steppers, a slider, and the existing numeric input, all clamped
  to `[1, TotalRetainerQty]`.
- Filter by item category (`ItemUICategory`, localized, multi-select, only categories present in
  current data) and a crafting-materials-only toggle, both combined with the name filter.
- Sortable Browse columns (name, retainer qty, in-bags, ilvl, category, vendor price, holder).
- Browse summary line: selected count, worst-case pull total, bag-slots-needed vs free (warn-tinted),
  and a "N items, M shown" result count.
- Right-click row context menu: link in chat, retrieve just this item, select/deselect.
- Persisted preferences (filters, sort, column visibility, icon scale, window size) survive relog.
- Vendor price column (sell-to-vendor) plus a gil total for the current selection.
- Group-by-retainer Browse layout with a per-retainer filter and a "Retrieve all from `<retainer>`"
  action scoped to that retainer.
- Saved retrieval presets ("shopping lists"): save/apply/delete a named selection, with live-quantity
  clamping and skip-if-absent on apply.
- `/retrieve <item name> [quantity|all]` chat command for a quick retrieve without opening the window.

## Build

```bash
DALAMUD_HOME=~/.cache/dalamud-dev DOTNET_ROOT=~/.dotnet \
  ~/.dotnet/dotnet build RetainerReach/RetainerReach.csproj -c Release -p:Platform=x64
```

Gate: 0 warnings / 0 errors. Produces `RetainerReach/bin/x64/Release/RetainerReach/latest.zip`.

## Deploy

Deploy is a separate, explicit step (not run as part of implementation) — it stages the build under
the combined Zhyra plugin repo and merges an entry into `pluginmaster.json`.

```bash
./publish.sh
# equivalent to: publish-plugin <path to this repo>
```

Before every publish after the first:
- **Bump `<Version>`** in `RetainerReach/RetainerReach.csproj` (currently `0.1.0` — this is the first
  release, so nothing to supersede yet; every subsequent `publish-plugin` run needs a version bump or
  the in-game client won't pick up the new build).
- Per the owner's decision, `RetainerReach` is **not** in `~/share/zhyra/.private-plugins`, so
  `publish-plugin` includes it in the PUBLIC master (`pluginmaster.public.json` /
  `plugins.xivhub.net/pluginmaster.json`). To make it private again, add `RetainerReach` back to that
  denylist and re-run `publish-plugin`.

## Needs In-Game Verification

Everything below compiles and is exercised by the offline build gate, but can only be confirmed on a
live client. Consolidated from `PLAN.md`'s "Needs In-Game Verification" section and the in-source
`NEEDS IN-GAME VERIFICATION` notes left across Phases 3-7:

1. Exact AllaganTools **container numbers** for retainer pages vs crystals/market/gil within
   `10000..12999` (which to keep vs exclude in aggregation) — `Logic/UnifiedInventory.cs`.
2. Summoning Bell **`EObjName` row 2000401** localized name match + valid interaction distances
   (6.5 / 4.75 / 4.6) on the current patch — `Automation/BellFinder.cs`.
3. Whether the bell **set-target -> interact split across ticks** is actually required (AutoRetainer
   splits them) or whether same-frame works — `Automation/RetainerUi.cs` / `RetrieveScheduler.cs`
   (`TargetBell`/`RingBell`).
4. `SelectString` **`Addon` row 2378** "Entrust or withdraw items." text match, and that selecting it
   loads `RetainerPage*` into memory — `Automation/RetainerUi.cs`.
5. Retainer inventory **addon readiness** signal + the **stabilization frame count** (currently 5,
   `RetrieveScheduler.StabilizationFrames`) needed before `RetainerPage*` are fully populated for
   scanning — `Automation/RetrieveScheduler.cs` (`WaitInventory`).
6. **Retainer command mechanism** (the load-bearing one): that the signature
   `48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0` binds on the
   current patch; that invoking it with `(agentModule, slot, RetainerPage*, 0, RetrieveFromRetainer)`
   actually withdraws the slot's stack to player bags while the retainer inventory addon is open and
   the agent is active; the meaning of `a4` (currently passed as `0`, mirroring AutoRetainer); and
   confirmation that retrieve is whole-stack only (no partial) — `Automation/RetainerCommandInvoker.cs`.
7. `RetainerManager` accessors: `IsReady`, `Retainers` name read, `GetActiveRetainer()` CID matching
   AllaganTools' retainer CID — `Game/RetainerRoster.cs`.
8. **Probe for a qty-aware retrieve before locking in whole-stack-greedy as final.** AutoRetainer's
   trimmed `RetainerItemCommand` enum defines `0,1,4,5`; ordinals **2 and 3 are unused/undefined** and
   AutoRetainer never calls `RetrieveFromRetainer=0` at all, so the absent retrieve-quantity value is
   NOT proof one doesn't exist. In-game, invoke the command with an unused ordinal (2, then 3) against
   a retrieve slot/page and watch for an `InputNumeric` addon appearing — that is exactly how
   `EntrustQuantity=4` takes its count. If a qty-aware path is found, a split step would need to be
   added to the pull loop and whole-stack-greedy dropped; if not, whole-stack-greedy (the current,
   shipped behavior) stands.
9. Frame-spacing: whether one `RetrieveFromRetainer` invoke per tick is reliable, or whether
   `Configuration.MoveTickGap` needs a default greater than `1` (a server round-trip per pull may need
   a larger gap than a local `MoveItemSlot`) — `Automation/RetrieveScheduler.cs`
   (`MaxLandingChecks`/`TickPulling*`), `Windows/ConfigWindow.cs` (the slider, currently 1-10).
10. AutoRetainerAPI availability/version and the `OnRetainerReadyToPostprocess` handshake — relevant
    only if Phase 8 (AutoRetainer postprocess secondary driver, optional/not built) is picked up later.
11. `InventoryManager.MoveItemSlot` return codes for InventoryCleaner's player-internal moves (shared
    unknown with the XivHubPluginKit `MoveQueue`; unaffected by RetainerReach's retrieve path, which
    never uses `MoveItemSlot`).
12. **Task 7.7 full-flow verification** (not yet run): the complete preview -> confirm -> run -> done
    UX end to end — a small single-retainer, single-item retrieve, then a two-retainer batch — plus
    confirming `Stop` mid-run leaves no stuck retainer UI, and that the disclaimer gate/Settings window
    behave as expected in-client.
13. **Guards pause vs in-flight wait actions**: `RetrieveScheduler.Tick()` pauses state advancement
    while `!Guards.SafeToAct()`, but the internal ECommons `TaskManager` runs an already-enqueued
    wait's condition body (which fires UI actions like `SelectRetainer`/`InteractBell`) on its own
    framework hook. Each such action self-guards on addon-readiness / range / `IsAnimationLocked`
    (all false during a genuine interruption), and `OnWaitTimeout` extends the deadline while paused,
    so this is expected to be benign. Confirm in-game that no stray retainer/bell interaction fires
    during a mid-run cutscene / loading / between-areas transition; if it does, also gate the wait
    condition bodies on `Guards.SafeToAct()` — `Automation/RetrieveScheduler.cs`.

One related item is already confirmed offline and does *not* need in-game verification: AllaganTools'
`InternalName` is `InventoryTools` (confirmed from `~/dev/InventoryTools/InventoryTools/InventoryTools.json`).

### 0.2.0 QoL expansion

Added by the `PLAN_QOL.md` expansion; compiles and passes the offline build gate but needs a live
client to confirm:

14. Window free-resize past the old 900x1400 cap, and the Browse table fills window height without
    clipping the summary/footer — `Windows/MainWindow.cs` (`SizeConstraints`, `DrawItemTable`).
15. Icon scale slider (1.0x-2.5x) renders crisp, correctly aligned icons in Browse/Preview/Results,
    especially at the 2.5x extreme — `Game/ItemIcon.cs`, `Windows/ConfigWindow.cs`.
16. HQ glyph renders correctly in all three sites it appears (Browse, Preview, target Results) —
    `Windows/MainWindow.cs` (`HqSuffix`).
17. Quantity `-`/`+` steppers, slider, and numeric input all clamp to `[1, TotalRetainerQty]` and stay
    usable/fit at narrow window widths — `Windows/MainWindow.cs` (`DrawQtyControls`).
18. Category filter combo shows real, localized `ItemUICategory` names and lists only categories
    present in the current data — `Game/ItemCategories.cs`, `Windows/MainWindow.cs`.
19. Crafting-materials-only toggle matches a known recipe ingredient and excludes a known
    non-ingredient — `Logic/CraftingMaterials.cs`.
20. Sortable column headers (name, retainer qty, in-bags, ilvl, category, vendor, holder) produce the
    correct order, ascending and descending — `Windows/MainWindow.cs` (`RecomputeView`, `sortSpec`).
21. Vendor price column matches the NPC sell price in-game, and the selection gil total sums
    correctly — `Model/UnifiedItem.cs` (`VendorPrice`), `Windows/MainWindow.cs` (`SelectionGilTotal`).
22. Group-by-retainer headers list the right items per retainer; the per-retainer filter combo (and
    its interaction with the flat/grouped view toggle) narrows correctly; "Retrieve all from
    `<retainer>`" pulls only that retainer's shown items — `Windows/MainWindow.cs`
    (`DrawGroupedView`, `RetrieveAllFromRetainer`).
23. Saved presets survive a relog (persisted in `Configuration.cs`), and applying a preset after
    retainer contents changed clamps quantities to the live stack and skips items no longer held —
    `Windows/MainWindow.cs` (`ApplyPreset`), `Model/RetrievePreset.cs`.
24. Persisted preferences (filters, sort, column visibility, icon scale, window size) survive a
    relog — `Configuration.cs`, `Windows/MainWindow.cs` (ctor restore + debounced write-back).
