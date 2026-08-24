using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using XivHubPluginKit.Inventory;

namespace RetainerReach.Game
{
    /// <summary>
    /// Draws a single (ItemId, Hq) game icon inline via <see cref="Plugin.TextureProvider"/>, sized to
    /// the current text line height scaled by <see cref="Configuration.IconScale"/> (Task 2.2) so it
    /// sits flush with adjacent item-name text. Shared by the Browse table, Preview panel, and Results
    /// panel (all draw the icon left of the item name).
    /// <see cref="ISharedImmediateTexture.GetWrapOrEmpty"/> never throws and never blocks — it simply
    /// returns an empty texture until the real one has finished loading, so calling this every frame
    /// before the icon is ready is safe.
    /// </summary>
    public static class ItemIcon
    {
        /// <summary>
        /// Draws the icon for <paramref name="itemId"/>/<paramref name="hq"/> at the current cursor
        /// position and returns true if something was drawn (so the caller knows whether to follow up
        /// with <c>ImGui.SameLine()</c> before the item name). Draws nothing — and returns false — when
        /// the item has no icon (id 0) or its texture isn't available yet.
        /// </summary>
        public static bool Draw(uint itemId, bool hq)
        {
            var iconId = ItemSheet.ById(itemId)?.Icon ?? 0;
            return DrawIcon(iconId, hq);
        }

        /// <summary>
        /// Task 4.3: icon-id-based overload — draws directly from an already-known
        /// <see cref="Model.UnifiedItem.IconId"/> with no per-row <see cref="ItemSheet.ById"/> sheet
        /// lookup. Browse rows route through this overload since <c>IconId</c> is stamped onto each
        /// <see cref="Model.UnifiedItem"/> once at <see cref="Logic.UnifiedInventory.Build"/>; Preview/
        /// Results may keep the itemId-based overload above.
        /// Typed <c>ushort</c> (matching the stamped <c>IconId</c> field, which mirrors
        /// <c>Lumina.Excel.Sheets.Item.Icon</c>'s real type) rather than <c>uint</c> — the plan's literal
        /// "Draw(uint iconId, bool hq)" would collide with the itemId-based overload above (both would
        /// be (uint, bool)), which C# cannot distinguish by parameter name alone. See report deviations.
        /// </summary>
        public static bool Draw(ushort iconId, bool hq)
        {
            return DrawIcon(iconId, hq);
        }

        private static bool DrawIcon(uint iconId, bool hq)
        {
            if (iconId == 0)
                return false;

            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId, itemHq: hq)).GetWrapOrEmpty();
            if (wrap.Handle.IsNull)
                return false;

            var sz = ImGui.GetTextLineHeight() * Math.Clamp(Plugin.C.IconScale, 1.0f, 3.0f);
            ImGui.Image(wrap.Handle, new Vector2(sz, sz));
            return true;
        }

        /// <summary>
        /// Grid-cell variant: hands back the raw game-icon texture handle for an already-known
        /// <see cref="Model.UnifiedItem.IconId"/> so the caller can paint it via a draw list
        /// (<c>ImGui.GetWindowDrawList().AddImage</c>) at an explicit square size — which the
        /// text-line-height-bound <see cref="Draw"/> can't do. Returns false for icon id 0 or a
        /// texture that hasn't finished loading, the same non-blocking guarantee as <see cref="DrawIcon"/>.
        /// The handle is <c>ImTextureID</c> (what <see cref="ISharedImmediateTexture"/> yields) — never
        /// route it through <c>nint</c>, which the ImGui binding can't convert back to <c>ImTextureID</c>.
        /// </summary>
        public static bool TryGetGameIconHandle(ushort iconId, bool hq, out ImTextureID handle)
        {
            handle = default;
            if (iconId == 0)
                return false;

            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId, itemHq: hq)).GetWrapOrEmpty();
            if (wrap.Handle.IsNull)
                return false;

            handle = wrap.Handle;
            return true;
        }

        /// <summary>
        /// Task 2.3: call directly after <see cref="Draw"/> + <c>ImGui.SameLine()</c>, before the
        /// following text draw call, to vertically center that text against the icon — which may now
        /// be taller than a text line once <see cref="Configuration.IconScale"/> exceeds 1.0. Shared by
        /// all three <see cref="Draw"/> call sites so the alignment stays consistent everywhere.
        /// </summary>
        public static void AlignTextToIcon()
        {
            var iconSize = ImGui.GetTextLineHeight() * Math.Clamp(Plugin.C.IconScale, 1.0f, 3.0f);
            var textHeight = ImGui.GetTextLineHeight();
            var offset = (iconSize - textHeight) / 2f;
            if (offset > 0f)
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offset);
        }
    }
}
