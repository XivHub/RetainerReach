using System.Collections.Generic;
using Lumina.Excel.Sheets;
using XivHubPluginKit.Inventory;

namespace RetainerReach.Game
{
    /// <summary>
    /// Lazily-built, process-lifetime cached <c>ItemSearchCategory</c> (the market-board category
    /// taxonomy: Stone, Metal, Lumber, Cloth, …) id -&gt; (localized name, icon id). Unlike
    /// <c>ItemUICategory</c>, these rows carry a dedicated category <c>Icon</c>, which is what the
    /// Browse category chips render. Built once from the sheet; never rebuilt per frame/refresh.
    /// </summary>
    public static class ItemSearchCategories
    {
        private static Dictionary<ushort, (string Name, uint Icon)>? map;

        private static Dictionary<ushort, (string Name, uint Icon)> Map => map ??= Build();

        /// <summary>Localized category name for <paramref name="id"/>; empty if unknown/uncategorized.</summary>
        public static string NameFor(ushort id)
            => Map.TryGetValue(id, out var v) ? v.Name : string.Empty;

        /// <summary>Category icon id for <paramref name="id"/>; 0 if unknown.</summary>
        public static uint IconFor(ushort id)
            => Map.TryGetValue(id, out var v) ? v.Icon : 0;

        /// <summary>The <c>ItemSearchCategory</c> row id for <paramref name="itemId"/>; 0 if not found/uncategorized.</summary>
        public static ushort CategoryOf(uint itemId)
            => (ushort)(ItemSheet.ById(itemId)?.ItemSearchCategory.RowId ?? 0);

        private static Dictionary<ushort, (string Name, uint Icon)> Build()
        {
            var result = new Dictionary<ushort, (string Name, uint Icon)>();

            var sheet = Plugin.DataManager.GetExcelSheet<ItemSearchCategory>();
            if (sheet == null)
                return result;

            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (string.IsNullOrEmpty(name))
                    continue;

                result[(ushort)row.RowId] = (name, (uint)row.Icon);
            }

            return result;
        }
    }
}
