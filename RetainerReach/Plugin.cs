using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using RetainerReach.Automation;
using RetainerReach.Ipc;
using RetainerReach.Logic;
using RetainerReach.Model;
using RetainerReach.Windows;
using XivHubPluginKit;

namespace RetainerReach
{
    public sealed class Plugin : IDalamudPlugin
    {
        public static string Name => "RetainerReach";

        private const string commandName = "/retainerreach";
        private const string aliasCommandName = "/rtr";
        private const string retrieveCommandName = "/retrieve";
        private const string retrieveUsage = "[RetainerReach] Usage: /retrieve <item name> [quantity|all]";

        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] public static IDataManager DataManager { get; private set; } = null!;
        [PluginService] public static IPluginLog Logger { get; private set; } = null!;
        [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
        [PluginService] public static IFramework Framework { get; private set; } = null!;
        [PluginService] public static IClientState ClientState { get; private set; } = null!;
        [PluginService] public static ICondition Condition { get; private set; } = null!;
        [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
        [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
        [PluginService] public static IGameGui GameGui { get; private set; } = null!;
        [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;

        public Configuration Configuration { get; init; }
        public static Configuration C { get; private set; } = null!;
        public WindowSystem WindowSystem = new("RetainerReach");
        private readonly MainWindow mainWindow;
        private readonly ConfigWindow configWindow;
        private readonly DevTelemetry telemetry;

        // Task 7.5: detects scheduler state transitions across ticks (compared here in Plugin.cs,
        // rather than adding a hook to RetrieveScheduler) so DevTelemetry.Log fires once per
        // transition, in addition to the per-tick Snapshot below.
        private RetrieveScheduler.State lastLoggedState = RetrieveScheduler.State.Idle;

        public Plugin()
        {
            KitServices.Init(DataManager, Logger, ChatGui, "[RetainerReach]");

            ECommonsMain.Init(PluginInterface, this, Module.DalamudReflector);

            this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.Configuration.Initialize(PluginInterface);
            C = this.Configuration;

            telemetry = new DevTelemetry("RetainerReach", () => C.DevLog, () => C.DevLogUrl);

            mainWindow = new MainWindow(this.Configuration);
            WindowSystem.AddWindow(mainWindow);

            configWindow = new ConfigWindow(this.Configuration);
            WindowSystem.AddWindow(configWindow);

            // Let the main window's settings button open the config window (it doesn't own it).
            mainWindow.OpenSettings = () => configWindow.IsOpen = true;

            CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open the RetainerReach window. 'stop' aborts a running retrieve."
            });

            CommandManager.AddHandler(aliasCommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Alias for /retainerreach.",
                ShowInHelp = true,
            });

            CommandManager.AddHandler(retrieveCommandName, new CommandInfo(OnRetrieveCommand)
            {
                HelpMessage = "Retrieve <item name> [quantity|all] from your retainers without opening the window.",
                ShowInHelp = true,
            });

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

            Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            RetrieveScheduler.Shutdown();
            telemetry.Dispose();

            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

            WindowSystem.RemoveAllWindows();
            mainWindow.Dispose();
            configWindow.Dispose();

            CommandManager.RemoveHandler(commandName);
            CommandManager.RemoveHandler(aliasCommandName);
            CommandManager.RemoveHandler(retrieveCommandName);

            ECommonsMain.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            var arg = args.Trim();
            if (string.Equals(arg, "stop", StringComparison.OrdinalIgnoreCase))
            {
                RetrieveScheduler.Abort();
                return;
            }

            mainWindow.IsOpen = true;
        }

        /// <summary>
        /// Feature 3: <c>/retrieve &lt;item name&gt; [quantity|all]</c> — resolves the item against a
        /// fresh <see cref="UnifiedInventory.Build"/>, builds a single-item <see cref="RetrieveBatch"/>
        /// via <see cref="RetrievePlanner.Plan"/>, and submits it through the same
        /// <see cref="RetrieveLauncher"/> path <see cref="Windows.MainWindow"/>'s confirm button uses
        /// (Feature 4), so this command and the UI share one set of gates/routing.
        /// </summary>
        private void OnRetrieveCommand(string command, string args)
        {
            if (!TryParseRetrieveArgs(args, out var itemName, out var wantAll, out var explicitQty))
            {
                ChatGui.Print(retrieveUsage);
                return;
            }

            var items = UnifiedInventory.Build();

            var exact = items.Where(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = exact.Count > 0
                ? exact
                : items.Where(i => i.Name.Contains(itemName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 0)
            {
                ChatGui.PrintError($"[RetainerReach] No retainer holds an item matching '{itemName}'.");
                return;
            }

            var distinctIds = matches.Select(i => i.ItemId).Distinct().ToList();
            if (distinctIds.Count > 1)
            {
                var candidates = string.Join(", ", matches.Select(i => i.Name).Distinct().Take(8));
                ChatGui.Print($"[RetainerReach] Multiple items match '{itemName}': {candidates}. Please be more specific.");
                return;
            }

            // Single ItemId; if both an HQ and an NQ UnifiedItem exist for it, default to NQ.
            var chosen = matches.FirstOrDefault(i => !i.Hq) ?? matches[0];

            var requestedQty = wantAll ? chosen.TotalRetainerQty : explicitQty ?? 1;
            requestedQty = (uint)Math.Clamp(requestedQty, 1, chosen.TotalRetainerQty);

            var selection = new Dictionary<(uint ItemId, bool Hq), uint> { [(chosen.ItemId, chosen.Hq)] = requestedQty };
            var batch = RetrievePlanner.Plan(selection, items);

            if (!RetrieveLauncher.TrySubmit(batch, C, out var reason))
            {
                ChatGui.PrintError($"[RetainerReach] Cannot retrieve: {reason}");
                return;
            }

            ChatGui.Print($"[RetainerReach] Retrieving {requestedQty}x {chosen.Name} from {batch.ByRetainer().Count()} retainer(s)...");
        }

        /// <summary>
        /// Tokenizes and interprets <paramref name="args"/> per Feature 3's parsing rules: a trailing
        /// positive integer is the quantity, a trailing "all" (case-insensitive) requests every
        /// available unit, otherwise the whole string is the item name and qty defaults to 1. Returns
        /// false (usage line) for an empty/whitespace-only <paramref name="args"/> or when no item
        /// name remains after stripping a trailing quantity/"all" token.
        /// </summary>
        private static bool TryParseRetrieveArgs(string args, out string itemName, out bool wantAll, out uint? explicitQty)
        {
            itemName = string.Empty;
            wantAll = false;
            explicitQty = null;

            var trimmed = args.Trim();
            if (trimmed.Length == 0)
                return false;

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lastToken = tokens[^1];

            if (int.TryParse(lastToken, out var parsedQty) && parsedQty > 0 && tokens.Length > 1)
            {
                itemName = string.Join(' ', tokens[..^1]);
                explicitQty = (uint)parsedQty;
            }
            else if (string.Equals(lastToken, "all", StringComparison.OrdinalIgnoreCase) && tokens.Length > 1)
            {
                itemName = string.Join(' ', tokens[..^1]);
                wantAll = true;
            }
            else
            {
                itemName = trimmed;
            }

            return itemName.Length > 0;
        }

        private void ToggleMainUi() => mainWindow.Toggle();
        private void ToggleConfigUi() => configWindow.Toggle();
        private void DrawUI() => WindowSystem.Draw();

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                RetrieveScheduler.Tick();
            }
            catch (Exception ex)
            {
                // Defense-in-depth: a throw anywhere in the state machine must not propagate out of the
                // framework tick (it would re-enter the broken state every frame). Abort the run cleanly.
                Logger.Error(ex, "[RetainerReach] Scheduler tick threw; aborting the run.");
                RetrieveScheduler.Abort();
            }

            // Telemetry is a dev-only feature: skip the per-frame closure alloc + state compare entirely
            // unless it's enabled (the normal case pays nothing here).
            if (!C.DevLog)
                return;

            var state = RetrieveScheduler.CurrentState;
            if (state != lastLoggedState)
            {
                var retainer = RetrieveScheduler.CurrentRetainerName;
                telemetry.Log($"state {lastLoggedState} -> {state}" + (retainer != null ? $" ({retainer})" : string.Empty));
                lastLoggedState = state;
            }

            telemetry.Snapshot(() =>
            {
                var line = $"state={state} retainer={RetrieveScheduler.CurrentRetainerName ?? "-"} " +
                    $"done={RetrieveScheduler.DoneRetainers}/{RetrieveScheduler.TotalRetainers} " +
                    $"pending={RetrieveScheduler.PendingRetainers} failed={RetrieveScheduler.FailedRetainers}";

                // When stalled selecting a retainer, attach the live RetainerList + active-retainer
                // dump so the reason (name/CID mismatch) shows up in the telemetry stream.
                if (state == RetrieveScheduler.State.SelectRetainer)
                    line += $" | expectedCid={RetrieveScheduler.CurrentRetainerCid} {RetainerUi.DescribeRetainerList()}";
                else if (state == RetrieveScheduler.State.OpenInventory)
                    line += $" | {RetainerUi.DescribeSelectString()}";

                return line;
            });
        }
    }
}
