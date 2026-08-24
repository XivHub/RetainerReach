using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RetainerReach.Automation;
using RetainerReach.Game;
using RetainerReach.Ipc;
using RetainerReach.Logic;
using RetainerReach.Model;
using XivHubPluginKit.Inventory;
using XivHubPluginKit.UI;

namespace RetainerReach.Windows
{
    public class MainWindow : Window, IDisposable
    {
        // Grid "slot" look, approximating the native inventory cell: a recessed well one step below
        // the window on the surface ramp, rimmed one step above it, icon inset 1px inside it.
        private const float SlotRounding = 3f;
        private static Vector4 SlotBg => HubColors.Get("HubGround", 0.95f);
        private static Vector4 SlotBorder => HubColors.Get("HubSurface");

        // Cell overlays are painted straight onto the draw list over arbitrary icon art, so they
        // carry their own contrast: a near-black plate/shadow and the text colour on top of it.
        private static Vector4 CellShade => HubColors.Get("HubGround", 0.90f);
        private static Vector4 CellPlate => HubColors.Get("HubGround", 0.55f);
        private static Vector4 CellHover => HubColors.Get("HubText", 0.60f);
        private static Vector4 CellSelected => HubColors.Get("HubGold", 0.30f);

        private readonly Configuration cfg;

        // Cached view state — UnifiedInventory.Build() is only re-run on Refresh, never every frame.
        private List<UnifiedItem> cachedItems = new();
        private bool hasLoaded;
        private string filterText = string.Empty;

        // Built once per RefreshItems so hot paths (Running panel / Results / tooltips) resolve a name
        // by O(1) lookup instead of linear-scanning cachedItems every frame; plus the memoized total
        // inventory vendor value shown in the header.
        private Dictionary<uint, string> nameById = new();
        private ulong totalRetainerValue;

        // Prebuilt header stat line (rebuilt only in RefreshItems — its inputs change only on refresh).
        private string headerStats = string.Empty;

        // Presets panel is toggled open by a compact toolbar button (not an always-on full-width bar).
        private bool showPresets;

        // Task 4.5/4.6: memoized filter pipeline. viewCache is the name/category/material-filtered
        // list; it's only recomputed when viewDirty, never per frame. categoryOptions (distinct
        // present categories, sorted by name) is recomputed once per RefreshItems, never per frame.
        private List<UnifiedItem> viewCache = new();
        private bool viewDirty = true;
        // Selected ItemSearchCategory ids (market-board taxonomy) — the icon-chip / Options category
        // filter. Transient per session (like selectedRetainers).
        private readonly HashSet<ushort> selectedCategories = new();
        private bool materialOnly;
        // Present search categories, ordered by item count (desc) so the chip bar can take the top N;
        // each carries its icon + count. Recomputed once per RefreshItems, never per frame.
        private List<(ushort Id, string Name, uint Icon, int Count)> categoryOptions = new();
        // Same set as categoryOptions but name-sorted, for the Options combo (avoids a per-frame OrderBy).
        private List<(ushort Id, string Name, uint Icon, int Count)> categoryOptionsAlpha = new();

        // How many category chips the quick-bar shows inline; the rest live in the Options combo.
        private const int CategoryChipCount = 8;

        // Task 9.1: Browse layout toggle — flat table (default) vs grouped-by-retainer. Mirrors the
        // materialOnly/selectedCategories idiom: a local field synced with the persisted
        // cfg.GroupByRetainer on toggle (debounced Save(), only on the frame it actually flips).
        private bool groupByRetainer;

        // Browse column sizing toggle — synced with cfg.FitColumnsToContent (see DrawFilterBar). false =
        // stretch to window; true = fit-to-content + horizontal scroll.
        private bool fitColumns;

        // Active Browse layout; migrated once from the legacy cfg.GroupByRetainer bool in the ctor.
        private BrowseLayout browseLayout;

        // Opens the settings window; wired by Plugin (MainWindow doesn't own the ConfigWindow).
        public Action? OpenSettings;

        // Task 9.3: per-retainer filter — ANDs into RecomputeView alongside name/category/material.
        // retainerOptions (distinct retainers present in cachedItems) is computed once per
        // RefreshItems, never per frame, exactly like categoryOptions.
        private readonly HashSet<ulong> selectedRetainers = new();
        private List<(ulong Cid, string Name)> retainerOptions = new();

        // Task 9.2: grouped-by-retainer view of viewCache, built inside RecomputeView (memoized, not
        // per frame). One entry per retainer present in viewCache's items' Holdings; an item held by
        // N retainers appears under each of those N entries. Selection stays item-level (keyed by
        // (ItemId, Hq)) — grouping is purely a view lens, so selecting an item under retainer A also
        // shows it selected under retainer B.
        private List<(string Retainer, ulong Cid, List<UnifiedItem> Items)> grouped = new();

        // Task 5.3: cached (column index, ascending) sort spec, read from
        // ImGui.TableGetSortSpecs() inside DrawItemTable (only valid after TableHeadersRow()) and
        // applied inside RecomputeView's memoized pass — never an unconditional per-frame OrderBy.
        // Column indices below must match DrawItemTable's TableSetupColumn call order.
        private const int SelectColumn = 0;
        private const int ItemColumn = 1;
        private const int RetainerQtyColumn = 2;
        private const int InBagsColumn = 3;
        private const int IlvlColumn = 4;
        private const int CategoryColumn = 5;
        private const int VendorColumn = 6;
        private const int HolderColumn = 7;
        private const int RequestedQtyColumn = 8;
        private (int ColumnIndex, bool Ascending) sortSpec = (ItemColumn, true);

        // (ItemId, Hq) -> requested qty. Presence in the dictionary means the row is selected.
        private readonly Dictionary<(uint ItemId, bool Hq), uint> selection = new();

        // Task 5.4: cached Browse summary (selected count / worst-case pull / bag-slots-needed /
        // result count), recomputed only when summaryDirty is set — never per frame.
        private BrowseSummary browseSummary;
        private bool summaryDirty = true;

        // Prebuilt summary line + its overflow flag, rebuilt only in RecomputeSummary (summaryDirty).
        private string summaryLine = string.Empty;
        private bool summaryOverflow;

        // Task 10.3: presets bar state. newPresetName is the InputText buffer (kept as a field, not
        // allocated per frame). presetActionMessage is the inline feedback line (empty-name/no-selection
        // hints, "Applied N (M skipped)", "Saved preset ..."). pendingOverwrite*/pendingDeleteIndex +
        // trigger* track a one-shot confirm-modal request: the trigger bools are consumed (set back to
        // false) the same frame ImGui.OpenPopup is called, so the popup is opened exactly once per
        // click rather than every frame while pending (which would fight the modal's own Cancel/Confirm
        // CloseCurrentPopup()).
        private string newPresetName = string.Empty;
        private string? presetActionMessage;
        private int pendingOverwriteIndex = -1;
        private string pendingOverwriteName = string.Empty;
        private bool triggerOverwriteConfirm;
        private int pendingDeleteIndex = -1;
        private bool triggerDeleteConfirm;

        private struct BrowseSummary
        {
            public int SelectedCount;
            public uint WorstCasePull;
            public int BagSlotsNeeded;
            public int FreeBagSlots;
            public int TotalItemCount;
            public int ShownCount;

            // Task 8.3: sum over the current selection of VendorPrice * requestedQty.
            public ulong SelectionGilTotal;
        }

        // Browse (table) -> Running (live automation) -> Results (per-retainer summary), then Back
        // returns to Browse. Retrieve is submitted directly from Browse (no Preview step).
        private enum ViewMode { Browse, Running, Results }

        // Browse content layouts. Table (sortable columns) and Grouped (per-retainer) are the
        // originals; Grid is the in-game-inventory-style icon grid. Persisted via cfg.BrowseLayout.
        private enum BrowseLayout { Table, Grouped, Grid }

        private ViewMode viewMode = ViewMode.Browse;

        // Set false whenever we transition into ViewMode.Results so DrawResultsPanel() refreshes
        // UnifiedInventory.Build() exactly once on entry, never every frame.
        private bool resultsRefreshed;

        // Cached on Results entry (with resultsRefreshed) so the Retry button visibility check isn't
        // recomputed via nested LINQ every frame the panel is shown.
        private bool resultsHaveRetryable;

        public MainWindow(Configuration configuration) : base("RetainerReach###RetainerReachMain")
        {
            cfg = configuration;

            // The content region (grid/table/results) owns its own fill-height scroll; the window
            // itself should never grow a second scrollbar — the layout reserves footer space so the
            // grid resizes to fit instead.
            Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(380, 240),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
            };

            // Task 7.3: restore the last saved window size on first open. Plugin.cs constructs this
            // window AFTER Configuration is loaded/Initialize()d, so cfg.WindowSize already reflects
            // any prior session. Default (0,0) means "never saved" — leave Dalamud's own default
            // sizing alone in that case rather than applying a zero-size Size override.
            if (cfg.WindowSize != default)
            {
                Size = cfg.WindowSize;
                SizeCondition = ImGuiCond.FirstUseEver;
            }

            // Task 7.2: restore persisted filter/sort prefs. Same "cfg already loaded" guarantee as
            // above makes this safe in the ctor (no loadedPrefs flag needed). viewDirty is already
            // true via its field initializer, but set explicitly here too so the restored
            // filters/sort are guaranteed to take effect on first render even if that default ever
            // changes.
            // Category filter is now the ItemSearchCategory taxonomy (icon chips) and is transient per
            // session — the legacy SavedCategoryFilter (ItemUICategory ids) is intentionally not loaded.
            materialOnly = cfg.SavedMaterialOnly;
            sortSpec = (cfg.SavedSortColumn, cfg.SavedSortAscending);

            // Task 9.1: restore the persisted Browse layout toggle the same way. (The Task 9.3
            // per-retainer filter is intentionally NOT persisted — like filterText, it's transient
            // per session; only GroupByRetainer was added to Configuration.cs.)
            // Migrate the legacy GroupByRetainer bool to the BrowseLayout selector once (sentinel -1),
            // then keep groupByRetainer in sync so RecomputeView's grouping gate stays correct.
            if (cfg.BrowseLayout == -1)
            {
                browseLayout = cfg.GroupByRetainer ? BrowseLayout.Grouped : BrowseLayout.Table;
                cfg.BrowseLayout = (int)browseLayout;
                cfg.Save();
            }
            else
            {
                browseLayout = cfg.BrowseLayout is >= 0 and <= 2 ? (BrowseLayout)cfg.BrowseLayout : BrowseLayout.Table;
            }

