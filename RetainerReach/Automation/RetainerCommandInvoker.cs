using System;
using System.Runtime.InteropServices;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using AgentModulePtr = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule;

namespace RetainerReach.Automation
{
    /// <summary>
    /// Binds and invokes the native retainer item command (AutoRetainer's
    /// <c>RetainerItemCommandDelegate</c> — see PLAN.md Architecture Decision / Existing Patterns
    /// "Retainer item transaction"). This is the ONLY mechanism RetainerReach uses to move items out
    /// of a retainer; <c>InventoryManager.MoveItemSlot</c> is never called on a <c>RetainerPage*</c>
    /// container. The signature is bound once (lazily, on first touch of this static class) and
    /// invoked directly — no hook, since RetainerReach never needs to observe/intercept the call,
    /// only fire it.
    /// </summary>
    public static unsafe class RetainerCommandInvoker
    {
        // AutoRetainer Internal/Memory.cs: RetainerItemCommandHook = new("48 89 5C 24 ?? 48 89 6C 24
        // ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0", ...). NEEDS IN-GAME VERIFICATION
        // (PLAN.md item 6): whether this still binds on the current patch.
        private const string Signature =
            "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

        /// <summary>Local copy of AutoRetainer's <c>Internal/InventoryManagement/RetainerItemCommand.cs</c> enum.</summary>
        private enum RetainerItemCommand : long
        {
            RetrieveFromRetainer = 0,
            EntrustToRetainer = 1,
            EntrustQuantity = 4,
            HaveRetainerSellItem = 5,
        }

        private delegate void RetainerItemCommandDelegate(nint agentModule, uint slot, InventoryType invType, uint a4, RetainerItemCommand cmd);

        private static readonly RetainerItemCommandDelegate? invoke;

        /// <summary>
        /// True once the native command signature bound successfully. False means the byte pattern
        /// broke on a game patch (PLAN.md Edge Cases "Retainer command signature breaks on a game
        /// patch") — every <see cref="Retrieve"/> call is then a hard no-op and the scheduler treats
        /// it as an abort-worthy condition rather than calling a bad pointer.
        /// </summary>
        public static bool Bound { get; private set; }

        static RetainerCommandInvoker()
        {
            try
            {
                var address = Svc.SigScanner.ScanText(Signature);
                if (address == nint.Zero)
                {
                    Bound = false;
                    Svc.Log.Error("[RetainerReach] RetainerItemCommand signature not found (ScanText returned 0); retrieve is disabled until the plugin is updated for this game version.");
                    return;
                }

                invoke = Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(address);
                Bound = true;
            }
            catch (Exception ex)
            {
                Bound = false;
                Svc.Log.Error(ex, "[RetainerReach] Failed to bind the RetainerItemCommand signature; retrieve is disabled until the plugin is updated for this game version.");
            }
        }

        /// <summary>True while the Retainer agent is active, mirroring AutoRetainer's <c>IsAgentRetainerActive</c>.</summary>
        public static bool RetainerActive()
        {
            var agentModule = AgentModulePtr.Instance();
            if (agentModule == null)
                return false;

            var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
            return agent != null && agent->IsAgentActive();
        }

        /// <summary>
        /// Invokes <c>RetrieveFromRetainer</c> for the given retainer-container slot, moving its
        /// entire stack to the player's bags — the command has no qty arg (PLAN.md Architecture
        /// Decision "Whole-stack"). Preconditions mirror AutoRetainer's
        /// <c>IsAgentRetainerActive</c> + <c>IsRetainerInventoryLoaded</c> checks: signature bound,
        /// Retainer agent active, retainer inventory addon ready. Returns false without invoking if
        /// any precondition fails — never calls into a possibly-invalid function pointer, and a
        /// mis-timed call is not treated as a move (PLAN.md Edge Cases). The agent module is fetched
        /// and null-validated once here and reused for both the active check and the command's first
        /// argument (the pointer arithmetic mirrors AutoRetainer's
        /// <c>InventorySpaceManager.AgentRetainerItemCommandModule</c> = Retainer agent + 40).
        /// </summary>
        public static bool Retrieve(uint slot, InventoryType retainerPage)
        {
            if (!Bound || invoke == null)
                return false;

            var agentModule = AgentModulePtr.Instance();
            if (agentModule == null)
                return false;

            var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
            if (agent == null || !agent->IsAgentActive())
                return false;

            if (!RetainerUi.RetainerInventoryReady())
                return false;

            try
            {
                // a4's meaning is unconfirmed; AutoRetainer always passes 0 for every command it fires
                // (SafeSellSlot -> HaveRetainerSellItem, 0). NEEDS IN-GAME VERIFICATION (PLAN.md item 6).
                invoke((nint)agent + 40, slot, retainerPage, 0, RetainerItemCommand.RetrieveFromRetainer);
                return true;
            }
            catch (Exception ex)
            {
                // Never let a managed exception from the native invoke escape into the framework tick
                // (it would re-enter the same broken state every frame). Treated as a failed pull.
                Svc.Log.Error(ex, "[RetainerReach] RetainerItemCommand invoke threw; treating the pull as failed.");
                return false;
            }
        }
    }
}
