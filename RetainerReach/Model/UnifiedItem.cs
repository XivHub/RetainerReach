using System.Collections.Generic;

namespace RetainerReach.Model
{
    /// <summary>
    /// One (ItemId, Hq) group aggregated across the active character's retainers, with read-only
    /// player-bag context. Built by <see cref="Logic.UnifiedInventory.Build"/>; consumed by the
    /// unified inventory view (Phase 4) and the retrieve planner (Phase 4/6).
    /// </summary>
    public sealed class UnifiedItem
    {
        public uint ItemId;
        public string Name = string.Empty;
        public bool Hq;
        public uint TotalRetainerQty;
        public uint PlayerBagQty;
        public List<RetainerHolding> Holdings = new();

        // Task 4.3: stamped once by UnifiedInventory.Build() (never per frame/per row) so Browse's
        // category/material filters and icon draw never touch a Lumina sheet in the Draw path.
        public ushort CategoryId;
        public string CategoryName = string.Empty;
        public ushort IconId;

        // ItemSearchCategory (market-board taxonomy) row id — drives the icon category chips/filter.
        // Distinct from CategoryId (ItemUICategory), which the table's Category column still displays.
        public ushort SearchCategoryId;

        // Task 5.1: stamped once by UnifiedInventory.Build() via ItemSheet.Ilvl (never per
        // frame/per row) so the Browse "ilvl" column and its sort key never touch a Lumina sheet
        // in the Draw path.
        public ushort Ilvl;

        // Task 8.1: stamped once by UnifiedInventory.Build() via ItemSheet.ById(itemId)?.PriceLow
        // (sell-to-vendor price, never per frame/per row) so the Browse "Vendor" column, its sort
        // key, and the selection gil total never touch a Lumina sheet in the Draw path.
        public uint VendorPrice;
    }

    /// <summary>A single retainer's stake in a <see cref="UnifiedItem"/> group.</summary>
    public sealed class RetainerHolding
    {
        public ulong RetainerCid;
        public string RetainerName = string.Empty;
        public uint Qty;
    }
}