            groupByRetainer = browseLayout == BrowseLayout.Grouped;
            fitColumns = cfg.FitColumnsToContent;
            viewDirty = true;
        }

        public override void Draw()
        {
            if (!hasLoaded)
            {
                RefreshItems();
            }

            // Task 5.3: a header-click sort change can only be discovered inside DrawItemTable
            // (ImGui.TableGetSortSpecs() is valid only after TableHeadersRow()), and it feeds the same
            // memoized RecomputeView() pass as the filter pipeline. DrawBrowsePanel recomputes at its
            // top for filter/refresh-driven dirtiness (so DrawSelectionBar never reads a stale
            // viewCache), and DrawItemTable re-checks viewDirty after the sort-spec read for a header
            // click that flips it mid-frame.
            SyncViewModeWithScheduler();

            DrawHeader();

            if (!AllaganToolsIpc.IsInitialized())
                DrawNotReadyBanner();

            switch (viewMode)
            {
                case ViewMode.Running:
                    DrawRunningPanel();
                    break;
                case ViewMode.Results:
                    DrawResultsPanel();
                    break;
                default:
                    DrawBrowsePanel();
                    break;
            }

            DrawDevSection();

            PersistWindowSizeIfChanged();
        }

        /// <summary>
        /// Task 7.3: captures the window's current size and persists it to
        /// <see cref="Configuration.WindowSize"/> only when it actually changed beyond a ~1px
        /// epsilon — never an unconditional per-frame <see cref="Configuration.Save"/> (that would
        /// be disk IO every single frame). Called once at the end of <see cref="Draw"/>, while the
        /// window is still open (Dalamud's <c>WindowSystem</c> calls <c>Draw()</c> between its own
        /// <c>Begin</c>/<c>End</c>, so <c>ImGui.GetWindowSize()</c> is valid here).
        /// </summary>
        private void PersistWindowSizeIfChanged()
        {
            var current = ImGui.GetWindowSize();

            var changedVsSaved = MathF.Abs(current.X - cfg.WindowSize.X) > 1f
                              || MathF.Abs(current.Y - cfg.WindowSize.Y) > 1f;
            // Only persist once the size has SETTLED (matches last frame) — otherwise an active
            // resize-drag changes the size every frame and would Save() the whole config to disk every
            // frame for the duration of the drag. Debounce to drag-end.
            var settled = MathF.Abs(current.X - lastSeenWindowSize.X) <= 0.5f
                       && MathF.Abs(current.Y - lastSeenWindowSize.Y) <= 0.5f;
            lastSeenWindowSize = current;

            if (changedVsSaved && settled)
            {
                cfg.WindowSize = current;
                cfg.Save();
            }
        }

        private Vector2 lastSeenWindowSize;

        /// <summary>
        /// Moves out of <see cref="ViewMode.Running"/> the instant the scheduler stops running. On a
        /// fully-clean finish (Done, nothing failed/short) it jumps straight back to Browse — fewer
        /// clicks to start the next retrieve — refreshing the inventory and clearing the selection so
        /// the grid reflects what was pulled. If anything failed/short, or the run errored/aborted, it
        /// shows <see cref="ViewMode.Results"/> instead so the user can review and Retry.
        /// </summary>
        private void SyncViewModeWithScheduler()
        {
            if (viewMode != ViewMode.Running || RetrieveScheduler.IsRunning)
                return;

            if (RetrieveScheduler.CurrentState == RetrieveScheduler.State.Done && !ResultsHaveRetryable())
            {
                RefreshItems();
                selection.Clear();
                MarkSelectionDirty();
                viewMode = ViewMode.Browse;
            }
            else
            {
                viewMode = ViewMode.Results;
                resultsRefreshed = false;
            }
        }

        public void Dispose()
        {
        }

        /// <summary>
        /// Vertical space (px) a fill-height scroll region / table must leave below itself for the
        /// footer rows it sits above, so the region tracks the window on resize instead of overlapping
        /// them. <paramref name="rows"/> is the panel-specific footer row count (summary + action
        /// buttons); the shared <see cref="DrawDevSection"/> drawn at the bottom of EVERY view (only
        /// when <see cref="Configuration.DevLog"/> is on) is added here once so every panel reserves
        /// for it — otherwise the dev section overflows below the window and forces a second scrollbar.
        /// </summary>
        private float FooterReserve(int rows)
            => ImGui.GetFrameHeightWithSpacing() * (rows + (cfg.DevLog ? 4 : 0));

        /// <summary>
        /// Exact height of the Browse footer below the item view — the <c>Spacing()</c> + the one-line
        /// summary + the Retrieve button (+ the Dev section when enabled) — so the layout's fill-height
        /// scroll region reaches right down to the footer with no dead margin under the button (and,
        /// under the window's NoScrollbar flag, no clip). Measured from real style metrics, not an
        /// approximate row count, because a stray ~30px gap there was visible.
        /// </summary>
        private float BrowseFooterHeight()
        {
            var h = ImGui.GetStyle().ItemSpacing.Y
                  + ImGui.GetTextLineHeightWithSpacing()
                  + ImGui.GetFrameHeightWithSpacing();
            if (cfg.DevLog)
                h += ImGui.GetFrameHeightWithSpacing() * 4f;
            return h;
        }

