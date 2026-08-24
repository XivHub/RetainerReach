using System.Collections.Generic;
using System.Linq;

namespace RetainerReach.Model
{
    /// <summary>
    /// How a single <see cref="RetrieveTarget"/> resolved once its retainer visit finished. Because
    /// <c>RetrieveFromRetainer</c> is whole-stack-only, <see cref="TargetResult.Pulled"/> may exceed
    /// <see cref="TargetResult.Requested"/> (PLAN.md Edge Cases "Whole-stack overshoot") — that case is
    /// still <see cref="Moved"/>, never a failure.
    /// </summary>
    public enum TargetOutcome
    {
        /// <summary>Pulled qty met or exceeded the requested qty.</summary>
        Moved,

        /// <summary>Some qty landed, but less than requested (e.g. bags filled mid-retainer).</summary>
        Short,

        /// <summary>Nothing landed at all (item gone at scan time, or every pull attempt failed to land).</summary>
        Failed,
    }

    /// <summary>One <see cref="RetrieveTarget"/>'s outcome, for the Phase 7 per-retainer results view.</summary>
    public sealed record TargetResult(uint ItemId, bool Hq, uint Requested, uint Pulled, TargetOutcome Outcome, string? Reason);

    /// <summary>
    /// The finished outcome of one retainer visit — every <see cref="RetrieveTarget"/> that retainer
    /// held, with its resolved <see cref="TargetResult"/>. Appended to
    /// <see cref="Automation.RetrieveScheduler.Results"/> as each retainer's <c>Pulling</c> state
    /// drains (PLAN.md Task 6.5).
    /// </summary>
    public sealed record RetainerResult(string RetainerName, ulong RetainerCid, IReadOnlyList<TargetResult> Targets)
    {
        public int Moved => Targets.Count(t => t.Outcome == TargetOutcome.Moved);
        public int Short => Targets.Count(t => t.Outcome == TargetOutcome.Short);
        public int Failed => Targets.Count(t => t.Outcome == TargetOutcome.Failed);
    }
}
