using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using Lumina.Excel.Sheets;

namespace RetainerReach.Automation
{
    /// <summary>
    /// Locates and range-checks Summoning Bells, mirroring AutoRetainer's
    /// <c>Utils.GetNearestRetainerBell</c>/<c>GetReachableRetainerBell</c>/<c>GetValidInteractionDistance</c>
    /// (see PLAN.md Existing Patterns / AutoRetainer). Requires proximity — no walk-to-bell (Architecture
    /// Decision: "Bell scope: require proximity").
    /// </summary>
    public static class BellFinder
    {
        private static string? cachedBellName;

        /// <summary>
        /// The localized Summoning Bell object name (<c>EObjName</c> row 2000401, "Lang.BellName" in
        /// AutoRetainer), cached once. NEEDS IN-GAME VERIFICATION: exact localized text match on the
        /// current patch (PLAN.md Needs In-Game Verification item 2).
        /// </summary>
        private static string BellName
            => cachedBellName ??= Svc.Data.GetExcelSheet<EObjName>().GetRow(2000401).Singular.ExtractText();

        /// <summary>The nearest bell (any distance) and its distance from the player; null if none exists.</summary>
        public static IGameObject? Nearest(out float distance)
        {
            distance = float.MaxValue;
            IGameObject? nearest = null;

            var playerPosition = Player.Object?.Position;
            if (playerPosition == null)
                return null;

            foreach (var obj in Svc.Objects)
            {
                if (!IsBell(obj))
                    continue;

                var d = Vector3.Distance(playerPosition.Value, obj.Position);
                if (d < distance)
                {
                    distance = d;
                    nearest = obj;
                }
            }

            return nearest;
        }

        /// <summary>True when at least one bell is within its <see cref="ValidInteractionDistance"/> and targetable.</summary>
        public static bool Reachable()
        {
            if (Player.Object == null)
                return false;

            foreach (var obj in Svc.Objects)
            {
                if (IsBell(obj) && IsReachable(obj))
                    return true;
            }

            return false;
        }

        /// <summary>True when the specific <paramref name="bell"/> is targetable and within its valid interaction distance.</summary>
        public static bool IsReachable(IGameObject bell)
            => Player.Object != null
               && bell.IsTargetable
               && Vector3.Distance(Player.Object.Position, bell.Position) < ValidInteractionDistance(bell);

        /// <summary>The currently-targeted game object, if it is a bell; otherwise null.</summary>
        public static IGameObject? TargetedBell()
        {
            var target = Svc.Targets.Target;
            return target != null && IsBell(target) ? target : null;
        }

        /// <summary>
        /// The bell the run should use: a bell the player has explicitly targeted, when it is itself
        /// reachable (PLAN.md Assumptions: "already standing within interaction range of a Summoning
        /// Bell ... or have one targeted"), otherwise the nearest bell. Null when no bell exists.
        /// </summary>
        public static IGameObject? Preferred()
        {
            var targeted = TargetedBell();
            if (targeted != null && IsReachable(targeted))
                return targeted;

            return Nearest(out _);
        }

        /// <summary>
        /// Valid interaction distance for a bell, ported from AutoRetainer's
        /// <c>Utils.GetValidInteractionDistance</c>: 6.5f for housing bells, 4.75f inside an inn room,
        /// else 4.6f. NEEDS IN-GAME VERIFICATION alongside the bell name match (item 2).
        /// </summary>
        public static float ValidInteractionDistance(IGameObject bell)
        {
            if (bell.ObjectKind == ObjectKind.HousingEventObject)
                return 6.5f;

            if (Array.IndexOf(Inns.List, (ushort)Svc.ClientState.TerritoryType) >= 0)
                return 4.75f;

            return 4.6f;
        }

        private static bool IsBell(IGameObject obj)
            => (obj.ObjectKind is ObjectKind.HousingEventObject or ObjectKind.EventObj)
               && string.Equals(obj.Name.ToString(), BellName, StringComparison.OrdinalIgnoreCase);
    }
}
