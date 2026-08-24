using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RetainerReach.Windows
{
    /// <summary>
    /// Settings window: the <see cref="Configuration.MoveTickGap"/> slider, appearance prefs
    /// (icon scale, column visibility), and a Dev section
    /// (<see cref="Configuration.DevLog"/>/<see cref="Configuration.DevLogUrl"/>) for the
    /// XivHubPluginKit <c>DevTelemetry</c> wiring in <c>Plugin.cs</c>.
    /// </summary>
    public class ConfigWindow : Window, IDisposable
    {
        private readonly Configuration cfg;

        public ConfigWindow(Configuration configuration) : base("RetainerReach Settings###RetainerReachConfig")
        {
            cfg = configuration;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(420, 320),
                MaximumSize = new Vector2(800, 900),
            };
        }

        public void Dispose()
        {
        }

        public override void Draw()
        {
            DrawAutomationSettings();

            ImGui.Spacing();
            ImGui.Separator();
            DrawAppearanceSettings();

            ImGui.Spacing();
            ImGui.Separator();
            DrawDevSection();
        }

        private void DrawAutomationSettings()
        {
            ImGui.TextDisabled("Automation");
            ImGui.Spacing();

            var tickGap = cfg.MoveTickGap;
            ImGui.SetNextItemWidth(240);
            if (ImGui.SliderInt("Move tick gap (frames between pulls)", ref tickGap, 1, 10))
            {
                cfg.MoveTickGap = Math.Clamp(tickGap, 1, 10);
                cfg.Save();
            }
        }

        private void DrawAppearanceSettings()
        {
            ImGui.TextDisabled("Appearance");
            ImGui.Spacing();

            var scale = cfg.IconScale;
            ImGui.SetNextItemWidth(240);
            if (ImGui.SliderFloat("Item icon scale", ref scale, 1.0f, 3.0f, "%.1fx"))
            {
                cfg.IconScale = Math.Clamp(scale, 1.0f, 3.0f);
                cfg.Save();
            }

            var gridScale = cfg.GridCellScale;
            ImGui.SetNextItemWidth(240);
            if (ImGui.SliderFloat("Grid cell size", ref gridScale, 2.0f, 6.0f, "%.1fx"))
            {
                cfg.GridCellScale = Math.Clamp(gridScale, 2.0f, 6.0f);
                cfg.Save();
            }

            ImGui.Spacing();

            // Task 7.1/7.2 (Config-visibility toggles): each checkbox writes its field + Save() only
            // on the frame it actually flips, matching the rest of this window's idiom. ShowVendorColumn
            // is shown now (not gated behind Phase 8) — flipping it is harmless today since Phase 7's
            // DrawItemTable doesn't have a Vendor column yet; Phase 8 wires the actual column render.
            var showIlvl = cfg.ShowIlvlColumn;
            if (ImGui.Checkbox("Show ilvl column", ref showIlvl))
            {
                cfg.ShowIlvlColumn = showIlvl;
                cfg.Save();
            }

            var showCategory = cfg.ShowCategoryColumn;
            if (ImGui.Checkbox("Show Category column", ref showCategory))
            {
                cfg.ShowCategoryColumn = showCategory;
                cfg.Save();
            }

            var showVendor = cfg.ShowVendorColumn;
            if (ImGui.Checkbox("Show Vendor column", ref showVendor))
            {
                cfg.ShowVendorColumn = showVendor;
                cfg.Save();
            }
        }

        private void DrawDevSection()
        {
            ImGui.TextDisabled("Dev");
            ImGui.Spacing();

            var devLog = cfg.DevLog;
            if (ImGui.Checkbox("Dev log (live telemetry)", ref devLog))
            {
                cfg.DevLog = devLog;
                cfg.Save();
            }

            var devLogUrl = cfg.DevLogUrl;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("Dev log URL", "http://<lan-ip>:9999/log", ref devLogUrl, 256))
            {
                cfg.DevLogUrl = devLogUrl;
                cfg.Save();
            }
        }
    }
}
