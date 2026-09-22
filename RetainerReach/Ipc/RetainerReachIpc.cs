using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using RetainerReach.Logic;

namespace RetainerReach.Ipc
{
    /// <summary>
    /// The IPC RetainerReach *provides*: read-only retainer inventory contents, so another plugin
    /// can ask how many of an item a retainer is holding without duplicating the AllaganTools row
    /// walk and its container filtering.
    ///
    /// What it reports is stock in a retainer's own item pages, which is exactly the stock that is
    /// NOT on sale: crystals, gil, equipped gear and market listings are all excluded. A unit here
    /// is one that was bought or pulled back off the board and is sitting idle.
    ///
    /// Only primitives, framework collections and value tuples cross the gate, since a plugin's own
    /// types are loaded in its own context and would not resolve on the caller's side.
    /// </summary>
    public sealed class RetainerReachIpc : IDisposable
    {
        /// <summary>
        /// Bumped whenever a signature or the meaning of a returned value changes. A caller reads
        /// this first and stands down unless it matches what it was written against.
        /// </summary>
        public const int Version = 1;

        private const string IpcApiVersion = "RetainerReach.ApiVersion";
        private const string IpcGetRetainerInventories = "RetainerReach.GetRetainerInventories";
        private const string IpcGetItemCount = "RetainerReach.GetItemCount";

        private readonly ICallGateProvider<int> apiVersion;
        private readonly ICallGateProvider<Dictionary<ulong, List<(uint ItemId, bool Hq, uint Qty)>>> getRetainerInventories;
        private readonly ICallGateProvider<ulong, uint, bool, uint> getItemCount;

        public RetainerReachIpc()
        {
            apiVersion = Plugin.PluginInterface.GetIpcProvider<int>(IpcApiVersion);
            apiVersion.RegisterFunc(() => Version);

            getRetainerInventories = Plugin.PluginInterface
                .GetIpcProvider<Dictionary<ulong, List<(uint ItemId, bool Hq, uint Qty)>>>(IpcGetRetainerInventories);
            getRetainerInventories.RegisterFunc(GetRetainerInventories);

            getItemCount = Plugin.PluginInterface.GetIpcProvider<ulong, uint, bool, uint>(IpcGetItemCount);
            getItemCount.RegisterFunc(GetItemCount);
        }

        public void Dispose()
        {
            apiVersion.UnregisterFunc();
            getRetainerInventories.UnregisterFunc();
            getItemCount.UnregisterFunc();
        }

        /// <summary>
        /// Every retainer of the logged-in character, by CID, with what each one holds as
        /// (ItemId, Hq, Qty) rows summed across its item pages. Empty when AllaganTools is absent or
        /// has not scanned yet, which makes a caller's stock read too small rather than invented.
        ///
        /// Keyed by CID rather than by name because a name is only resolvable while the local
        /// retainer roster is loaded; the caller already knows its own roster.
        /// </summary>
        private Dictionary<ulong, List<(uint ItemId, bool Hq, uint Qty)>> GetRetainerInventories()
        {
            try
            {
                return UnifiedInventory.HoldingsByRetainer();
            }
            catch (Exception ex)
            {
                Plugin.Logger.Error(ex, "[RetainerReach] IPC: could not read retainer inventories.");
                return new Dictionary<ulong, List<(uint ItemId, bool Hq, uint Qty)>>();
            }
        }

        /// <summary>
        /// Units of one item, of one quality, held by one retainer; 0 when the retainer holds none.
        /// Quality is part of the identity: NQ and HQ are separate stacks and separate market
        /// channels.
        /// </summary>
        private uint GetItemCount(ulong retainerCid, uint itemId, bool hq)
        {
            try
            {
                if (!UnifiedInventory.HoldingsByRetainer().TryGetValue(retainerCid, out var holdings))
                    return 0;

                return holdings
                    .Where(h => h.ItemId == itemId && h.Hq == hq)
                    .Aggregate(0u, (total, h) => total + h.Qty);
            }
            catch (Exception ex)
            {
                Plugin.Logger.Error(ex, "[RetainerReach] IPC: could not count item {0} on retainer {1}.",
                    itemId, retainerCid);
                return 0;
            }
        }
    }
}
