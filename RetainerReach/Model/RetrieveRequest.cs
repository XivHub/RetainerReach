using System.Collections.Generic;
using System.Linq;

namespace RetainerReach.Model
{
    /// <summary>
    /// One concrete pull: an (ItemId, Hq) targeted at a single retainer, with the qty the user asked
    /// for and the worst-case qty that will actually land in bags given the whole-stack-only
    /// retrieve command (see PLAN.md Architecture Decision / Edge Cases "Whole-stack overshoot").
    /// Built by <see cref="Logic.RetrievePlanner.Plan"/>; consumed by the Phase 4 preview and the
    /// Phase 5/6 automation (grouped via <see cref="RetrieveBatch.ByRetainer"/>).
    /// </summary>
    public sealed class RetrieveTarget
    {
        public uint ItemId;
        public bool Hq;
        public string RetainerName = string.Empty;
        public ulong RetainerCid;
        public uint Qty;

        /// <summary>
        /// Sum of the whole retainer-holding stacks that must be touched to cover <see cref="Qty"/>
        /// for this (ItemId, Hq) at this retainer, since <c>RetrieveFromRetainer</c> has no qty arg
        /// and always pulls an entire slot's stack. Equal to <see cref="Qty"/> only when the holding
        /// happens to be an exact-match single stack. AT-cached stack sizes are the estimate; the
        /// live re-scan at pull time (Phase 6) is authoritative.
        /// </summary>
        public uint WorstCasePulled;
    }

    /// <summary>
    /// A full retrieve request: every <see cref="RetrieveTarget"/> the user confirmed, ready to be
    /// grouped and walked by the Phase 5/6 automation one retainer at a time.
    /// </summary>
    public sealed class RetrieveBatch
    {
        public List<RetrieveTarget> Targets = new();

        /// <summary>
        /// Targets grouped by the retainer they must be pulled from, keyed by <see cref="RetrieveTarget.RetainerCid"/>.
        /// This is the shape the Phase 5/6 scheduler iterates: one group per bell-summon + retainer
        /// visit, its items pulled in turn before moving to the next group.
        /// </summary>
        public IEnumerable<IGrouping<ulong, RetrieveTarget>> ByRetainer()
            => Targets.GroupBy(t => t.RetainerCid);
    }
}