        private void DrawHeader()
        {
            using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Accent))
            {
                Icon(FontAwesomeIcon.Box);
                ImGui.SameLine();
                ImGui.TextUnformatted("RetainerReach");
            }
            ImGui.SameLine();
            // Live at-a-glance context — prebuilt in RefreshItems, no per-frame interpolation.
            ImGui.TextDisabled(headerStats);
            ImGui.Separator();
        }

        private void DrawNotReadyBanner()
        {
            using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Warn))
            {
                ImGui.TextWrapped("AllaganTools not ready — install/enable AllaganTools and let it finish scanning before this view is accurate.");
            }
            ImGui.Separator();
        }

        private void DrawBrowsePanel()
        {
            // Resolve filter/refresh-driven dirtiness BEFORE any panel body reads viewCache
            // (DrawSelectionBar iterates it). DrawItemTable re-checks viewDirty after the sort-spec read.
            if (viewDirty)
                RecomputeView();

            DrawToolbar();
            ImGui.Spacing();

            // Presets are a secondary "shopping list" feature — shown only while toggled on via the
            // compact toolbar button, so search + the grid stay front-and-center.
            if (showPresets)
                DrawPresetsBar();

            DrawSelectionBar();
            DrawCategoryChips();
            ImGui.Spacing();

            if (viewCache.Count == 0)
            {
                DrawEmptyBrowseMessage();
            }
            else
            {
                // Both branches read the same memoized viewCache/grouped fields — never recomputed per frame.
                switch (browseLayout)
                {
                    case BrowseLayout.Grouped:
                        DrawGroupedView();
                        break;
                    case BrowseLayout.Grid:
                        DrawItemGrid();
                        break;
                    default:
                        DrawItemTable();
                        break;
                }
            }

            ImGui.Spacing();
            DrawSummary();
            DrawRetrieveButton();
        }

        /// <summary>Empty-state line for Browse when the filtered set is empty, tailored to why.</summary>
        private void DrawEmptyBrowseMessage()
        {
            ImGui.Spacing();
            if (!string.IsNullOrWhiteSpace(filterText))
                ImGui.TextDisabled($"No items match \"{filterText}\".");
            else if (materialOnly || selectedCategories.Count > 0 || selectedRetainers.Count > 0)
                ImGui.TextDisabled("No items match the active filters (see Options).");
            else if (!AllaganToolsIpc.IsInitialized())
                ImGui.TextDisabled("Waiting for AllaganTools to finish scanning…");
            else
                ImGui.TextDisabled("No items on your retainers. Try Refresh.");
        }

        /// <summary>
        /// Search-first toolbar: a wide name-search box (the primary workflow) plus compact icon
        /// buttons for Refresh and Settings, and an Options popup that holds the secondary controls
        /// (layout, category/retainer filters, materials-only). An "active filters" hint appears next
        /// to Options so those tucked-away filters stay discoverable.
        /// </summary>
        private void DrawToolbar()
        {
            // Reserve the exact width of the trailing controls (two icon buttons + Options + Presets +
            // the gaps) so the search box fills the rest without ever clipping a button off the right
            // edge on a narrower window. A small margin keeps icon-button width estimates safe.
            var style = ImGui.GetStyle();
            var trailing = ImGui.GetFrameHeight() * 2f
                + ImGui.CalcTextSize("Options").X + style.FramePadding.X * 2f
                + ImGui.CalcTextSize("Presets").X + style.FramePadding.X * 2f
                + style.ItemSpacing.X * 4f
                + 12f;
            var searchWidth = MathF.Max(120f, ImGui.GetContentRegionAvail().X - trailing);
            ImGui.SetNextItemWidth(searchWidth);
            if (ImGui.InputTextWithHint("###FilterText", "Search items by name...", ref filterText, 128))
                viewDirty = true;

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Sync))
                RefreshItems();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Refresh inventory");

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
                OpenSettings?.Invoke();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Settings");

            ImGui.SameLine();
            if (ImGui.Button("Options"))
                ImGui.OpenPopup("##browseoptions");

            ImGui.SameLine();
            if (ImGui.Button("Presets"))
                showPresets = !showPresets;

            var filtersActive = materialOnly || selectedCategories.Count > 0 || selectedRetainers.Count > 0;
            if (filtersActive)
            {
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Accent))
                    ImGui.TextUnformatted("• filters on");
            }

            DrawOptionsPopup();
        }

        /// <summary>The secondary Browse controls, behind the toolbar's Options button: layout, then filters.</summary>
        private void DrawOptionsPopup()
        {
            using var opts = ImRaii.Popup("##browseoptions");
            if (!opts)
                return;

            // Layout selector (Table / Grouped / Grid). Grouping is derived inside the memoized
            // RecomputeView pass, never per frame — switching only flips which draw method renders.
            ImGui.TextDisabled("Layout");
            DrawLayoutRadio("Table", BrowseLayout.Table);
            DrawLayoutRadio("Grouped by retainer", BrowseLayout.Grouped);
            DrawLayoutRadio("Grid (icons)", BrowseLayout.Grid);

            // Column sizing only affects the Table layout — hidden otherwise (no dead controls).
            if (browseLayout == BrowseLayout.Table && ImGui.Checkbox("Fit columns to content", ref fitColumns))
            {
                cfg.FitColumnsToContent = fitColumns;
                cfg.Save();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextDisabled("Filters");

            if (ImGui.Checkbox("Crafting materials only", ref materialOnly))
            {
                viewDirty = true;
                cfg.SavedMaterialOnly = materialOnly;
                cfg.Save();
            }

            DrawCategoryFilter();
            DrawRetainerFilter();
        }

        /// <summary>One layout radio row; on selection routes through <see cref="SetBrowseLayout"/> (debounced Save).</summary>
        private void DrawLayoutRadio(string label, BrowseLayout layout)
        {
            if (ImGui.RadioButton(label, browseLayout == layout))
                SetBrowseLayout(layout);
        }

        /// <summary>
        /// Switches the Browse layout, keeping <see cref="groupByRetainer"/> in sync (so
        /// <see cref="RecomputeView"/>'s grouping gate stays correct) and persisting both the new
        /// <see cref="Configuration.BrowseLayout"/> and the legacy <see cref="Configuration.GroupByRetainer"/>.
        /// No-op (no Save) when the layout is unchanged — matching the debounced write-back idiom.
        /// </summary>
        private void SetBrowseLayout(BrowseLayout layout)
        {
            if (browseLayout == layout)
                return;

            browseLayout = layout;
            groupByRetainer = layout == BrowseLayout.Grouped;
            viewDirty = true;
            cfg.BrowseLayout = (int)layout;
            cfg.GroupByRetainer = groupByRetainer;
            cfg.Save();
        }

        /// <summary>
        /// Task 9.3: multi-select per-retainer filter — same ID-scoped checkbox-combo idiom as
        /// <see cref="DrawCategoryFilter"/>, populated from <see cref="retainerOptions"/> (computed
        /// once per <see cref="RefreshItems"/>). ORs within retainers (any selected retainer matches),
        /// ANDed with name/category/material inside <see cref="RecomputeView"/>. No selection means
        /// "all retainers". Not persisted (transient per session, like filterText).
        /// </summary>
        private void DrawRetainerFilter()
        {
            var preview = selectedRetainers.Count == 0
                ? "All"
                : $"{selectedRetainers.Count} selected";

            ImGui.SetNextItemWidth(180);
            using var combo = ImRaii.Combo("Retainers", preview);
            if (!combo)
                return;

            foreach (var (cid, name) in retainerOptions)
            {
                // Scope by CID: two retainers could in theory share a display name.
                using var retainerId = ImRaii.PushId(unchecked((int)cid));
                var isSelected = selectedRetainers.Contains(cid);
                if (ImGui.Checkbox(name, ref isSelected))
                {
                    if (isSelected)
                        selectedRetainers.Add(cid);
                    else
                        selectedRetainers.Remove(cid);
                    viewDirty = true;
                }
            }
        }

        /// <summary>
        /// Full multi-select category picker (the "+more" beyond the inline chips): checkbox rows over
        /// every present <see cref="ItemSearchCategories"/>, toggling <see cref="selectedCategories"/>.
        /// OR within categories; no selection = all. Alphabetical here (chips cover the busiest ones).
        /// </summary>
        private void DrawCategoryFilter()
        {
            var preview = selectedCategories.Count == 0 ? "All" : $"{selectedCategories.Count} selected";

            ImGui.SetNextItemWidth(180);
            using var combo = ImRaii.Combo("Categories", preview);
            if (!combo)
                return;

            foreach (var cat in categoryOptionsAlpha)
            {
                // Scope by id: two rows can share a localized name (name is both label and id).
                using var catId = ImRaii.PushId((int)cat.Id);
                var isSelected = selectedCategories.Contains(cat.Id);
                if (ImGui.Checkbox($"{cat.Name} ({cat.Count})", ref isSelected))
                {
                    ToggleCategory(cat.Id, isSelected);
                }
            }
        }

        /// <summary>Adds/removes a search-category from the filter and marks the view dirty (shared by chips + combo).</summary>
        private void ToggleCategory(ushort id, bool select)
        {
            if (select)
                selectedCategories.Add(id);
            else
                selectedCategories.Remove(id);
            viewDirty = true;
        }

        /// <summary>
        /// Inline quick-filter chips continuing the selection-bar row: the top
        /// <see cref="CategoryChipCount"/> present categories as bare clickable market-board icons
        /// (no button chrome — just the icon, with a hover/selected border). Toggles the same
        /// <see cref="selectedCategories"/> the Options combo uses. Manually wraps to the next line
        /// when it runs out of horizontal width.
        /// </summary>
        private void DrawCategoryChips()
        {
            if (categoryOptions.Count == 0)
                return;

            var size = ImGui.GetFrameHeight(); // square, aligns with the selection-bar buttons' height
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var rightEdge = ImGui.GetWindowContentRegionMax().X;
            var count = Math.Min(CategoryChipCount, categoryOptions.Count);

            // Continue the selection-bar row (small gap), then place chips, wrapping by width.
            ImGui.SameLine(0f, spacing * 2f);

            for (var i = 0; i < count; i++)
            {
                var cat = categoryOptions[i];
                if (!ItemIcon.TryGetGameIconHandle((ushort)cat.Icon, false, out var handle))
                    continue;

                DrawCategoryChip(cat, size, handle);

                // Keep the next chip on this line only if it still fits before the right edge.
                if (i + 1 < count && ImGui.GetItemRectMax().X + spacing + size < rightEdge)
                    ImGui.SameLine();
            }

            if (selectedCategories.Count > 0)
            {
                ImGui.SameLine();
                // Full-height Button (not SmallButton) so it lines up with the chips/selection buttons.
                if (ImGui.Button("Clear categories"))
                {
                    selectedCategories.Clear();
                    viewDirty = true;
                }
            }
        }

        private void DrawCategoryChip((ushort Id, string Name, uint Icon, int Count) cat, float size, ImTextureID handle)
        {
            using var chipId = ImRaii.PushId((int)cat.Id);
            var isSelected = selectedCategories.Contains(cat.Id);

            ImGui.InvisibleButton("##chip", new Vector2(size, size));
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var hovered = ImGui.IsItemHovered();

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                ToggleCategory(cat.Id, !isSelected);
                isSelected = !isSelected;
            }

            var dl = ImGui.GetWindowDrawList();
            dl.AddImage(handle, min, max);

            if (isSelected)
                dl.AddRect(min, max, ImGui.GetColorU32(HubStyle.Accent), 2f, ImDrawFlags.None, 2f);
            else if (hovered)
                dl.AddRect(min, max, ImGui.GetColorU32(CellHover), 2f, ImDrawFlags.None, 1.5f);

            if (hovered)
                ImGui.SetTooltip($"{cat.Name} ({cat.Count})");
        }

        private void DrawSelectionBar()
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.CheckSquare, "Select all"))
            {
                foreach (var item in viewCache)
                    selection[(item.ItemId, item.Hq)] = item.TotalRetainerQty;
                MarkSelectionDirty();
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Square, "Clear"))
            {
                selection.Clear();
                MarkSelectionDirty();
            }

            ImGui.SameLine();
            // Task 4.7: selects exactly the shown (viewCache) items that are recipe ingredients, at
            // their full retainer quantity — rides on F5's material set (Q-M3).
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Hammer, "Select all materials"))
            {
                foreach (var item in viewCache)
                {
                    if (CraftingMaterials.IsMaterial(item.ItemId))
                        selection[(item.ItemId, item.Hq)] = item.TotalRetainerQty;
                }
                MarkSelectionDirty();
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"({selection.Count} selected)");
        }

        /// <summary>
        /// Task 10.3: saved-preset ("shopping list") bar — an inline name entry + "Save preset" button
        /// that snapshots the current <see cref="selection"/>, and a list of <see cref="Configuration.Presets"/>
        /// each with Apply / Apply + Retrieve / Delete. All of this is user-action-triggered (button
        /// clicks), never per-frame work; <see cref="newPresetName"/> is kept as a field so the
        /// <c>InputText</c> buffer isn't reallocated every frame.
        /// </summary>
        private void DrawPresetsBar()
        {
            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##presetname", "Preset name...", ref newPresetName, 64);

            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save preset"))
                RequestSavePreset();

            if (triggerOverwriteConfirm)
            {
                ImGui.OpenPopup("Overwrite preset?");
                triggerOverwriteConfirm = false;
            }
            DrawOverwriteConfirmPopup();

            if (presetActionMessage != null)
                ImGui.TextDisabled(presetActionMessage);

            if (cfg.Presets.Count == 0)
            {
                ImGui.TextDisabled("(no saved presets)");
                return;
            }

            for (var i = 0; i < cfg.Presets.Count; i++)
            {
                var preset = cfg.Presets[i];

                // Scoped so the three buttons below get unique widget IDs across rows despite
                // sharing the same visible label text.
                using var presetId = ImRaii.PushId(i);

                ImGui.TextUnformatted($"{preset.Name} ({preset.Entries.Count} items)");

                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderOpen, "Apply"))
                    ApplyPreset(preset);

                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Truck, "Apply + Retrieve"))
                    ApplyAndRetrievePreset(preset);

                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Trash, "Delete"))
                {
                    pendingDeleteIndex = i;
                    triggerDeleteConfirm = true;
                }
            }

            // OpenPopup/BeginPopupModal below run OUTSIDE any per-row PushId scope (the loop above
            // already ended), at the same ID-stack location every frame — required so the popup
            // opened from row i's Delete button is found by the single BeginPopupModal call here.
            if (triggerDeleteConfirm)
            {
                ImGui.OpenPopup("Delete preset?");
                triggerDeleteConfirm = false;
            }
            DrawDeleteConfirmPopup();
        }

        private void DrawOverwriteConfirmPopup()
        {
            if (!ImGui.BeginPopupModal("Overwrite preset?"))
                return;

            ImGui.TextUnformatted($"A preset named \"{pendingOverwriteName}\" already exists. Overwrite it?");

            if (ImGui.Button("Overwrite", new Vector2(120, 0)))
            {
                if (pendingOverwriteIndex >= 0 && pendingOverwriteIndex < cfg.Presets.Count)
                {
                    cfg.Presets[pendingOverwriteIndex] = BuildPresetFromSelection(pendingOverwriteName);
                    cfg.Save();
                    presetActionMessage = $"Saved preset \"{pendingOverwriteName}\".";
                    newPresetName = string.Empty;
                }

                pendingOverwriteIndex = -1;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                pendingOverwriteIndex = -1;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        private void DrawDeleteConfirmPopup()
        {
            if (!ImGui.BeginPopupModal("Delete preset?"))
                return;

            if (pendingDeleteIndex >= 0 && pendingDeleteIndex < cfg.Presets.Count)
                ImGui.TextUnformatted($"Delete preset \"{cfg.Presets[pendingDeleteIndex].Name}\"?");

            if (ImGui.Button("Delete", new Vector2(120, 0)))
            {
                if (pendingDeleteIndex >= 0 && pendingDeleteIndex < cfg.Presets.Count)
                {
                    cfg.Presets.RemoveAt(pendingDeleteIndex);
                    cfg.Save();
                }

                pendingDeleteIndex = -1;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                pendingDeleteIndex = -1;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        /// <summary>
        /// Task 10.3: validates the name/selection, then either adds a new
        /// <see cref="Configuration.Presets"/> entry (and persists immediately) or — when a preset with
        /// the same name (case-insensitive) already exists — defers to the overwrite confirm popup
        /// (<see cref="DrawOverwriteConfirmPopup"/>) instead of silently clobbering it.
        /// </summary>
        private void RequestSavePreset()
        {
            var name = newPresetName.Trim();
            if (name.Length == 0)
            {
                presetActionMessage = "Enter a preset name first.";
                return;
            }

            if (selection.Count == 0)
            {
                presetActionMessage = "Select at least one item before saving a preset.";
                return;
            }

            var existingIndex = cfg.Presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                pendingOverwriteIndex = existingIndex;
                pendingOverwriteName = name;
                triggerOverwriteConfirm = true;
                return;
            }

            cfg.Presets.Add(BuildPresetFromSelection(name));
            cfg.Save();
            presetActionMessage = $"Saved preset \"{name}\".";
            newPresetName = string.Empty;
        }

        private RetrievePreset BuildPresetFromSelection(string name)
        {
            return new RetrievePreset
            {
                Name = name,
                Entries = selection
                    .Select(kv => new PresetEntry { ItemId = kv.Key.ItemId, Hq = kv.Key.Hq, Qty = kv.Value })
                    .ToList(),
            };
        }

        /// <summary>
        /// Task 10.3: clears <see cref="selection"/> then loads <paramref name="preset"/>'s entries,
        /// resolved against the live <see cref="cachedItems"/> (NOT the data at save time) — clamping
        /// each requested qty to the item's current <see cref="UnifiedItem.TotalRetainerQty"/> and
        /// skipping entries for items no longer held (or now at 0 total qty) entirely. Always calls
        /// <see cref="MarkSelectionDirty"/> so the Task 5.4 summary reflects the applied selection, and
        /// surfaces the applied/skipped counts via <see cref="presetActionMessage"/>. Never auto-runs a
        /// retrieve — that's <see cref="ApplyAndRetrievePreset"/>'s job (Task 10.4).
        /// </summary>
        private void ApplyPreset(RetrievePreset preset)
        {
            selection.Clear();

            var itemsByKey = cachedItems.ToDictionary(i => (i.ItemId, i.Hq));
            var applied = 0;
            var skipped = 0;

            foreach (var entry in preset.Entries)
            {
                if (itemsByKey.TryGetValue((entry.ItemId, entry.Hq), out var liveItem) && liveItem.TotalRetainerQty > 0)
                {
                    selection[(entry.ItemId, entry.Hq)] = Math.Clamp(entry.Qty, 1u, liveItem.TotalRetainerQty);
                    applied++;
                }
                else
                {
                    skipped++;
                }
            }

            MarkSelectionDirty();

            presetActionMessage = skipped > 0
                ? $"Applied {applied} items ({skipped} skipped — no longer held)."
                : $"Applied {applied} items.";
        }

        /// <summary>
        /// Task 10.4: applies <paramref name="preset"/> (see <see cref="ApplyPreset"/>) then immediately
        /// plans + submits the resulting selection through the SAME gated path every other retrieve entry
        /// point uses (<see cref="RetrievePlanner.Plan"/> + <see cref="RetrieveLauncher.TrySubmit"/>) —
        /// disclaimer/bell/already-running gates all apply identically, no bypass.
        /// </summary>
        private void ApplyAndRetrievePreset(RetrievePreset preset)
        {
            ApplyPreset(preset);

            if (selection.Count == 0)
            {
                presetActionMessage = "Nothing to retrieve — none of this preset's items are currently held.";
                return;
            }

            var batch = RetrievePlanner.Plan(selection, cachedItems);
            if (!RetrieveLauncher.TrySubmit(batch, cfg, out var reason))
            {
                Plugin.ChatGui.PrintError($"[RetainerReach] Cannot retrieve: {reason}");
                return;
            }

            viewMode = ViewMode.Running;
        }

        private void DrawItemTable()
        {
            const ImGuiTableFlags baseFlags =
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.Sortable;

            // Fit-to-content mode sizes each column to its widest cell (SizingFixedFit) and adds a
            // horizontal scrollbar so wide content (long item names) isn't clipped; stretch mode fills
            // the window proportionally. Distinct table IDs per mode so each remembers its own column
            // widths and the fit mode auto-fits to content the first time it's shown.
            var flags = baseFlags | (fitColumns
                ? ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollX
                : ImGuiTableFlags.SizingStretchProp);
            var tableId = fitColumns ? "###RetainerReachItemsFit" : "###RetainerReachItems";

            // Fill-height table. footer reserves the Browse summary rows (selection/worst-case/bag line,
            // gil-total line, and "N items, M shown") plus the preview button row (and the dev section
            // when shown — see FooterReserve), so the table doesn't grow over them. Clamped to a small
            // positive floor so MinimumSize.Y (240) can never collapse it to <=0.
            var tableHeight = MathF.Max(64f, ImGui.GetContentRegionAvail().Y - BrowseFooterHeight());
            using var table = ImRaii.Table(tableId, 9, flags, new Vector2(-1, tableHeight));
            if (!table)
                return;

            SetupItemTableColumns();
            ImGui.TableHeadersRow();

            // Task 5.3: ImGui.TableGetSortSpecs() is only valid AFTER TableHeadersRow(), inside the
            // open table — this is why RecomputeView() (Task 4.5) now runs from here instead of the
            // top of Draw(). A header-click sort change sets viewDirty just like a filter change, so
            // it's applied inside the same memoized pass, never an unconditional per-frame OrderBy.
            var specs = ImGui.TableGetSortSpecs();
            if (!specs.IsNull && specs.SpecsDirty)
            {
                var newSort = specs.SpecsCount > 0
                    ? ((int)specs.Specs[0].ColumnIndex, specs.Specs[0].SortDirection != ImGuiSortDirection.Descending)
                    : sortSpec;

                if (newSort != sortSpec)
                {
                    sortSpec = newSort;
                    viewDirty = true;
                    // Task 7.2: write-back debounced by the enclosing "sort actually changed" guard
                    // — never a per-frame Save().
                    cfg.SavedSortColumn = sortSpec.ColumnIndex;
                    cfg.SavedSortAscending = sortSpec.Ascending;
                    cfg.Save();
                }

                specs.SpecsDirty = false;
            }

            if (viewDirty)
                RecomputeView();

            // Clip to visible rows (auto-measured height) so a large inventory doesn't run every row's
            // per-frame widget logic — parity with the grid's clipper. DrawItemRow issues TableNextRow.
            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(viewCache.Count, -1f);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawItemRow(viewCache[i]);
            }
            clipper.End();
            clipper.Destroy();
        }

        /// <summary>
        /// Task 5.2/8.2/9.2: shared column declaration for the Browse item table, used by both the
        /// flat <see cref="DrawItemTable"/> and each grouped-view retainer's mini-table
        /// (<see cref="DrawGroupItemsTable"/>) so the two never drift out of sync with the
        /// *Column constants / <see cref="ApplySort"/>. Select/Holder(s)/Requested qty carry no sort
        /// key, so they're NoSort; Item/Retainer qty/In bags/ilvl/Category/Vendor are left sortable
        /// (the default) — only the flat table actually reads sort specs (Task 5.3); the per-group
        /// mini-tables render already-sorted data with Sortable omitted from their flags.
        /// </summary>
        private void SetupItemTableColumns()
        {
            ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 50);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Retainer qty", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("In bags", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("ilvl", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Vendor", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Holder(s)", ImGuiTableColumnFlags.NoSort);
            ImGui.TableSetupColumn("Requested qty", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 300);

            // Task 7.2/8.2: column-visibility prefs. TableSetColumnEnabled toggles a column's
            // visibility WITHOUT changing its declared column index (unlike simply omitting its
            // TableSetupColumn call, which would shift every later column's index) — so the
            // *Column constants above and ApplySort's sort-key switch stay valid no matter which
            // of these are shown. This is the "simpler" of the two approaches Task 7.2 offers.
            ImGui.TableSetColumnEnabled(IlvlColumn, cfg.ShowIlvlColumn);
            ImGui.TableSetColumnEnabled(CategoryColumn, cfg.ShowCategoryColumn);
            ImGui.TableSetColumnEnabled(VendorColumn, cfg.ShowVendorColumn);
        }

        /// <summary>
        /// Task 9.2: grouped-by-retainer Browse layout — one <see cref="ImGui.CollapsingHeader"/> per
        /// entry in the memoized <see cref="grouped"/> list (built inside <see cref="RecomputeView"/>,
        /// never per frame), with a "Retrieve all from &lt;retainer&gt;" button beside each header
        /// (Task 9.4) and that retainer's matching items rendered beneath via the same per-row
        /// rendering the flat table uses (<see cref="DrawItemRow"/>). Selection stays item-level
        /// (keyed by (ItemId, Hq)) — grouping here is purely a view lens: checking an item's box under
        /// retainer A also shows it checked under retainer B if that item is held by both.
        /// </summary>
        private void DrawGroupedView()
        {
            // Same exact footer height as DrawItemTable/DrawItemGrid so the scroll region fills down to
            // the summary/Retrieve footer without dead margin below it.
            var height = MathF.Max(64f, ImGui.GetContentRegionAvail().Y - BrowseFooterHeight());
            using var scroll = ImRaii.Child("###RetainerReachGroupedScroll", new Vector2(-1, height));
            if (!scroll)
                return;

            foreach (var (retainerName, cid, items) in grouped)
            {
                // Scope by CID so the CollapsingHeader, retrieve button, and every row/control drawn
                // beneath it get a unique ID path per retainer group — the same item can legitimately
                // appear under two different groups (held by two retainers) and must not collide.
                using var groupId = ImRaii.PushId(unchecked((int)cid));

                var opened = ImGui.CollapsingHeader($"{retainerName} ({items.Count})###hdr", ImGuiTreeNodeFlags.DefaultOpen);

                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Truck, $"Retrieve all from {retainerName}"))
                    RetrieveAllFromRetainer(cid, retainerName, items);

                if (opened)
                    DrawGroupItemsTable(items);
            }
        }

        /// <summary>
        /// Task 9.2: per-group mini item table — same 9-column layout as <see cref="DrawItemTable"/>
        /// (<see cref="SetupItemTableColumns"/>) and the same <see cref="DrawItemRow"/> per-row
        /// rendering, so qty controls/checkbox/context menu behave identically inside a group. No
        /// <see cref="ImGuiTableFlags.Sortable"/>/ScrollY here: <paramref name="items"/> is already the
        /// memoized, sorted <see cref="viewCache"/> subset for this retainer (Task 9.2), and nesting a
        /// second independently-sortable/scrolling table per group would need its own sort-spec
        /// handling (Task 5.3 only wires that up for the one flat table).
        /// </summary>
        private void DrawGroupItemsTable(List<UnifiedItem> items)
        {
            const ImGuiTableFlags flags =
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingStretchProp;

            using var table = ImRaii.Table("###RetainerReachGroupItems", 9, flags);
            if (!table)
                return;

            SetupItemTableColumns();
            ImGui.TableHeadersRow();

            foreach (var item in items)
                DrawItemRow(item);
        }

        /// <summary>
        /// Task 9.4: "Retrieve all from &lt;retainer&gt;" — builds a <see cref="RetrieveBatch"/>
        /// directly, one <see cref="RetrieveTarget"/> per currently-shown item this retainer holds,
        /// WITHOUT going through <see cref="RetrievePlanner.Plan"/>. Plan() is a qty-distribution
        /// planner: it walks an item's <see cref="RetainerHolding"/> list in insertion order (the
        /// order retainers were enumerated in <see cref="UnifiedInventory.Build"/>, not sorted by this
        /// group's CID) and spills the requested qty across holdings until covered. For a
        /// retainer-scoped bulk retrieve that is unsafe: if an item is held by multiple retainers and
        /// this group's retainer isn't first in that item's Holdings list, Plan() would consume another
        /// retainer's stock first and misattribute the pull to the WRONG retainer. Building the batch
        /// directly and forcing RetainerCid/RetainerName to THIS group's retainer for every target
        /// guarantees the pull always targets the intended retainer — this is exactly what
        /// RetrieveBatch.ByRetainer() (the scheduler's per-retainer grouping) relies on downstream.
        /// Still submits via the one gated path (<see cref="RetrieveLauncher.TrySubmit"/>) — same
        /// disclaimer/bell/already-running gates as every other retrieve entry point, no bypass.
        /// </summary>
        private void RetrieveAllFromRetainer(ulong cid, string retainerName, List<UnifiedItem> items)
        {
            var batch = new RetrieveBatch();

            foreach (var item in items)
            {
                var holding = item.Holdings.FirstOrDefault(h => h.RetainerCid == cid);
                if (holding == null || holding.Qty == 0)
                    continue;

                var qty = Math.Min(holding.Qty, item.TotalRetainerQty);
                batch.Targets.Add(new RetrieveTarget
                {
                    ItemId = item.ItemId,
                    Hq = item.Hq,
                    RetainerName = retainerName,
                    RetainerCid = cid,
                    Qty = qty,
                    // Whole-stack-only command: this holding's entire stack is touched regardless of
                    // how much of it is "requested" — same worst-case semantics as RetrievePlanner.Plan.
                    WorstCasePulled = qty,
                });
            }

            if (!RetrieveLauncher.TrySubmit(batch, cfg, out var reason))
            {
                Plugin.ChatGui.PrintError($"[RetainerReach] Cannot retrieve: {reason}");
                return;
            }

            viewMode = ViewMode.Running;
        }

        /// <summary>
        /// In-game-inventory-style layout: <see cref="viewCache"/> as a wrapping grid of square
        /// icon cells. Fill-height scroll (mirrors <see cref="DrawItemTable"/>/<see cref="DrawGroupedView"/>).
        /// Cells wrap to the window width and only the visible rows are drawn (row-based
        /// <c>ImGuiListClipper</c>) so a large multi-retainer inventory stays cheap — the equivalent of
        /// the free row clipping the ScrollY table gets. Shares <see cref="viewCache"/>/<see cref="selection"/>
        /// with the other layouts.
        /// </summary>
        private void DrawItemGrid()
        {
            var scrollHeight = MathF.Max(64f, ImGui.GetContentRegionAvail().Y - BrowseFooterHeight());
            using var scroll = ImRaii.Child("###RetainerReachGridScroll", new Vector2(-1, scrollHeight));
            if (!scroll)
                return;

            // Tight uniform gap between slots so the grid reads like the native inventory (not floating
            // icons). Both the horizontal SameLine gap and the row pitch derive from this one value.
            const float gap = 3f;
            using var slotSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap, gap));

            // Clamp at the draw site, mirroring ItemIcon's IconScale clamp (no config-setter clamp).
            var cell = ImGui.GetTextLineHeight() * Math.Clamp(cfg.GridCellScale, 2.0f, 6.0f);
            var perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap) / (cell + gap)));
            var rowCount = (viewCache.Count + perRow - 1) / perRow;
            var rowHeight = cell + gap;

            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(rowCount, rowHeight);
            while (clipper.Step())
            {
                for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                {
                    for (var col = 0; col < perRow; col++)
                    {
                        if (col > 0)
                            ImGui.SameLine();

                        var index = row * perRow + col;
                        if (index < viewCache.Count)
                            DrawGridCell(viewCache[index], cell);
                        else
                            DrawEmptySlot(cell); // pad the final row with empty native-style slots
                    }
                }
            }

            clipper.End();
            clipper.Destroy();
        }

        /// <summary>
        /// One grid cell: an <see cref="ImGui.InvisibleButton"/> for hit-testing, then the icon,
        /// stack quantity (corner), HQ glyph, and selection/hover highlight painted over its rect via
        /// the window draw list. Left-click toggles selection (same <see cref="selection"/> dict as the
        /// table checkbox), right-click opens the shared <see cref="DrawRowContextMenu"/>, hover shows
        /// <see cref="DrawGridCellTooltip"/>. Reads only pre-stamped <see cref="UnifiedItem"/> fields.
        /// </summary>
        private void DrawGridCell(UnifiedItem item, float cell)
        {
            var key = (item.ItemId, item.Hq);
            var isSelected = selection.ContainsKey(key);

            using var cellId = ImRaii.PushId(unchecked((int)item.ItemId * 2 + (item.Hq ? 1 : 0)));

            ImGui.InvisibleButton("##cell", new Vector2(cell, cell));
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var hovered = ImGui.IsItemHovered();

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                if (isSelected)
                    selection.Remove(key);
                else
                    selection[key] = item.TotalRetainerQty;
                MarkSelectionDirty();
                isSelected = !isSelected;
            }

            var dl = ImGui.GetWindowDrawList();

            // Native-style slot frame (dark rounded bg + border), then a selection tint over it, then
            // the icon inset 1px, then HQ/quantity overlays, then the selection/hover border on top.
            DrawSlotFrame(dl, min, max);

            if (isSelected)
                dl.AddRectFilled(min, max, ImGui.GetColorU32(CellSelected), SlotRounding, ImDrawFlags.None);

            if (ItemIcon.TryGetGameIconHandle(item.IconId, item.Hq, out var handle))
                dl.AddImage(handle, new Vector2(min.X + 1f, min.Y + 1f), new Vector2(max.X - 1f, max.Y - 1f));

            if (item.Hq)
                DrawCellText(dl, new Vector2(min.X + 2f, min.Y + 1f), SeIconChar.HighQuality.ToIconString());

            var qty = item.TotalRetainerQty.ToString();
            var qtySize = ImGui.CalcTextSize(qty);
            // Bottom-right, left-clamped so a long stack count can't overflow the cell's left edge.
            var qtyPos = new Vector2(MathF.Max(min.X + 2f, max.X - qtySize.X - 3f), max.Y - qtySize.Y - 2f);
            // Dark plate behind the number so it reads over any icon art.
            dl.AddRectFilled(qtyPos - new Vector2(2f, 1f), qtyPos + qtySize + new Vector2(2f, 1f), ImGui.GetColorU32(CellPlate), 2f, ImDrawFlags.None);
            DrawCellText(dl, qtyPos, qty);

            // Accent dot (top-right) when the item is split across more than one retainer.
            if (item.Holdings.Count > 1)
                dl.AddCircleFilled(new Vector2(max.X - 5f, min.Y + 5f), 2.5f, ImGui.GetColorU32(HubStyle.Accent));

            if (isSelected)
                dl.AddRect(min, max, ImGui.GetColorU32(HubStyle.Accent), SlotRounding, ImDrawFlags.None, 2f);
            else if (hovered)
                dl.AddRect(min, max, ImGui.GetColorU32(CellHover), SlotRounding, ImDrawFlags.None, 1.5f);

            DrawRowContextMenu(item, key, isSelected);

            if (hovered)
                DrawGridCellTooltip(item);
        }

        /// <summary>Cell-overlay text with a 1px dark shadow so it stays legible over any icon art.</summary>
        private static void DrawCellText(ImDrawListPtr dl, Vector2 pos, string text)
        {
            dl.AddText(pos + new Vector2(1f, 1f), ImGui.GetColorU32(CellShade), text);
            dl.AddText(pos, ImGui.GetColorU32(HubStyle.Text), text);
        }

        /// <summary>Draws the native-style slot background + border for a cell rect (shared by filled and empty slots).</summary>
        private static void DrawSlotFrame(ImDrawListPtr dl, Vector2 min, Vector2 max)
        {
            dl.AddRectFilled(min, max, ImGui.GetColorU32(SlotBg), SlotRounding, ImDrawFlags.None);
            dl.AddRect(min, max, ImGui.GetColorU32(SlotBorder), SlotRounding, ImDrawFlags.None, 1f);
        }

        /// <summary>An empty native-style slot occupying one grid cell of layout, used to pad the final row.</summary>
        private static void DrawEmptySlot(float cell)
        {
            ImGui.Dummy(new Vector2(cell, cell));
            DrawSlotFrame(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        }

        /// <summary>
        /// Rich hover tooltip for a grid cell — icon + name (+HQ), then ilvl / category / vendor
        /// (each gated by the same column-visibility toggle the table uses, so a hidden column stays
        /// hidden here too), on-retainers / in-bags totals, and a per-retainer holdings breakdown.
        /// Reads only pre-stamped <see cref="UnifiedItem"/> fields.
        /// </summary>
        private void DrawGridCellTooltip(UnifiedItem item)
        {
            using var tt = ImRaii.Tooltip();

            if (ItemIcon.Draw(item.IconId, item.Hq))
            {
                ImGui.SameLine();
                ItemIcon.AlignTextToIcon();
            }
            ImGui.TextUnformatted(item.Name + HqSuffix(item.Hq));

            if (cfg.ShowIlvlColumn)
                ImGui.TextDisabled($"Item level {item.Ilvl}");
            if (cfg.ShowCategoryColumn)
                ImGui.TextDisabled(item.CategoryName);
            if (cfg.ShowVendorColumn)
                ImGui.TextDisabled($"Vendor: {item.VendorPrice:N0} gil");

            ImGui.TextUnformatted($"On retainers: {item.TotalRetainerQty}   ·   In bags: {item.PlayerBagQty}");

            ImGui.Separator();
            if (item.Holdings.Count == 0)
            {
                ImGui.TextDisabled("-");
            }
            else
            {
                foreach (var holding in item.Holdings)
                    ImGui.TextUnformatted($"{holding.RetainerName}: {holding.Qty}");
            }
        }

        private void DrawItemRow(UnifiedItem item)
        {
            var key = (item.ItemId, item.Hq);
            var isSelected = selection.TryGetValue(key, out var requestedQty);

            // Task 3.2: stable per-row ID scope so every control below can use a fixed literal
            // label (no per-frame interpolated "###..." strings) without colliding across rows.
            using var rowId = ImRaii.PushId(unchecked((int)item.ItemId * 2 + (item.Hq ? 1 : 0)));

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(SelectColumn);
            if (ImGui.Checkbox("##sel", ref isSelected))
            {
                if (isSelected)
                    selection[key] = item.TotalRetainerQty;
                else
                    selection.Remove(key);
                MarkSelectionDirty();
            }

            ImGui.TableSetColumnIndex(ItemColumn);
            // Task 4.3: Browse routes through the cached IconId (stamped once at Build) instead of
            // the itemId-based overload, which would re-hit ItemSheet.ById per row per frame.
            if (ItemIcon.Draw(item.IconId, item.Hq))
            {
                ImGui.SameLine();
                ItemIcon.AlignTextToIcon();
            }

            // Draw first, then only measure-for-truncation on the one row actually hovered — avoids a
            // full name marshal+CalcTextSize on every visible row every frame.
            var availWidth = ImGui.GetContentRegionAvail().X;
            ImGui.TextUnformatted(item.Name);
            if (ImGui.IsItemHovered() && ImGui.CalcTextSize(item.Name).X > availWidth)
                ImGui.SetTooltip(item.Name);

            // Task 6.1: right-click context menu attached to the item-name cell (the widget just
            // drawn above). Scoped under this row's PushId (top of the method), so "##rowctx" is a
            // unique popup id per row despite being a fixed literal.
            DrawRowContextMenu(item, key, isSelected);

            if (item.Hq)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(HqSuffix(item.Hq));
            }

            ImGui.TableSetColumnIndex(RetainerQtyColumn);
            ImGui.TextUnformatted(item.TotalRetainerQty.ToString());

            ImGui.TableSetColumnIndex(InBagsColumn);
            ImGui.TextUnformatted(item.PlayerBagQty.ToString());

            // Task 5.2/7.2: ilvl + Category columns, skipped when hidden via cfg.ShowIlvlColumn /
            // cfg.ShowCategoryColumn (the column itself is still declared — see the
            // TableSetColumnEnabled calls in DrawItemTable — only the cell render is skipped here).
            if (cfg.ShowIlvlColumn)
            {
                ImGui.TableSetColumnIndex(IlvlColumn);
                ImGui.TextUnformatted(item.Ilvl.ToString());
            }

            if (cfg.ShowCategoryColumn)
            {
                ImGui.TableSetColumnIndex(CategoryColumn);
                ImGui.TextUnformatted(item.CategoryName);
            }

            // Task 8.2: Vendor column, skipped when hidden via cfg.ShowVendorColumn (the column
            // itself is still declared — see the TableSetColumnEnabled call in DrawItemTable —
            // only the cell render is skipped here). Right-aligned, thousands-separated.
            if (cfg.ShowVendorColumn)
            {
                ImGui.TableSetColumnIndex(VendorColumn);
                var text = item.VendorPrice.ToString("N0");
                var cellWidth = ImGui.GetContentRegionAvail().X;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, cellWidth - ImGui.CalcTextSize(text).X));
                ImGui.TextUnformatted(text);
            }

            ImGui.TableSetColumnIndex(HolderColumn);
            DrawHolders(item);

            ImGui.TableSetColumnIndex(RequestedQtyColumn);
            DrawQtyControls(item, key, isSelected);
        }

        /// <summary>
        /// Task 6.1: right-click context menu for a Browse row — "Link in chat", "Retrieve just this
        /// now", and a Select/Deselect toggle whose label reflects <paramref name="isSelected"/>.
        /// Only opens on right-click of the item-name cell (<see cref="ImRaii.ContextPopupItem"/>
        /// wraps <c>ImGui.BeginPopupContextItem</c>), so there's no per-frame cost while closed.
        /// </summary>
        private void DrawRowContextMenu(UnifiedItem item, (uint ItemId, bool Hq) key, bool isSelected)
        {
            using var popup = ImRaii.ContextPopupItem("##rowctx");
            if (!popup)
                return;

            if (ImGui.MenuItem("Link in chat"))
                LinkItemInChat(item);

            if (ImGui.MenuItem("Retrieve just this now"))
                RetrieveJustThis(item);

            if (ImGui.MenuItem(isSelected ? "Deselect" : "Select"))
            {
                if (isSelected)
                    selection.Remove(key);
                else
                    selection[key] = item.TotalRetainerQty;
                MarkSelectionDirty();
            }
        }

        /// <summary>
        /// Task 6.2: prints a clickable chat item link via the built-in
        /// <see cref="SeString.CreateItemLink(uint, bool, string?)"/> helper (confirmed against
        /// Dalamud.dll via ilspycmd — it assembles the rarity color + link + display-name payload
        /// chain itself), rather than hand-building an <c>ItemPayload</c>/<c>RawPayload.LinkTerminator</c>
        /// chain.
        /// </summary>
        private void LinkItemInChat(UnifiedItem item)
        {
            Plugin.ChatGui.Print(SeString.CreateItemLink(item.ItemId, item.Hq));
        }

        /// <summary>
        /// Task 6.3: single-item "retrieve just this now" — a one-entry selection at the item's full
        /// retainer qty, planned via <see cref="RetrievePlanner.Plan"/> and submitted through the SAME
        /// gated path the Preview confirm button and <c>/retrieve</c> use
        /// (<see cref="RetrieveLauncher.TrySubmit"/>): disclaimer/bell/already-running gates all apply
        /// identically, no bypass.
        /// </summary>
        private void RetrieveJustThis(UnifiedItem item)
        {
            var single = new Dictionary<(uint ItemId, bool Hq), uint> { [(item.ItemId, item.Hq)] = item.TotalRetainerQty };
            var batch = RetrievePlanner.Plan(single, cachedItems);

            if (!RetrieveLauncher.TrySubmit(batch, cfg, out var reason))
            {
                Plugin.ChatGui.PrintError($"[RetainerReach] Cannot retrieve: {reason}");
                return;
            }

            viewMode = ViewMode.Running;
        }

        /// <summary>
        /// Task 3.3/3.4: compact "-"/input/"+" stepper group plus a full-width slider, all clamped
        /// to <c>[1, item.TotalRetainerQty]</c> and disabled while the row isn't selected. Called
        /// from inside <see cref="DrawItemRow"/>'s per-row <see cref="ImRaii.PushId(int, bool)"/>
        /// scope, so the fixed literal labels below are unique per row.
        /// </summary>
        private void DrawQtyControls(UnifiedItem item, (uint ItemId, bool Hq) key, bool isSelected)
        {
            var max = (int)item.TotalRetainerQty;
            var qty = selection.TryGetValue(key, out var requestedQty) ? (int)requestedQty : max;

            // SliderInt (and a min==max stepper range) is degenerate when there's only one unit —
            // disable the slider and steppers entirely rather than calling SliderInt with vMin==vMax.
            var singleQty = max <= 1;

            using (ImRaii.Disabled(!isSelected))
            {
                using (ImRaii.Disabled(singleQty))
                {
                    if (ImGui.Button("-") && isSelected)
                    {
                        qty = Math.Clamp(qty - 1, 1, max);
                        selection[key] = (uint)qty;
                        MarkSelectionDirty();
                    }
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(70);
                if (ImGui.InputInt("##qty", ref qty, 0, 0) && isSelected)
                {
                    qty = Math.Clamp(qty, 1, max);
                    selection[key] = (uint)qty;
                    MarkSelectionDirty();
                }

                ImGui.SameLine();
                using (ImRaii.Disabled(singleQty))
                {
                    if (ImGui.Button("+") && isSelected)
                    {
                        qty = Math.Clamp(qty + 1, 1, max);
                        selection[key] = (uint)qty;
                        MarkSelectionDirty();
                    }
                }

                // Slider on the SAME line as the steppers (fills the rest of the column) so each row
                // stays one line tall.
                ImGui.SameLine();
                using (ImRaii.Disabled(singleQty))
                {
                    if (singleQty)
                    {
                        // Guard: ImGui.SliderInt asserts when vMin == vMax. Render an inert
                        // disabled stand-in instead of calling the real widget with max<=1.
                        ImGui.SetNextItemWidth(-1);
                        var fixedQty = qty;
                        ImGui.SliderInt("##qtyslider", ref fixedQty, 0, 1);
                    }
                    else
                    {
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.SliderInt("##qtyslider", ref qty, 1, max) && isSelected)
                        {
                            qty = Math.Clamp(qty, 1, max);
                            selection[key] = (uint)qty;
                            MarkSelectionDirty();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Task 3.4/5.4: invalidation hook for selection/qty changes — invalidates the Task 5.4
        /// Browse summary (<see cref="summaryDirty"/>). Must never set <c>viewDirty</c>:
        /// qty/selection changes don't affect filter membership or sort order, so they must not
        /// trigger a full <see cref="RecomputeView"/>.
        /// </summary>
        private void MarkSelectionDirty()
        {
            summaryDirty = true;
        }

        private static void DrawHolders(UnifiedItem item)
        {
            if (item.Holdings.Count == 0)
            {
                ImGui.TextDisabled("-");
                return;
            }

            if (item.Holdings.Count == 1)
            {
                ImGui.TextUnformatted(item.Holdings[0].RetainerName);
                return;
            }

            ImGui.TextUnformatted($"{item.Holdings.Count} retainers");
            if (ImGui.IsItemHovered())
            {
                var names = string.Join("\n", item.Holdings.Select(h => $"{h.RetainerName}: {h.Qty}"));
                ImGui.SetTooltip(names);
            }
        }

        /// <summary>
        /// Direct Retrieve action at the bottom of the Browse view: plans the current selection and
        /// submits it straight through <see cref="RetrieveLauncher.TrySubmit"/> to a running own-bell
        /// pull (no Preview step). Disabled with an inline reason when nothing is selected or a gate
        /// blocks it (no bell / bags full / a run already in progress).
        /// </summary>
        private void DrawRetrieveButton()
        {
            var hasSelection = selection.Count > 0;
            var reasons = RetrieveLauncher.DisabledReasons(cfg);

            using (ImRaii.Disabled(!hasSelection || reasons.Count > 0))
            using (HubStyle.Primary())
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Truck, "Retrieve selected"))
                {
                    var batch = RetrievePlanner.Plan(selection, cachedItems);
                    if (RetrieveLauncher.TrySubmit(batch, cfg, out var reason))
                        viewMode = ViewMode.Running;
                    else
                        Plugin.ChatGui.PrintError($"[RetainerReach] Cannot retrieve: {reason}");
                }
            }

            if (!hasSelection)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(select at least one item)");
            }
            else if (reasons.Count > 0)
            {
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Warn))
                    ImGui.TextWrapped($"Disabled: {string.Join(", ", reasons)}");
            }
        }

        private string ItemNameFor(uint itemId)
            => nameById.TryGetValue(itemId, out var name) ? name : itemId.ToString();

        private void DrawRunningPanel()
        {
            ImGui.TextUnformatted("Retrieving...");
            ImGui.Separator();

            ImGui.TextUnformatted($"Current retainer: {RetrieveScheduler.CurrentRetainerName ?? "-"}");
            ImGui.TextUnformatted($"State: {RetrieveScheduler.CurrentState}");

            // Live pull readout so a long Pulling state doesn't look frozen: the item in flight + how
            // many of this retainer's stacks are done.
            if (RetrieveScheduler.CurrentState == RetrieveScheduler.State.Pulling)
            {
                var pullTotal = RetrieveScheduler.PullsTotalThisRetainer;
                var pullDone = Math.Max(0, pullTotal - RetrieveScheduler.PullsRemaining);
                var pullItemId = RetrieveScheduler.CurrentPullItemId;

                ImGui.Spacing();
                if (pullItemId != 0)
                {
                    ImGui.TextUnformatted("Pulling:");
                    ImGui.SameLine();
                    if (ItemIcon.Draw(pullItemId, RetrieveScheduler.CurrentPullHq))
                    {
                        ImGui.SameLine();
                        ItemIcon.AlignTextToIcon();
                    }
                    ImGui.TextUnformatted(ItemNameFor(pullItemId) + HqSuffix(RetrieveScheduler.CurrentPullHq));
                }

                if (pullTotal > 0)
                    ImGui.TextDisabled($"{pullDone}/{pullTotal} stacks from this retainer");
            }

            var total = RetrieveScheduler.TotalRetainers;
            var done = RetrieveScheduler.DoneRetainers;
            var pending = RetrieveScheduler.PendingRetainers;
            var failed = RetrieveScheduler.FailedRetainers;

            var fraction = total > 0 ? (float)done / total : 0f;
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{done}/{total}");
            ImGui.TextDisabled($"done {done} · pending {pending} · failed {failed}");

            if (!Guards.SafeToAct())
            {
                using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Warn))
                    ImGui.TextWrapped("paused: interrupted (cutscene / loading / quest event)");
            }

            ImGui.Spacing();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Stop, "Stop"))
                RetrieveScheduler.Abort();
        }

        private void DrawResultsPanel()
        {
            if (!resultsRefreshed)
            {
                RefreshItems();
                // Results only change between runs — compute the retryable flag once on entry, not
                // per frame while the panel is visible.
                resultsHaveRetryable = ResultsHaveRetryable();
                resultsRefreshed = true;
            }

            ImGui.TextUnformatted($"Retrieve {RetrieveScheduler.CurrentState}");
            ImGui.Separator();

            // Fill-height scroll region so a long per-retainer results list grows with the window and
            // scrolls internally, keeping the Back button pinned as a footer.
            var height = MathF.Max(64f, ImGui.GetContentRegionAvail().Y - FooterReserve(2));
            using (var scroll = ImRaii.Child("###ResultsScroll", new Vector2(-1, height)))
            {
                if (scroll)
                {
                    if (RetrieveScheduler.ErrorMessage is { } error)
                    {
                        using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Bad))
                            ImGui.TextWrapped(error);
                        ImGui.Spacing();
                    }

                    if (RetrieveScheduler.Results.Count == 0)
                        ImGui.TextDisabled("No retainers were fully processed.");
                    else
                        foreach (var result in RetrieveScheduler.Results)
                            DrawRetainerResult(result);
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowLeft, "Back"))
            {
                viewMode = ViewMode.Browse;
                selection.Clear();
                MarkSelectionDirty();
            }

            // Retry: re-run only the shortfall (failed/short targets). Hidden when nothing is retryable
            // or a run is already going. Flag cached on Results entry (see above).
            if (resultsHaveRetryable)
            {
                ImGui.SameLine();
                using (ImRaii.Disabled(RetrieveScheduler.IsRunning))
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "Retry failed"))
                        RetryFailed();
                }
            }
        }

        /// <summary>True when the last run left any failed/short target with unretrieved quantity remaining.</summary>
        private static bool ResultsHaveRetryable()
            => RetrieveScheduler.Results.Any(r => r.Targets.Any(
                t => t.Outcome is TargetOutcome.Failed or TargetOutcome.Short && t.Requested > t.Pulled));

        /// <summary>
        /// Rebuilds a selection from the last run's failed/short shortfall (summed per (ItemId, Hq)
        /// across retainers) and re-plans it against the freshly-refreshed inventory
        /// (<see cref="RefreshItems"/> already ran on entry to Results) — so the retry targets whatever
        /// retainer currently holds each item and clamps to what is actually still there. Submits via
        /// the same gated <see cref="RetrieveLauncher.TrySubmit"/> path as every other retrieve entry.
        /// </summary>
        private void RetryFailed()
        {
            var retry = new Dictionary<(uint ItemId, bool Hq), uint>();
            foreach (var result in RetrieveScheduler.Results)
            {
                foreach (var t in result.Targets)
                {
                    if (t.Outcome is not (TargetOutcome.Failed or TargetOutcome.Short) || t.Requested <= t.Pulled)
                        continue;

                    var key = (t.ItemId, t.Hq);
                    retry[key] = retry.GetValueOrDefault(key) + (t.Requested - t.Pulled);
                }
            }

            if (retry.Count == 0)
                return;

            var batch = RetrievePlanner.Plan(retry, cachedItems);
            if (batch.Targets.Count == 0)
            {
                Plugin.ChatGui.Print("[RetainerReach] Nothing to retry — those items are no longer on your retainers.");
                return;
            }

            if (!RetrieveLauncher.TrySubmit(batch, cfg, out var reason))
            {
                Plugin.ChatGui.PrintError($"[RetainerReach] Cannot retry: {reason}");
                return;
            }

            viewMode = ViewMode.Running;
        }

        private void DrawRetainerResult(RetainerResult result)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Accent))
                ImGui.TextUnformatted(result.RetainerName);

            ImGui.TextDisabled($"  moved {result.Moved} · failed {result.Failed} · short {result.Short}");

            foreach (var target in result.Targets)
                DrawTargetResult(target);

            ImGui.Spacing();
        }

        private void DrawTargetResult(TargetResult target)
        {
            var label = ItemNameFor(target.ItemId) + HqSuffix(target.Hq);
            var line = $"{label}: {target.Outcome} ({target.Pulled}/{target.Requested})";
            if (target.Reason != null)
                line += $" — {target.Reason}";

            switch (target.Outcome)
            {
                case TargetOutcome.Failed:
                    using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Bad))
                        DrawIndentedIconLine(target.ItemId, target.Hq, line, wrap: true);
                    break;
                case TargetOutcome.Short:
                    using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Warn))
                        DrawIndentedIconLine(target.ItemId, target.Hq, line, wrap: true);
                    break;
                default:
                    DrawIndentedIconLine(target.ItemId, target.Hq, line, wrap: false);
                    break;
            }
        }

        /// <summary>
        /// Shared "    " indent + inline item icon + trailing text used by <see cref="DrawTargetResult"/>
        /// (Feature 1: icon left of the item name). <paramref name="wrap"/> preserves the original
        /// per-outcome choice between <c>TextWrapped</c> (Failed/Short) and <c>TextUnformatted</c> (Moved).
        /// </summary>
        private static void DrawIndentedIconLine(uint itemId, bool hq, string text, bool wrap)
        {
            ImGui.TextUnformatted("    ");
            ImGui.SameLine(0, 0);
            if (ItemIcon.Draw(itemId, hq))
            {
                ImGui.SameLine();
                ItemIcon.AlignTextToIcon();
            }

            if (wrap)
                ImGui.TextWrapped(text);
            else
                ImGui.TextUnformatted(text);
        }

        private void DrawDevSection()
        {
            if (!cfg.DevLog)
                return;

            ImGui.Separator();
            ImGui.TextDisabled("Dev");

            using (ImRaii.Disabled(RetrieveScheduler.IsRunning))
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Bug, "Dry-run automation (no moves)"))
                {
                    RetrieveScheduler.Start(BuildDryRunBatch());
                    viewMode = ViewMode.Running;
                }
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Stop, "Abort"))
                RetrieveScheduler.Abort();

            ImGui.TextUnformatted($"Scheduler: {RetrieveScheduler.CurrentState}");

            if (RetrieveScheduler.CurrentRetainerName is { } retainerName)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"· {retainerName}");
            }

            if (RetrieveScheduler.ErrorMessage is { } error)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Bad))
                    ImGui.TextWrapped(error);
            }
        }

        // Dry-run helper: one dummy target per known local retainer, so the scheduler's Pulling
        // stub exercises the full bell/select/open/close/next loop across a real multi-retainer
        // batch without depending on AllaganTools data or moving anything.
        private static RetrieveBatch BuildDryRunBatch()
        {
            var batch = new RetrieveBatch();

            foreach (var (cid, name) in RetainerRoster.All())
            {
                batch.Targets.Add(new RetrieveTarget
                {
                    ItemId = 0,
                    Hq = false,
                    RetainerName = name,
                    RetainerCid = cid,
                    Qty = 0,
                    WorstCasePulled = 0,
                });
            }

            return batch;
        }

        private void RefreshItems()
        {
            cachedItems = UnifiedInventory.Build();
            hasLoaded = true;

            // Present ItemSearchCategory categories with their icon + item count, ordered by count
            // (desc) so the chip bar takes the busiest N. Computed once per refresh, never per frame.
            categoryOptions = cachedItems
                .Where(i => i.SearchCategoryId != 0)
                .GroupBy(i => i.SearchCategoryId)
                .Select(g => (Id: g.Key, Name: ItemSearchCategories.NameFor(g.Key), Icon: ItemSearchCategories.IconFor(g.Key), Count: g.Count()))
                .Where(c => !string.IsNullOrEmpty(c.Name) && c.Icon != 0)
                .OrderByDescending(c => c.Count)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            categoryOptionsAlpha = categoryOptions
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Task 9.3: distinct retainers present in cachedItems (via each item's Holdings), sorted
            // by name — computed once per refresh, never per frame. The per-retainer filter combo
            // (DrawFilterBar) lists exactly this set.
            retainerOptions = cachedItems
                .SelectMany(i => i.Holdings)
                .Select(h => (h.RetainerCid, h.RetainerName))
                .Distinct()
                .OrderBy(r => r.RetainerName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // O(1) name lookup for hot paths + memoized total inventory value for the header — built
            // once here, never per frame.
            nameById = cachedItems
                .GroupBy(i => i.ItemId)
                .ToDictionary(g => g.Key, g => g.First().Name);

            ulong total = 0;
            foreach (var item in cachedItems)
                total += (ulong)item.VendorPrice * item.TotalRetainerQty;
            totalRetainerValue = total;

            headerStats = $"· {retainerOptions.Count} retainers · {cachedItems.Count} items · {totalRetainerValue:N0} gil";

            viewDirty = true;
            // Task 5.4: bag counts/data changed — the cached Browse summary must be refreshed too.
            summaryDirty = true;
        }

        /// <summary>
        /// Task 4.5/5.3/9.2/9.3: the memoized filter+sort pipeline. Applies name substring
        /// (case-insensitive) AND (no categories selected, or the item's category is one of them — OR
        /// within categories) AND (material toggle off, or the item is a recipe ingredient) AND (no
        /// retainers selected, or the item is held by at least one of them — OR within retainers),
        /// then applies the cached <see cref="sortSpec"/> (Task 5.3), into <see cref="viewCache"/>.
        /// Also rebuilds the Task 9.2 <see cref="grouped"/> view of that same result. Only ever called
        /// when <see cref="viewDirty"/> is set (from <see cref="RefreshItems"/>, a filter change, or a
        /// header-click sort change) — never per frame.
        /// </summary>
        private void RecomputeView()
        {
            IEnumerable<UnifiedItem> query = cachedItems;

            if (!string.IsNullOrWhiteSpace(filterText))
                query = query.Where(i => i.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase));

            if (selectedCategories.Count > 0)
                query = query.Where(i => selectedCategories.Contains(i.SearchCategoryId));

            if (materialOnly)
                query = query.Where(i => CraftingMaterials.IsMaterial(i.ItemId));

            // Task 9.3: per-retainer filter — ANDs with the above, ORs within selectedRetainers.
            if (selectedRetainers.Count > 0)
                query = query.Where(i => i.Holdings.Any(h => selectedRetainers.Contains(h.RetainerCid)));

            query = ApplySort(query, sortSpec);

            viewCache = query.ToList();
            viewDirty = false;

            // Only build the grouped view when it's actually shown (still memoized behind viewDirty).
            if (groupByRetainer)
                RecomputeGrouped();

            // Task 5.4: filtering/sorting changes what "M shown" reports (Q-S4), so the cached
            // summary needs a refresh too — still gated by summaryDirty, never per frame.
            summaryDirty = true;
        }

        /// <summary>
        /// Task 9.2: builds <see cref="grouped"/> from the just-recomputed <see cref="viewCache"/> —
        /// called only from inside <see cref="RecomputeView"/> (itself viewDirty-gated), so this is
        /// memoized alongside the filter+sort pass, never a per-frame allocation. One entry per
        /// retainer that holds at least one item in viewCache; an item held by N retainers appears in
        /// N entries' Items lists (grouping is a view lens, selection stays item-level). Groups are
        /// sorted by retainer name.
        /// </summary>
        private void RecomputeGrouped()
        {
            var byRetainer = new Dictionary<ulong, (string Name, List<UnifiedItem> Items)>();

            foreach (var item in viewCache)
            {
                foreach (var holding in item.Holdings)
                {
                    // Respect the active per-retainer filter: an item can pass RecomputeView via one
                    // selected retainer while also being held by unselected ones — don't surface a
                    // group header for a retainer the user filtered out.
                    if (selectedRetainers.Count > 0 && !selectedRetainers.Contains(holding.RetainerCid))
                        continue;

                    if (!byRetainer.TryGetValue(holding.RetainerCid, out var entry))
                    {
                        entry = (holding.RetainerName, new List<UnifiedItem>());
                        byRetainer[holding.RetainerCid] = entry;
                    }

                    entry.Items.Add(item);
                }
            }

            grouped = byRetainer
                .Select(kv => (Retainer: kv.Value.Name, Cid: kv.Key, Items: kv.Value.Items))
                .OrderBy(g => g.Retainer, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Task 5.3/8.2: applies the cached (column index, ascending) sort spec inside the memoized
        /// <see cref="RecomputeView"/> pass. Supported sort keys: Item name, Retainer qty, In bags,
        /// ilvl, Category name, Vendor price; any other column index (Select/Holder(s)/Requested qty
        /// are NoSort, so ImGui never reports them as the active sort column) leaves the query
        /// unsorted.
        /// </summary>
        private static IEnumerable<UnifiedItem> ApplySort(IEnumerable<UnifiedItem> query, (int ColumnIndex, bool Ascending) sort)
        {
            return sort.ColumnIndex switch
            {
                ItemColumn => sort.Ascending
                    ? query.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    : query.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
                RetainerQtyColumn => sort.Ascending
                    ? query.OrderBy(i => i.TotalRetainerQty)
                    : query.OrderByDescending(i => i.TotalRetainerQty),
                InBagsColumn => sort.Ascending
                    ? query.OrderBy(i => i.PlayerBagQty)
                    : query.OrderByDescending(i => i.PlayerBagQty),
                IlvlColumn => sort.Ascending
                    ? query.OrderBy(i => i.Ilvl)
                    : query.OrderByDescending(i => i.Ilvl),
                CategoryColumn => sort.Ascending
                    ? query.OrderBy(i => i.CategoryName, StringComparer.OrdinalIgnoreCase)
                    : query.OrderByDescending(i => i.CategoryName, StringComparer.OrdinalIgnoreCase),
                VendorColumn => sort.Ascending
                    ? query.OrderBy(i => i.VendorPrice)
                    : query.OrderByDescending(i => i.VendorPrice),
                _ => query,
            };
        }

        /// <summary>
        /// Task 5.4/8.3: selection/pull/bag-slot summary + "N items, M shown" line + selection vendor
        /// (gil) value, rendered above the preview button. <see cref="browseSummary"/> is a cached
        /// struct, recomputed only when <see cref="summaryDirty"/> is set (by
        /// <see cref="MarkSelectionDirty"/> on qty/selection edits, by <see cref="RefreshItems"/>, and
        /// by <see cref="RecomputeView"/> whenever the filtered/sorted set changes) — never per frame,
        /// per the Q-M2 perf discipline.
        /// </summary>
        private void DrawSummary()
        {
            if (summaryDirty)
                RecomputeSummary();

            // Prebuilt one-line summary (rebuilt only in RecomputeSummary), no per-frame interpolation.
            using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Warn, summaryOverflow))
                ImGui.TextUnformatted(summaryLine);
        }

        /// <summary>
        /// Task 5.4/8.3: the one place <see cref="RetrievePlanner.Plan"/> and
        /// <see cref="InventoryScan.FreeSlotsInBag"/> are called for the Browse summary — gated by
        /// <see cref="summaryDirty"/>, never per frame. Also sums <c>VendorPrice * requestedQty</c>
        /// over the current selection into <see cref="BrowseSummary.SelectionGilTotal"/>, via a
        /// (ItemId, Hq) lookup built here from <see cref="cachedItems"/> — same summaryDirty gate,
        /// so it never touches a Lumina sheet or rebuilds a lookup per frame.
        /// </summary>
        private void RecomputeSummary()
        {
            var batch = RetrievePlanner.Plan(selection, cachedItems);

            uint worstCasePull = 0;
            foreach (var target in batch.Targets)
                worstCasePull += target.WorstCasePulled;

            var itemsByKey = cachedItems.ToDictionary(i => (i.ItemId, i.Hq));
            ulong selectionGilTotal = 0;
            foreach (var (key, requestedQty) in selection)
            {
                if (itemsByKey.TryGetValue(key, out var item))
                    selectionGilTotal += (ulong)item.VendorPrice * requestedQty;
            }

            browseSummary = new BrowseSummary
            {
                SelectedCount = selection.Count,
                WorstCasePull = worstCasePull,
                BagSlotsNeeded = batch.Targets.Count,
                FreeBagSlots = InventoryScan.FreeSlotsInBag(),
                TotalItemCount = cachedItems.Count,
                ShownCount = viewCache.Count,
                SelectionGilTotal = selectionGilTotal,
            };

            summaryOverflow = browseSummary.BagSlotsNeeded > browseSummary.FreeBagSlots;
            summaryLine = browseSummary.SelectedCount == 0
                ? $"Nothing selected  ·  {browseSummary.ShownCount}/{browseSummary.TotalItemCount} shown"
                : $"Selected {browseSummary.SelectedCount}  ·  up to {browseSummary.WorstCasePull} units  ·  " +
                  $"{browseSummary.BagSlotsNeeded}/{browseSummary.FreeBagSlots} free bag slots  ·  " +
                  $"{browseSummary.SelectionGilTotal:N0} gil  ·  {browseSummary.ShownCount}/{browseSummary.TotalItemCount} shown";

            summaryDirty = false;
        }

        private static void Icon(FontAwesomeIcon icon)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                ImGui.TextUnformatted(icon.ToIconString());
        }

        /// <summary>
        /// Task 1.3/1.4: single source of truth for the HQ marker suffix, used by Browse
        /// (<see cref="DrawItemRow"/>), Preview (<see cref="DrawPreviewRetainerGroup"/>), and Results
        /// (<see cref="DrawTargetResult"/>) so all three sites render the same U+E03C glyph.
        /// </summary>
        private static string HqSuffix(bool hq)
            => hq ? " " + SeIconChar.HighQuality.ToIconString() : string.Empty;
    }
}
