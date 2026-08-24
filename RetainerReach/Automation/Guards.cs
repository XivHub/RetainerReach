using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace RetainerReach.Automation
{
    /// <summary>
    /// Per-frame safety gate for <see cref="RetrieveScheduler"/>.
    ///
    /// Deliberately narrower than ECommons' <see cref="GenericHelpers.IsOccupied"/>: that helper's
    /// OR-list includes <see cref="ConditionFlag.OccupiedSummoningBell"/>, which is true for the
    /// *entire* duration the player is browsing the RetainerList / a retainer's inventory — exactly
    /// the states this scheduler needs to run through. Confirmed against AutoRetainer, which only
    /// ever checks <c>IsOccupied()</c> as a precondition to *start* the bell sequence
    /// (<c>SchedulerMain.cs:306</c>, <c>PlayerWorldHandlers.SelectNearestBell</c>) — never as a
    /// per-tick gate on the later RetainerList/inventory steps, which have no <c>IsOccupied()</c>
    /// check at all (<c>RetainerListHandlers.SelectRetainerByName</c>,
    /// <c>RetainerHandlers.SelectEntrustItems</c>). Gating every <see cref="RetrieveScheduler.Tick"/>
    /// on the full <c>IsOccupied()</c> would make the scheduler self-deadlock the instant it rings
    /// the bell, so <see cref="ConditionFlag.OccupiedSummoningBell"/> is intentionally excluded here.
    /// </summary>
    public static class Guards
    {
        /// <summary>
        /// True when it is safe for the scheduler to act this frame: the screen isn't fading/loading,
        /// the player exists, and we are not in a genuine interruption (cutscene, zone transition,
        /// quest event). The bell-occupied state the automation itself induces does not count as an
        /// interruption — see the type doc comment.
        /// </summary>
        public static bool SafeToAct()
        {
            if (!GenericHelpers.IsScreenReady())
                return false;

            if (!Player.Available)
                return false;

            // OccupiedSummoningBell is true throughout our own retainer flow; excluded so the
            // scheduler doesn't self-deadlock (see AR: IsOccupied() is only a start precondition,
            // never a per-tick gate on retainer steps).
            if (Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]
                || Svc.Condition[ConditionFlag.WatchingCutscene]
                || Svc.Condition[ConditionFlag.WatchingCutscene78]
                || Svc.Condition[ConditionFlag.BetweenAreas]
                || Svc.Condition[ConditionFlag.BetweenAreas51]
                || Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
                return false;

            return true;
        }
    }
}
