using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using RetainerReach.Model;
using XivHubPluginKit.Inventory;
using XivHubPluginKit.Retainer;

namespace RetainerReach.Game
{
    /// <summary>
    /// Live re-scan of the summoned retainer's item pages (<see cref="InventoryType.RetainerPage1"/>..
    /// <see cref="InventoryType.RetainerPage7"/>), matched against a batch of
    /// <see cref="RetrieveTarget"/>s to produce concrete (page, slot) pulls. Only valid while a
    /// retainer's inventory is actually open — the pages are populated on demand (PLAN.md Edge Cases
    /// "Retainer inventory not loaded before scan"). Never trusts AllaganTools' cached container map
    /// for writes (PLAN.md "Reading vs moving are decoupled") — this is the sole live source of truth
    /// for the pull loop (<see cref="RetrieveScheduler.State.Pulling"/> re-scans again between
    /// individual pulls since the container shifts as items are withdrawn).
    /// </summary>
    public static unsafe class RetainerScan
    {
        /// <summary>
        /// For each target, greedily matches live slots by ItemId+Hq and selects whole slots until
        /// the accumulated qty meets or exceeds the requested qty — the retrieve command has no qty
        /// arg, so a partial-stack pull is impossible (PLAN.md Architecture Decision "Whole-stack").
        /// A single live slot is never counted toward two different targets. Returns the concrete
        /// slots to pull, in target order; a target whose live holdings are already exhausted
        /// (AT cache drift — PLAN.md Edge Cases) simply contributes no entries.
        /// </summary>
        public static List<(InventoryType Page, ushort Slot, uint ItemId, uint Qty)> PickSlots(IEnumerable<RetrieveTarget> targetsForThisRetainer)
        {
            var picks = new List<(InventoryType Page, ushort Slot, uint ItemId, uint Qty)>();
            var live = RetainerRetrieve.LivePages();
            var claimed = new HashSet<(InventoryType, int)>();

            foreach (var target in targetsForThisRetainer)
            {
                uint accumulated = 0;
                foreach (var slot in live)
                {
                    if (accumulated >= target.Qty)
                        break;

                    if (slot.ItemId != target.ItemId || slot.IsHq != target.Hq)
                        continue;

                    var key = (slot.Container, slot.SlotIndex);
                    if (claimed.Contains(key))
                        continue;

                    claimed.Add(key);
                    picks.Add((slot.Container, (ushort)slot.SlotIndex, slot.ItemId, slot.Qty));
                    accumulated += slot.Qty;
                }
            }

            return picks;
        }
    }
}
