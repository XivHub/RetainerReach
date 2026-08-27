using System.Collections.Generic;
using RetainerReach.Model;

namespace RetainerReach.Logic
{
    /// <summary>
    /// Turns the Phase 4 UI selection (requested qty per (ItemId, Hq)) into a concrete
    /// <see cref="RetrieveBatch"/>: for each selected item, the requested qty is distributed across
    /// that item's <see cref="RetainerHolding"/>s (fill the first holder, spill to the next), and
    /// each resulting <see cref="RetrieveTarget"/> also records the worst-case whole-stack total that
    /// will actually be pulled, since the native retrieve command has no qty arg (PLAN.md Architecture
    /// Decision, Edge Cases "Whole-stack overshoot").
    /// </summary>
    public static class RetrievePlanner
    {
        /// <summary>
        /// Builds a <see cref="RetrieveBatch"/> from the current selection. <paramref name="selection"/>
        /// maps (ItemId, Hq) to the requested qty (1..TotalRetainerQty); a requested qty equal to the
        /// item's TotalRetainerQty ("select all"/full) naturally consumes every holding in full,
        /// since the fill-then-spill loop below only stops once the requested amount is covered.
        /// </summary>
        public static RetrieveBatch Plan(
            IReadOnlyDictionary<(uint ItemId, bool Hq), uint> selection,
            IReadOnlyList<UnifiedItem> unifiedItems)
        {
            var batch = new RetrieveBatch();

            foreach (var item in unifiedItems)
            {
                var key = (item.ItemId, item.Hq);
                if (!selection.TryGetValue(key, out var requestedQty) || requestedQty == 0)
                    continue;

                var remaining = requestedQty;
                foreach (var holding in item.Holdings)
                {
                    if (remaining == 0)
                        break;
                    if (holding.Qty == 0)
                        continue;

                    var allocated = remaining < holding.Qty ? remaining : holding.Qty;
                    remaining -= allocated;

                    batch.Targets.Add(new RetrieveTarget
                    {
                        ItemId = item.ItemId,
                        Hq = item.Hq,
                        RetainerName = holding.RetainerName,
                        RetainerCid = holding.RetainerCid,
                        Qty = allocated,
                        // Whole-stack-only command: this holding's entire stack is touched
                        // regardless of how much of it was actually requested.
                        WorstCasePulled = holding.Qty,
                    });
                }
            }

            return batch;
        }

        /// <summary>
        /// How many of <paramref name="item"/>'s holdings the fill-then-spill walk in <see cref="Plan"/>
        /// would consume to cover <paramref name="qty"/>. Each consumed holding becomes one
        /// <see cref="RetrieveTarget"/>, so this is the same per-item bag-slot estimate the Browse
        /// summary derives from <c>RetrieveBatch.Targets.Count</c>.
        /// </summary>
        public static int HoldingsNeeded(UnifiedItem item, uint qty)
        {
            var remaining = qty;
            var needed = 0;

            foreach (var holding in item.Holdings)
            {
                if (remaining == 0)
                    break;
                if (holding.Qty == 0)
                    continue;

                remaining -= remaining < holding.Qty ? remaining : holding.Qty;
                needed++;
            }

            return needed;
        }
    }
}
