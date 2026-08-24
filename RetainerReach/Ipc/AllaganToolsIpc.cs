using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using ECommons.Reflection;

namespace RetainerReach.Ipc
{
    /// <summary>
    /// Read-only IPC subscriber for AllaganTools (source repo InventoryTools, InternalName
    /// "InventoryTools" — confirmed offline from InventoryTools.json). Drives the unified inventory
    /// view (<see cref="Logic.UnifiedInventory"/>). Every accessor is a safe no-op (empty/default)
    /// when AllaganTools is absent or the IPC call fails, so RetainerReach never throws when the
    /// optional dependency isn't installed.
    ///
    /// Ports the subscriber pattern from
    /// FFMarketConnector/Bridge/AllaganToolsBridge.cs; provider names + row layout confirmed against
    /// InventoryTools/IPC/IPCService.cs (see PLAN.md "Existing Patterns").
    /// </summary>
    public static class AllaganToolsIpc
    {
        private const string InternalName = "InventoryTools";

        private static readonly ICallGateSubscriber<bool> isInitializedSub =
            Plugin.PluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");

        private static readonly ICallGateSubscriber<ulong> currentCharacterSub =
            Plugin.PluginInterface.GetIpcSubscriber<ulong>("AllaganTools.CurrentCharacter");

        private static readonly ICallGateSubscriber<bool, HashSet<ulong>> charactersOwnedByActiveSub =
            Plugin.PluginInterface.GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive");

        private static readonly ICallGateSubscriber<ulong, HashSet<ulong[]>> characterItemsSub =
            Plugin.PluginInterface.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems");

        /// <summary>True when the AllaganTools plugin (InternalName "InventoryTools") is installed.</summary>
        public static bool Installed
            => DalamudReflector.TryGetDalamudPlugin(InternalName, out _, false, true);

        /// <summary>True once AllaganTools has completed its own inventory scan/init.</summary>
        public static bool IsInitialized()
        {
            try
            {
                return isInitializedSub.InvokeFunc();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The active character's CID as reported by AllaganTools; 0 on failure.</summary>
        public static ulong CurrentCharacter()
        {
            try
            {
                return currentCharacterSub.InvokeFunc();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Every CID AllaganTools associates with the active character (retainers, FCs, and
        /// optionally the owner itself). Empty on failure.
        /// </summary>
        public static HashSet<ulong> CharactersOwnedByActive(bool includeOwner)
        {
            try
            {
                return charactersOwnedByActiveSub.InvokeFunc(includeOwner) ?? new HashSet<ulong>();
            }
            catch (Exception)
            {
                return new HashSet<ulong>();
            }
        }

        /// <summary>
        /// Raw inventory rows for the given CID (<c>InventoryItem.ToNumeric()</c> layout:
        /// [0]=Container [1]=Slot [2]=ItemId [3]=Qty [6]=Flags). Empty on failure.
        /// </summary>
        public static HashSet<ulong[]> CharacterItems(ulong cid)
        {
            try
            {
                return characterItemsSub.InvokeFunc(cid) ?? new HashSet<ulong[]>();
            }
            catch (Exception)
            {
                return new HashSet<ulong[]>();
            }
        }
    }
}
