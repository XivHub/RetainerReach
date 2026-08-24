using System.Collections.Generic;
using Lumina.Excel.Sheets;
using XivHubPluginKit.Inventory;

namespace RetainerReach.Game
{
    /// <summary>
    /// Task 4.2: lazily-built, process-lifetime cached <c>ItemUICategory</c> id -> localized name map
    /// (F4). Built once from <see cref="Plugin.DataManager"/>'s <c>ItemUICategory</c> sheet; never
    /// rebuilt per frame or per refresh.
    /// <para>
    /// Per Feasibility Finding 2 (verified via ilspycmd): <c>Item.ItemUICategory</c> is
    /// <c>RowRef&lt;ItemUICategory&gt;</c> constructed from a <c>byte</c> row offset (fits comfortably
    /// in <c>ushort</c>); <c>ItemUICategory.Name</c> is a <c>ReadOnlySeString</c> (<c>.ExtractText()</c>).
    /// </para>
    /// </summary>
    public static class ItemCategories
    {
        private static Dictionary<ushort, string>? names;

        private static Dictionary<ushort, string> Names => names ??= Build();

        /// <summary>Localized category name for <paramref name="id"/>; empty string if unknown.</summary>
        public static string NameFor(ushort id)
            => Names.TryGetValue(id, out var name) ? name : string.Empty;

        /// <summary>The <c>ItemUICategory</c> row id for <paramref name="itemId"/>; 0 if not found.</summary>
        public static ushort CategoryOf(uint itemId)
            => (ushort)(ItemSheet.ById(itemId)?.ItemUICategory.RowId ?? 0);

        private static Dictionary<ushort, string> Build()
        {
            var map = new Dictionary<ushort, string>();

            var sheet = Plugin.DataManager.GetExcelSheet<ItemUICategory>();
            if (sheet == null)
                return map;

            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (string.IsNullOrEmpty(name))
                    continue;

                map[(ushort)row.RowId] = name;
            }

            return map;
        }
    }
}
