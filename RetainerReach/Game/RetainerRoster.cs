using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace RetainerReach.Game
{
    /// <summary>
    /// Unsafe, static wrapper over FFXIVClientStructs <see cref="RetainerManager"/>. Maps AllaganTools
    /// retainer CIDs to summonable retainer names for the view and, later, the bell automation.
    /// Only reflects the *local* roster — a retainer belonging to the active character but not yet
    /// synced to the client (e.g. a different world) resolves to null; see PLAN.md Edge Cases.
    /// </summary>
    public static unsafe class RetainerRoster
    {
        /// <summary>True once the local retainer roster has loaded (<see cref="RetainerManager.IsReady"/>).</summary>
        public static bool Ready
        {
            get
            {
                var mgr = RetainerManager.Instance();
                return mgr != null && mgr->IsReady;
            }
        }

        /// <summary>All known retainers (CID, name), skipping unassigned slots (RetainerId 0 / empty name).</summary>
        public static IReadOnlyList<(ulong Cid, string Name)> All()
        {
            var result = new List<(ulong Cid, string Name)>();

            var mgr = RetainerManager.Instance();
            if (mgr == null)
                return result;

            var retainers = mgr->Retainers;
            for (var i = 0; i < retainers.Length; i++)
            {
                var retainer = retainers[i];
                if (retainer.RetainerId == 0)
                    continue;

                var name = retainer.NameString;
                if (string.IsNullOrEmpty(name))
                    continue;

                result.Add((retainer.RetainerId, name));
            }

            return result;
        }

        /// <summary>Resolves a retainer CID to its summonable name; null if not present in the local roster.</summary>
        public static string? NameForCid(ulong cid)
        {
            foreach (var (retainerCid, name) in All())
            {
                if (retainerCid == cid)
                    return name;
            }

            return null;
        }

        /// <summary>The CID of the currently-summoned retainer; 0 if none is active.</summary>
        public static ulong ActiveRetainerCid()
        {
            var mgr = RetainerManager.Instance();
            if (mgr == null)
                return 0;

            var active = mgr->GetActiveRetainer();
            return active == null ? 0 : active->RetainerId;
        }

        /// <summary>The name of the currently-summoned retainer; null if none is active. Used as a
        /// CID-space-independent fallback for detecting that the intended retainer is now summoned.</summary>
        public static string? ActiveRetainerName()
        {
            var mgr = RetainerManager.Instance();
            if (mgr == null)
                return null;

            var active = mgr->GetActiveRetainer();
            return active == null ? null : active->NameString;
        }
    }
}
