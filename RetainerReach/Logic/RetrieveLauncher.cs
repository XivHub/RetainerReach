using System.Collections.Generic;
using RetainerReach.Automation;
using RetainerReach.Model;
using XivHubPluginKit.Inventory;

namespace RetainerReach.Logic
{
    /// <summary>
    /// Shared decision + submission path for turning a confirmed <see cref="RetrieveBatch"/> into a
    /// running retrieve. Extracted from <see cref="Windows.MainWindow"/>'s confirm button (Feature 4)
    /// so the window and the <c>/retrieve</c> command (Feature 3) share exactly one code path — same
    /// gates, same routing. RetainerReach always runs its own-bell automation: the player must be at a
    /// summoning bell and no run may already be in progress.
    /// </summary>
    public static class RetrieveLauncher
    {
        /// <summary>
        /// Human-readable reasons a retrieve cannot be submitted right now; empty when nothing blocks it.
        /// </summary>
        public static IReadOnlyList<string> DisabledReasons(Configuration cfg)
        {
            var reasons = new List<string>();

            if (!BellFinder.Reachable())
                reasons.Add("no bell reachable");

            if (InventoryScan.FreeSlotsInBag() <= 0)
                reasons.Add("no free bag slots");

            if (RetrieveScheduler.IsRunning)
                reasons.Add("a run is already in progress");

            return reasons;
        }

        /// <summary>
        /// Starts the own-bell run for <paramref name="batch"/> when nothing in
        /// <see cref="DisabledReasons"/> blocks it. Returns false — with <paramref name="reason"/> set
        /// to the blockers joined by ", " — without starting anything when
        /// <see cref="DisabledReasons"/> is non-empty.
        /// </summary>
        public static bool TrySubmit(RetrieveBatch batch, Configuration cfg, out string reason)
        {
            var reasons = DisabledReasons(cfg);
            if (reasons.Count > 0)
            {
                reason = string.Join(", ", reasons);
                return false;
            }

            RetrieveScheduler.Start(batch);
            reason = string.Empty;
            return true;
        }
    }
}
