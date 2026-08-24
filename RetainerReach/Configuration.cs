using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Numerics;
using RetainerReach.Model;

namespace RetainerReach
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        // NOTE: all fields below Version are additive (defaults apply on load for existing
        // configs). A future BREAKING change (removing/renaming/re-typing a field) must bump
        // Version and add a migration branch in Initialize — do not silently repurpose a field.
        public int Version { get; set; } = 1;

        public bool DevLog { get; set; } = false;

        public string DevLogUrl { get; set; } = string.Empty;

        public int MoveTickGap { get; set; } = 1;

        public float IconScale { get; set; } = 2.0f;

        // Task 7.1: persisted Browse view prefs (Q-S6). Filter text itself is intentionally NOT
        // persisted (Open Question 7) — only category/material/sort/column/window-size state.
        public List<ushort> SavedCategoryFilter { get; set; } = new();

        public bool SavedMaterialOnly { get; set; }

        // Default = ItemColumn (1) in MainWindow's *Column constants.
        public int SavedSortColumn { get; set; } = 1;

        public bool SavedSortAscending { get; set; } = true;

        public bool ShowIlvlColumn { get; set; } = true;

        public bool ShowCategoryColumn { get; set; } = true;

        // Phase 8: vendor value column toggle. Defined here (additive) so Phase 7's config block
        // is the single place new persisted view prefs land; wired to an actual table column in
        // Phase 8.
        public bool ShowVendorColumn { get; set; } = false;

        // Phase 9: group-by-retainer Browse layout toggle.
        public bool GroupByRetainer { get; set; } = false;

        // Browse column sizing: true = fit each column to its content width with horizontal scroll
        // (default), false = stretch columns to fill the window.
        public bool FitColumnsToContent { get; set; } = true;

        // Browse layout selector (0=Table, 1=Grouped, 2=Grid). Defaults to Grid; the v2 migration in
        // Initialize resets existing users to Grid once. A legacy -1 (pre-migration sentinel) is still
        // handled defensively in MainWindow's ctor (derives from the old GroupByRetainer bool).
        public int BrowseLayout { get; set; } = 2;

        // Grid-layout cell size, as a multiple of the text line height (larger than the inline
        // IconScale). Clamped 2.0..6.0 at the draw site, mirroring IconScale's clamp in ItemIcon.
        public float GridCellScale { get; set; } = 3.0f;

        // Default (0,0) means "never saved" — MainWindow leaves Dalamud's own default sizing in
        // that case instead of applying a zero-size Size override.
        public Vector2 WindowSize { get; set; } = default;

        // Phase 10: saved retrieve presets ("shopping lists"). Additive; persisted via the existing
        // Save() below, Version stays 1.
        public List<RetrievePreset> Presets { get; set; } = new();

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;

            // Owner-decision migrations, applied once each (presets/filters/other prefs untouched;
            // later user changes persist normally).
            var migrated = false;

            // v2: default everyone to the Grid Browse layout.
            if (Version < 2)
            {
                BrowseLayout = 2; // Grid — see MainWindow.BrowseLayout (0=Table, 1=Grouped, 2=Grid)
                GroupByRetainer = false;
                migrated = true;
            }

            // v3: default Table view to fit-columns-to-content.
            if (Version < 3)
            {
                FitColumnsToContent = true;
                migrated = true;
            }

            if (migrated)
            {
                Version = 3;
                Save();
            }
        }

        public void Save()
        {
            this.pluginInterface!.SavePluginConfig(this);
        }
    }
}
