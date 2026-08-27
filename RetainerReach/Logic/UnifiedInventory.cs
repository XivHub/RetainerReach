using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using RetainerReach.Game;
using RetainerReach.Ipc;
using RetainerReach.Model;
using XivHubPluginKit.Inventory;

namespace RetainerReach.Logic
{
    /// <summary>
    /// Aggregates the active character's AllaganTools-reported retainer inventories (+ player bags,
    /// read-only context) into (ItemId, Hq)-grouped <see cref="UnifiedItem"/> rows for the view
    /// (Phase 4).
    /// </summary>
    public static class UnifiedInventory
    {
        // AllaganTools reports retainer-owned rows with a container value in 10000..12999
        // (FFMarketConnector's IsRetainerInventory boundary; see PLAN.md "Existing Patterns").
        private const ulong RetainerInventoryRangeStart = 10000;
        private const ulong RetainerInventoryRangeEnd = 13000; // exclusive

        // Within that range only RetainerPage1..7 are normal item pages that can be retrieved from;
        // RetainerEquippedItems/RetainerGil/RetainerCrystals/RetainerMarket are excluded (crystals,
        // gil, and market listings are never retrieve targets — PLAN.md Out of Scope). This split
        // assumes AllaganTools reports the raw FFXIVClientStructs.InventoryType value as the row's
        // container index; NOT confirmed in-game — see PLAN.md "Needs In-Game Verification" item 1.
        private const ulong RetainerPageRangeStart = (ulong)InventoryType.RetainerPage1;
        private const ulong RetainerPageRangeEnd = (ulong)InventoryType.RetainerPage7 + 1; // exclusive

        private static readonly InventoryType[] PlayerBagContainers =
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        };

        /// <summary>
        /// Builds the unified (ItemId, Hq) view: retainer-held quantities + holders, with the
        /// player's own bag quantity attached as read-only context. Retrievable source is
        /// retainer-held items only (PLAN.md Assumptions) — bag-only items never appear here.
        /// </summary>
        public static List<UnifiedItem> Build()
        {
            var groups = new Dictionary<(uint ItemId, bool Hq), UnifiedItem>();

            var owned = AllaganToolsIpc.CharactersOwnedByActive(true);
            ulong playerCid = AllaganToolsIpc.CurrentCharacter();

            foreach (var cid in owned)
            {
                if (cid == playerCid)
                    continue;

                var rows = AllaganToolsIpc.CharacterItems(cid);
                if (!IsRetainerInventory(rows))
                    continue; // FC / other non-retainer CID AT tracks for this character

                string retainerName = RetainerRoster.NameForCid(cid) ?? cid.ToString();

                foreach (var row in rows)
                {
                    if (row.Length < 7)
                        continue;

                    ulong container = row[0];
                    if (container < RetainerPageRangeStart || container >= RetainerPageRangeEnd)
                        continue; // crystals/market/gil/equipped — not a retrievable item page

                    uint itemId = (uint)row[2];
                    uint qty = (uint)row[3];
                    bool hq = (row[6] & 1UL) != 0;
                    if (itemId == 0 || qty == 0)
                        continue;

                    var key = (itemId, hq);
                    if (!groups.TryGetValue(key, out var item))
                    {
                        // Task 4.3: category/material-eligible/icon data is resolved here, once per
                        // build, so the Draw path never re-hits a Lumina sheet per row per frame.
                        var categoryId = ItemCategories.CategoryOf(itemId);
                        var sheetRow = ItemSheet.ById(itemId);
                        item = new UnifiedItem
                        {
                            ItemId = itemId,
                            Name = ItemSheet.Name(itemId),
                            Hq = hq,
                            CategoryId = categoryId,
                            CategoryName = ItemCategories.NameFor(categoryId),
                            SearchCategoryId = ItemSearchCategories.CategoryOf(itemId),
                            IconId = sheetRow?.Icon ?? 0,
                            Ilvl = (ushort)(sheetRow?.LevelItem.RowId ?? 0),
                            VendorPrice = sheetRow?.PriceLow ?? 0,
                            StackSize = sheetRow?.StackSize ?? 0,
                            Untradable = sheetRow?.IsUntradable ?? false,
                            Unique = sheetRow?.IsUnique ?? false,
                        };
                        groups[key] = item;
                    }

                    item.TotalRetainerQty += qty;

                    var holding = item.Holdings.FirstOrDefault(h => h.RetainerCid == cid);
                    if (holding == null)
                    {
                        holding = new RetainerHolding { RetainerCid = cid, RetainerName = retainerName };
                        item.Holdings.Add(holding);
                    }

                    holding.Qty += qty;
                }
            }

            PopulatePlayerBagQty(groups);

            return groups.Values.ToList();
        }

        /// <summary>
        /// True if the given rows include at least one entry in the AT retainer container range
        /// (10000..12999); mirrors AllaganToolsBridge's IsRetainerInventory classification.
        /// </summary>
        private static bool IsRetainerInventory(HashSet<ulong[]> rows)
        {
            foreach (var row in rows)
            {
                if (row.Length < 1)
                    continue;

                ulong container = row[0];
                if (container >= RetainerInventoryRangeStart && container < RetainerInventoryRangeEnd)
                    return true;
            }

            return false;
        }

        private static void PopulatePlayerBagQty(Dictionary<(uint ItemId, bool Hq), UnifiedItem> groups)
        {
            foreach (var container in PlayerBagContainers)
            {
                foreach (var slot in InventoryScan.ScanContainer(container))
                {
                    var key = (slot.ItemId, slot.IsHq);
                    if (groups.TryGetValue(key, out var item))
                        item.PlayerBagQty += slot.Qty;
                }
            }
        }
    }
}
