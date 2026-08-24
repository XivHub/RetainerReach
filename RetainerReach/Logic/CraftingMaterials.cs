using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace RetainerReach.Logic
{
    /// <summary>
    /// Task 4.1: lazily-built, process-lifetime cached set of every item id that appears as an
    /// ingredient in any <see cref="Recipe"/> (F5). Built once from
    /// <see cref="Plugin.DataManager"/>'s <c>Recipe</c> sheet; game data doesn't change mid-session, so
    /// this is never rebuilt per frame or per refresh.
    /// <para>
    /// Per Feasibility Finding 1 (verified via ilspycmd against Lumina.Excel.dll): <c>Recipe.Ingredient</c>
    /// is a <c>Collection&lt;RowRef&lt;Item&gt;&gt;</c> of 8 slots; empty slots have <c>RowId == 0</c>.
    /// <c>RowRef&lt;T&gt;.RowId</c> is a direct field read that never throws, unlike <c>.Value</c> /
    /// <c>.ValueNullable</c> which resolve against the sheet — so this reads <c>.RowId</c> only.
    /// </para>
    /// </summary>
    public static class CraftingMaterials
    {
        private static HashSet<uint>? ingredientIds;

        private static HashSet<uint> IngredientIds => ingredientIds ??= Build();

        /// <summary>True if <paramref name="itemId"/> is used as an ingredient in any recipe.</summary>
        public static bool IsMaterial(uint itemId) => IngredientIds.Contains(itemId);

        private static HashSet<uint> Build()
        {
            var ids = new HashSet<uint>();

            var sheet = Plugin.DataManager.GetExcelSheet<Recipe>();
            if (sheet == null)
                return ids;

            foreach (var recipe in sheet)
            {
                foreach (var ingredient in recipe.Ingredient)
                {
                    if (ingredient.RowId != 0)
                        ids.Add(ingredient.RowId);
                }
            }

            return ids;
        }
    }
}
