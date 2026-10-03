using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using RetainerReach.Game;
using XivHubPluginKit.Retainer;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace RetainerReach.Automation
{
    /// <summary>
    /// Thin, throttled wrappers over the bell/retainer-list/retainer-inventory UI, mirroring
    /// AutoRetainer's handler shapes (<c>PlayerWorldHandlers</c>/<c>RetainerListHandlers</c>/
    /// <c>RetainerHandlers</c> — see PLAN.md Existing Patterns). Every action gates on
    /// <see cref="GenericThrottle"/> so repeated <see cref="RetrieveScheduler"/> ticks don't spam the
    /// same UI action multiple times before the game has processed the previous one.
    ///
    /// <see cref="SetBellTarget"/> and <see cref="InteractBell"/> are deliberately separate calls
    /// meant to run on different scheduler ticks — AutoRetainer splits
    /// <c>PlayerWorldHandlers.SelectNearestBell</c> (set target) and
    /// <c>InteractWithTargetedBell</c> (interact) into distinct steps on separate frames. Do NOT
    /// combine them into one same-frame call (PLAN.md Task 5.2 / Needs In-Game Verification item 3).
    /// </summary>
    public static unsafe class RetainerUi
    {
        private const int ThrottleFrames = 10;

        private static string? cachedQuitText;

        /// <summary>Shared UI-action throttle, mirroring AutoRetainer's <c>Utils.GenericThrottle</c>.</summary>
        private static bool GenericThrottle => FrameThrottler.Throttle("RetainerReach.GenericThrottle", ThrottleFrames);

        /// <summary><see cref="GenericThrottle"/> for the kit's retainer helpers, which take a throttle.</summary>
        internal static bool Throttle() => GenericThrottle;

        /// <summary>The localized retainer-menu "Quit" entry text (<c>Addon</c> sheet row 2383), cached once — mirrors AutoRetainer's <c>SelectQuit</c>.</summary>
        private static string QuitText
            => cachedQuitText ??= Svc.Data.GetExcelSheet<Addon>().GetRow(2383).Text.GetText(true);

        /// <summary>Sets the player's target to <paramref name="bell"/>. Interact on a later tick, not this one.</summary>
        public static bool SetBellTarget(IGameObject bell)
        {
            if (bell == null || !GenericThrottle)
                return false;

            Svc.Targets.Target = bell;
            return true;
        }

        /// <summary>Interacts with the already-targeted <paramref name="bell"/>.</summary>
        public static bool InteractBell(IGameObject bell)
        {
            if (bell == null || !bell.IsTargetable)
                return false;

            if (Player.Object == null || Vector3.Distance(Player.Object.Position, bell.Position) >= BellFinder.ValidInteractionDistance(bell))
                return false;

            if (Player.IsAnimationLocked)
                return false;

            if (GenericThrottle && EzThrottler.Throttle("RetainerReach.InteractBell", 5000))
            {
                TargetSystem.Instance()->InteractWithObject((GameObject*)bell.Address, false);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Advances the retainer greeting/farewell subtitle box (the <c>Talk</c> addon). A summoned
        /// retainer plays a greeting line in the Talk box that blocks the "Entrust or withdraw items"
        /// SelectString from opening until it is clicked through; nothing else in the flow dismisses
        /// it, so a run would otherwise stall in <c>OpenInventory</c> until it timed out. Mirrors
        /// AutoRetainer's <c>MiniTA.Tick</c> Talk click. Uses its own throttle key (not the shared
        /// <see cref="GenericThrottle"/>) so progressing the greeting never starves the
        /// list/select/open UI actions competing for that key. Returns true when a click was fired.
        /// </summary>
        public static bool ProgressTalk()
        {
            if (!GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var addon) || !GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
                return false;

            if (!FrameThrottler.Throttle("RetainerReach.ProgressTalk", ThrottleFrames))
                return false;

            new AddonMaster.Talk((nint)addon).Click();
            return true;
        }

        /// <summary>
        /// True once a retainer's own menu (a <c>SelectString</c>) is present and ready — the signal
        /// that RetainerList selection actually took and we can now pick "Entrust or withdraw items".
        /// </summary>
        public static bool RetainerMenuReady()
            => GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase);

        /// <summary>True when the RetainerList addon is present and ready.</summary>
        public static bool RetainerListOpen()
            => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon);

        /// <summary>
        /// Selects the retainer named <paramref name="name"/> from the open RetainerList. Returns
        /// true once actually fired, false while still trying (name not found yet / entry not
        /// selectable / throttled).
        /// </summary>
        public static bool? SelectRetainer(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !GenericHelpers.IsAddonReady(addon))
                return false;

            var list = new AddonMaster.RetainerList(addon);
            foreach (var retainer in list.Retainers)
            {
                if (!NameMatches(retainer.Name, name))
                    continue;

                if (GenericThrottle)
                {
                    // Fire the RetainerList select callback directly (opcode 2 + entry index), mirroring
                    // ECommons' AddonMaster.RetainerList.Select but WITHOUT its IsActive gate: that
                    // reader bit can read false on our ECommons/game version and silently no-op the
                    // summon, so the retainer menu never opens. The scheduler confirms success by
                    // waiting for the menu (RetainerMenuReady), so an over-eager fire is harmless.
                    Callback.Fire(addon, true, 2, (uint)retainer.Index, Callback.ZeroAtkValue, Callback.ZeroAtkValue);
                    return true;
                }

                return false;
            }

            // No list entry matched the target name — dump the list (throttled) so a name-format or
            // CID→name mismatch surfaces instead of a silent SelectRetainer timeout.
            if (EzThrottler.Throttle("RetainerReach.RetainerListDump", 3000))
                Svc.Log.Warning($"[RetainerReach] retainer '{name}' not matched in list. {DescribeRetainerList()}");

            return false;
        }

        private static bool NameMatches(string a, string b)
            => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Compact dump of the open RetainerList (each entry's name / selectable flag / index) plus the
        /// currently-active retainer, for diagnostics and DevTelemetry. Returns a "(not open)" marker
        /// when the addon isn't up. Safe to call every frame.
        /// </summary>
        public static string DescribeRetainerList()
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !GenericHelpers.IsAddonReady(addon))
                return "RetainerList=(not open)";

            var list = new AddonMaster.RetainerList(addon);
            var entries = string.Join(" | ", list.Retainers.Select(r => $"'{r.Name}'[active={r.IsActive},i={r.Index}]"));
            return $"entries=[{entries}] active=(cid={RetainerRoster.ActiveRetainerCid()},name='{RetainerRoster.ActiveRetainerName()}')";
        }

        /// <summary>
        /// Tolerant menu-entry match: trims and compares case-insensitively, accepting either a
        /// prefix or a substring hit. Deliberately looser than an ordinal exact/prefix match — the
        /// sheet text and the live addon entry can differ by trailing punctuation, an auto-translate
        /// wrapper, or stray whitespace, which would otherwise cause a silent no-match.
        /// </summary>
        private static bool MenuEntryMatches(string entryText, string target)
        {
            entryText = entryText.Trim();
            target = target.Trim();
            if (target.Length == 0)
                return false;

            return entryText.StartsWith(target, StringComparison.OrdinalIgnoreCase)
                || entryText.Contains(target, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Selects the retainer menu's "Quit" entry (<c>Addon</c> row 2383) to leave the current
        /// retainer and return to the RetainerList, so a different retainer can be picked next
        /// (mirrors AutoRetainer's <c>SelectQuit</c>). Returns true once fired.
        /// </summary>
        public static bool? QuitRetainerMenu()
        {
            if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var addon) || !GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
                return false;

            var text = QuitText;
            var master = new AddonMaster.SelectString(addon);
            foreach (var entry in master.Entries)
            {
                if (!MenuEntryMatches(entry.Text, text))
                    continue;

                if (GenericThrottle)
                {
                    entry.Select();
                    return true;
                }

                return false;
            }

            if (EzThrottler.Throttle("RetainerReach.QuitDump", 3000))
                Svc.Log.Warning($"[RetainerReach] retainer 'Quit' entry (target \"{text}\") not found. {RetainerWalk.DescribeSelectString()}");

            return false;
        }

        /// <summary>Closes the RetainerList addon via <c>FireCallback(1, {Int=-1})</c>, mirroring AutoRetainer. See <see cref="CloseRetainerInventory"/> for <paramref name="force"/>.</summary>
        public static bool? CloseRetainerList(bool force = false)
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !GenericHelpers.IsAddonReady(addon))
                return true;

            if (!force && !GenericThrottle)
                return false;

            Callback.Fire(addon, false, -1);
            return true;
        }

        /// <summary>Hides the Retainer agent (<see cref="AgentId.Retainer"/>), tearing down whatever retainer UI remains. See <see cref="CloseRetainerInventory"/> for <paramref name="force"/>.</summary>
        public static bool? CloseRetainerAgent(bool force = false)
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
                return true;

            var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
            if (agent == null || !agent->IsAgentActive())
                return true;

            if (!force && !GenericThrottle)
                return false;

            agent->Hide();
            return true;
        }
    }
}
