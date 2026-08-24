using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using ECommons.Automation.NeoTaskManager;
using FFXIVClientStructs.FFXIV.Client.Game;
using RetainerReach.Game;
using RetainerReach.Ipc;
using RetainerReach.Model;
using XivHubPluginKit.Inventory;

namespace RetainerReach.Automation
{
    /// <summary>
    /// Frame-driven state machine that drives the bell -&gt; RetainerList -&gt; select -&gt;
    /// "Entrust or withdraw items" -&gt; wait-for-inventory -&gt; pull -&gt; close -&gt; next loop
    /// (PLAN.md Architecture Decision / Phase 5-6). <see cref="Tick"/> is called once per frame from
    /// <c>Plugin.cs</c>'s <c>IFramework.Update</c>; it gates on <see cref="Guards.SafeToAct"/> before
    /// advancing state so genuine interruptions (cutscene, loading, quest events) pause the run.
    ///
    /// <see cref="State.Pulling"/> runs its own frame-spaced native-command pull loop (Phase 6, Tasks
    /// 6.1-6.3) — never the kit <c>MoveQueue</c>/<c>MoveItemSlot</c>, since retainer containers only
    /// accept the native retainer item command (PLAN.md Architecture Decision). Once a retainer's
    /// picked slots drain (or its bags fill), the result is recorded (Task 6.5) and control falls
    /// through to the non-mutating <see cref="State.CloseInventory"/>/<see cref="State.NextRetainer"/>
    /// transitions.
    /// </summary>
    public static class RetrieveScheduler
    {
        public enum State
        {
            Idle,
            TargetBell,
            RingBell,
            WaitList,
            SelectRetainer,
            OpenInventory,
            WaitInventory,
            Pulling,
            CloseInventory,
            NextRetainer,
            Done,
            Aborted,
            Error,
        }

        private const int StabilizationFrames = 5;
        private const int DefaultWaitTimeoutMs = 8000;

        /// <summary>
        /// How long (in frames) a single <c>Retrieve</c> invoke is given to reflect in the live
        /// retainer scan before it is recorded failed and the pull loop advances (PLAN.md Edge Cases
        /// "Pull didn't land" — no infinite retry). Retrieving from a retainer round-trips to the
        /// server, so this is deliberately generous (~5s at 60fps, longer at lower framerates): a
        /// too-short window reports an item that DID land as failed (the original 5-check ≈ 83ms
        /// budget did exactly that). The live re-scan is cheap, so a long ceiling costs nothing when
        /// the pull lands early (it exits the instant it sees the source slot emptied).
        /// </summary>
        private const int LandingTimeoutFrames = 300;

        private static readonly TaskManager tasks = new(new TaskManagerConfiguration(
            timeLimitMS: DefaultWaitTimeoutMs, abortOnTimeout: true, showError: false));

        private static Queue<IGrouping<ulong, RetrieveTarget>> workQueue = new();
        private static IGrouping<ulong, RetrieveTarget>? currentRetainer;
        private static IGameObject? targetedBell;
        private static int inventoryStableFrames;

        private static bool waitPending;
        private static bool waitFailed;

        // --- Pulling loop state (Task 6.3) ---
        private enum PullPhase { Ready, AwaitingLanding }

        private sealed record PendingPull(RetrieveTarget Target, uint ItemId, bool Hq, uint ExpectedQty);

        private static readonly Queue<PendingPull> pullQueue = new();
        private static readonly Dictionary<RetrieveTarget, uint> pulledByTarget = new();
        private static readonly Dictionary<RetrieveTarget, string> targetFailureReasons = new();

        /// <summary>
        /// Slots that already exhausted <see cref="LandingTimeoutFrames"/> this Pulling session. Excluded
        /// from the next pull's <c>(ItemId,Hq)</c> live-scan match so a failed-but-still-live stack
        /// isn't re-selected in place of a distinct, untouched stack of the same item (which would
        /// silently skip a genuinely retrievable stack). Cleared in <see cref="ResetPullingState"/>.
        /// </summary>
        private static readonly HashSet<(InventoryType Container, int SlotIndex)> deadSlots = new();
        private static PullPhase pullPhase = PullPhase.Ready;
        private static PendingPull? activePull;
        private static (InventoryType Container, int SlotIndex) activeSlot;
        private static uint activePreQty;
        private static int landingDeadlineFrame;
        private static int pullFrameCounter;
        private static int nextActionFrame;

        // --- Run accounting (Task 6.5) ---
        private static readonly List<RetainerResult> results = new();

        /// <summary>
        /// True once the in-flight retainer's result has been appended to <see cref="results"/> and
        /// before the next retainer is dequeued. Guards <see cref="FailedRetainers"/> from counting a
        /// benign post-finalize UI-teardown timeout (e.g. the final RetainerList close) as a retainer
        /// failure — which would push <see cref="PendingRetainers"/> negative. Reset per dequeue.
        /// </summary>
        private static bool currentRetainerFinalized;

        /// <summary>The scheduler's current state; exposed for the UI / Task 5.6 dry-run readout.</summary>
        public static State CurrentState { get; private set; } = State.Idle;

        /// <summary>The retainer currently being visited (name, falling back to a CID string); null when idle.</summary>
        public static string? CurrentRetainerName { get; private set; }

        /// <summary>CID of the retainer currently being visited (0 when idle); exposed for diagnostics/telemetry.</summary>
        public static ulong CurrentRetainerCid => currentRetainer?.Key ?? 0;

        /// <summary>Set on <see cref="State.Error"/>/<see cref="State.Aborted"/>; null otherwise.</summary>
        public static string? ErrorMessage { get; private set; }

        /// <summary>True whenever a run is in progress (not Idle/Done/Aborted/Error).</summary>
        public static bool IsRunning => CurrentState is not (State.Idle or State.Done or State.Aborted or State.Error);

        /// <summary>ItemId of the stack currently being pulled (0 between pulls / outside Pulling); the UI resolves the name.</summary>
        public static uint CurrentPullItemId => activePull?.ItemId ?? 0;

        /// <summary>HQ flag of the stack currently being pulled.</summary>
        public static bool CurrentPullHq => activePull?.Hq ?? false;

        /// <summary>Stacks seeded for the current retainer on entry to <see cref="State.Pulling"/> (0 outside a pull loop).</summary>
        public static int PullsTotalThisRetainer { get; private set; }

        /// <summary>Stacks still queued (including the one in flight) for the current retainer; done = <see cref="PullsTotalThisRetainer"/> − this.</summary>
        public static int PullsRemaining => pullQueue.Count;

        /// <summary>Retainers in the batch this run started with (set by <see cref="Start"/>).</summary>
        public static int TotalRetainers { get; private set; }

        /// <summary>
        /// Finished per-retainer outcomes, in visit order, for the Phase 7 results view. A retainer is
        /// appended here exactly once, when its <see cref="State.Pulling"/> loop drains or its bags
        /// fill (Task 6.5); a retainer interrupted mid-visit by <see cref="State.Error"/> or
        /// <see cref="Abort"/> is NOT appended (it is reflected by <see cref="FailedRetainers"/> /
        /// the gap between <see cref="DoneRetainers"/> and <see cref="TotalRetainers"/> instead).
        /// </summary>
        public static IReadOnlyList<RetainerResult> Results => results;

        /// <summary>Retainers whose visit fully finished (drained or bag-full-shortfall) and were recorded into <see cref="Results"/>.</summary>
        public static int DoneRetainers => results.Count;

        /// <summary>1 when the run hard-errored with a retainer's visit abandoned mid-way (before its
        /// result was finalized), else 0 — only <see cref="State.Error"/> with an unfinalized current
        /// retainer counts; a user <see cref="Abort"/>, or an error during post-finalize UI teardown, does not.</summary>
        public static int FailedRetainers => CurrentState == State.Error && !currentRetainerFinalized ? 1 : 0;

        /// <summary>
        /// Retainers not yet fully resolved (still queued, or the one currently being visited).
        /// <see cref="DoneRetainers"/> + <see cref="PendingRetainers"/> + <see cref="FailedRetainers"/>
        /// always equals <see cref="TotalRetainers"/>, so the Phase 7 UI can render
        /// <c>Done/(Done+Pending+Failed)</c> directly.
        /// </summary>
        public static int PendingRetainers => TotalRetainers - DoneRetainers - FailedRetainers;

        /// <summary>Seeds the per-retainer work list (ordered by <see cref="RetrieveBatch.ByRetainer"/>) and starts the run.</summary>
        public static void Start(RetrieveBatch batch)
        {
            if (IsRunning)
                return;

            // Defensive: clear any AutoRetainer suppression a prior run may have left set (a missed
            // exit path) before we (re)apply it for this run.
            AutoRetainerIpc.RestoreAutoRetainer();

            workQueue = new Queue<IGrouping<ulong, RetrieveTarget>>(batch.ByRetainer());
            currentRetainer = null;
            CurrentRetainerName = null;
            targetedBell = null;
            inventoryStableFrames = 0;
            waitPending = false;
            waitFailed = false;
            ErrorMessage = null;
            tasks.Abort();

            TotalRetainers = workQueue.Count;
            results.Clear();
            currentRetainerFinalized = false;
            ResetPullingState();

            if (workQueue.Count == 0)
            {
                CurrentState = State.Done;
                return;
            }

            // Own-bell run: suppress AutoRetainer's autonomous scheduler + Talk auto-clicker so the two
            // plugins don't both drive the summoning-bell / retainer UI and collide. Restored on every
            // run-exit path (Done/Aborted/Error).
            AutoRetainerIpc.SuppressAutoRetainer();
            CurrentState = State.TargetBell;
        }

        /// <summary>Hard-stops the run and closes whatever retainer UI is open, from any state.</summary>
        public static void Abort()
        {
            tasks.Abort();
            waitPending = false;
            ResetPullingState();

            AutoRetainerIpc.RestoreAutoRetainer();
            CloseOpenRetainerUi();

            workQueue.Clear();
            currentRetainer = null;
            CurrentRetainerName = null;
            targetedBell = null;
            CurrentState = State.Aborted;
        }

        /// <summary>Clears the frame-spaced pull loop's state (Task 6.3), used by <see cref="Start"/>, <see cref="Abort"/> and <see cref="ErrorOut"/>.</summary>
        private static void ResetPullingState()
        {
            pullQueue.Clear();
            pulledByTarget.Clear();
            targetFailureReasons.Clear();
            deadSlots.Clear();
            pullPhase = PullPhase.Ready;
            activePull = null;
            activeSlot = default;
            activePreQty = 0;
            landingDeadlineFrame = 0;
            pullFrameCounter = 0;
            nextActionFrame = 0;
            PullsTotalThisRetainer = 0;
        }

        /// <summary>
        /// Tears down any open retainer UI (inventory addon, RetainerList, Retainer agent) in one
        /// synchronous burst. Uses <c>force: true</c> so all three fire this frame — the shared
        /// <c>GenericThrottle</c> key would otherwise drop the 2nd/3rd call. Shared by <see cref="Abort"/>
        /// and <see cref="ErrorOut"/> so an errored run doesn't leave stale addons on screen.
        /// </summary>
        private static void CloseOpenRetainerUi()
        {
            RetainerUi.CloseRetainerInventory(force: true);
            RetainerUi.CloseRetainerList(force: true);
            RetainerUi.CloseRetainerAgent(force: true);
        }

        /// <summary>Releases the scheduler's internal <see cref="TaskManager"/> Framework.Update subscription. Call from Plugin.Dispose().</summary>
        public static void Shutdown()
        {
            // Never leave AutoRetainer suppressed if RetainerReach unloads mid-run.
            AutoRetainerIpc.RestoreAutoRetainer();
            tasks.Dispose();
        }

        /// <summary>
        /// Resolves a retainer group to a display/selection name: the local FFXIVClientStructs roster
        /// name (matches the RetainerList entries) when the CID is present there, else the AllaganTools
        /// name carried on the batch targets, else the raw CID string as a last resort. Prefers a real
        /// name over the numeric CID so <see cref="RetainerUi.SelectRetainer"/> can match a list entry
        /// even when the AllaganTools CID isn't in the local roster.
        /// </summary>
        private static string ResolveRetainerName(IGrouping<ulong, RetrieveTarget> retainer)
            => RetainerRoster.NameForCid(retainer.Key)
               ?? retainer.FirstOrDefault()?.RetainerName
               ?? retainer.Key.ToString();

        /// <summary>Advances the state machine by one frame. Called from Plugin.cs's IFramework.Update.</summary>
        public static void Tick()
        {
            if (!IsRunning)
                return;

            if (!Guards.SafeToAct())
                return;

            // A summoned retainer plays a greeting line in the Talk subtitle box that blocks the
            // "Entrust or withdraw items" SelectString from appearing until it is advanced. Nothing
            // else in the flow dismisses it, so click it through every active tick (mirrors
            // AutoRetainer's MiniTA Talk click).
            RetainerUi.ProgressTalk();

            switch (CurrentState)
            {
                case State.TargetBell: TickTargetBell(); break;
                case State.RingBell: TickRingBell(); break;
                case State.WaitList: TickWaitList(); break;
                case State.SelectRetainer: TickSelectRetainer(); break;
                case State.OpenInventory: TickOpenInventory(); break;
                case State.WaitInventory: TickWaitInventory(); break;
                case State.Pulling: TickPulling(); break;
                case State.CloseInventory: TickCloseInventory(); break;
                case State.NextRetainer: TickNextRetainer(); break;
            }
        }

        private static void TickTargetBell()
        {
            if (!BellFinder.Reachable())
            {
                AbortWithMessage("No summoning bell is reachable. Stand near a bell and try again.");
                return;
            }

            var bell = BellFinder.Preferred();
            if (bell == null)
            {
                AbortWithMessage("No summoning bell found.");
                return;
            }

            if (RetainerUi.SetBellTarget(bell))
            {
                targetedBell = bell;
                CurrentState = State.RingBell;
            }
        }

        private static void TickRingBell()
        {
            if (targetedBell == null)
            {
                ErrorOut("Lost the summoning bell target.");
                return;
            }

            var bell = targetedBell;
            if (!waitPending)
                BeginWait("RingBell", () => RetainerUi.InteractBell(bell), DefaultWaitTimeoutMs);

            if (TryFinishWait(out var failed))
            {
                if (failed)
                {
                    ErrorOut("Timed out interacting with the summoning bell.");
                    return;
                }

                CurrentState = State.WaitList;
            }
        }

        private static void TickWaitList()
        {
            if (!waitPending)
                BeginWait("WaitList", RetainerUi.RetainerListOpen, DefaultWaitTimeoutMs);

            if (TryFinishWait(out var failed))
            {
                if (failed)
                {
                    ErrorOut("Timed out waiting for the retainer list to open.");
                    return;
                }

                currentRetainer = workQueue.Dequeue();
                CurrentRetainerName = ResolveRetainerName(currentRetainer);
                CurrentState = State.SelectRetainer;
            }
        }

        private static void TickSelectRetainer()
        {
            if (currentRetainer == null)
            {
                ErrorOut("No retainer queued to select.");
                return;
            }

            var expectedName = CurrentRetainerName ?? string.Empty;

            if (!waitPending)
            {
                BeginWait("SelectRetainer", () =>
                {
                    // Success = the retainer's own menu (a SelectString) is open, i.e. the summon
                    // actually took. Do NOT use RetainerManager's "active retainer": it can still report
                    // a previously-summoned retainer, so it passes instantly without the menu opening and
                    // the run then stalls in OpenInventory waiting on a menu that never appeared.
                    if (RetainerUi.RetainerMenuReady())
                        return true;

                    RetainerUi.SelectRetainer(expectedName);
                    return false;
                }, DefaultWaitTimeoutMs);
            }

            if (TryFinishWait(out var failed))
            {
                if (failed)
                {
                    ErrorOut($"Timed out selecting retainer '{expectedName}'.");
                    return;
                }

                CurrentState = State.OpenInventory;
            }
        }

        private static void TickOpenInventory()
        {
            if (!waitPending)
                BeginWait("OpenInventory", () => RetainerUi.SelectEntrustWithdraw() == true, DefaultWaitTimeoutMs);

            if (TryFinishWait(out var failed))
            {
                if (failed)
                {
                    ErrorOut("Timed out opening the retainer's inventory (Entrust or withdraw items).");
                    return;
                }

                inventoryStableFrames = 0;
                CurrentState = State.WaitInventory;
            }
        }

        private static void TickWaitInventory()
        {
            if (!waitPending)
            {
                BeginWait("WaitInventory", () =>
                {
                    if (!RetainerUi.RetainerInventoryReady())
                    {
                        inventoryStableFrames = 0;
                        return false;
                    }

                    // N-frame stabilization delay before RetainerPage* are considered populated
                    // (PLAN.md Edge Cases; exact frame count is a needs-in-game-verification item).
                    inventoryStableFrames++;
                    return inventoryStableFrames >= StabilizationFrames;
                }, DefaultWaitTimeoutMs);
            }

            if (TryFinishWait(out var failed))
            {
                if (failed)
                {
                    ErrorOut("Timed out waiting for the retainer inventory to load.");
                    return;
                }

                EnterPulling();
                CurrentState = State.Pulling;
            }
        }

        /// <summary>
        /// Builds this retainer's pull queue on entry to <see cref="State.Pulling"/> (Task 6.3 "On
        /// entry to Pulling, snapshot the current retainer's picked slots via
        /// <see cref="RetainerScan.PickSlots"/>"). Calls <see cref="RetainerScan.PickSlots"/> exactly
        /// once with the whole retainer group (matching the flat (Page,Slot,ItemId,Qty) shape from
        /// Task 6.2), then separately re-derives each pick's Hq (via a same-instant
        /// <see cref="RetainerScan.LivePages"/> lookup — the tuple itself doesn't carry Hq) to
        /// attribute every pick back to the exact <see cref="RetrieveTarget"/> that needs it, which
        /// Task 6.5's per-target accounting requires.
        /// </summary>
        private static void EnterPulling()
        {
            ResetPullingState();

            if (currentRetainer == null)
                return;

            foreach (var target in currentRetainer)
                pulledByTarget[target] = 0;

            var hqBySlot = new Dictionary<(InventoryType Container, int SlotIndex), bool>();
            foreach (var slot in RetainerScan.LivePages())
                hqBySlot[(slot.Container, slot.SlotIndex)] = slot.IsHq;

            foreach (var pick in RetainerScan.PickSlots(currentRetainer))
            {
                var hq = hqBySlot.TryGetValue((pick.Page, pick.Slot), out var isHq) && isHq;
                var target = currentRetainer.FirstOrDefault(t => t.ItemId == pick.ItemId && t.Hq == hq);
                if (target == null)
                    continue; // defensive: every live slot should attribute to exactly one target

                pullQueue.Enqueue(new PendingPull(target, pick.ItemId, hq, pick.Qty));
            }

            PullsTotalThisRetainer = pullQueue.Count;
        }

        /// <summary>
        /// The frame-spaced native-command pull loop (Task 6.3): one <see cref="RetainerCommandInvoker.Retrieve"/>
        /// invoke (or one landing-verification check) per <see cref="Configuration.MoveTickGap"/>
        /// frames, driven directly here — NOT the kit <c>MoveQueue</c> (that is <c>MoveItemSlot</c>-based
        /// and used only for InventoryCleaner's player-internal moves; see PLAN.md Architecture
        /// Decision).
        /// </summary>
        private static void TickPulling()
        {
            pullFrameCounter++;
            if (pullFrameCounter < nextActionFrame)
                return;

            if (pullPhase == PullPhase.Ready)
                TickPullingReady();
            else
                TickPullingVerify();
        }

        private static void TickPullingReady()
        {
            if (pullQueue.Count == 0)
            {
                FinalizeCurrentRetainerResult();
                AdvancePastPulling();
                return;
            }

            // (a) Bags full mid-retainer: stop this retainer's remaining pulls and record the
            // shortfall (PLAN.md Task 6.3 / Edge Cases "No free player bag slot").
            if (InventoryScan.FreeSlotsInBag() <= 0)
            {
                DrainRemainingAsShort("Player bags are full.");
                FinalizeCurrentRetainerResult();
                AdvancePastPulling();
                return;
            }

            var pending = pullQueue.Peek();

            // (b) Re-scan LivePages to relocate the next target's current slot — never reuse a
            // cached slot index, the container shifts after each pull (PLAN.md Task 6.3). Exclude
            // deadSlots so a stack that already failed its landing checks isn't re-selected ahead of
            // a distinct, still-untouched stack of the same item.
            var slot = RetainerScan.LivePages().FirstOrDefault(s =>
                s.ItemId == pending.ItemId && s.IsHq == pending.Hq && !deadSlots.Contains((s.Container, s.SlotIndex)));
            if (slot == null)
            {
                // AT cache drift: the item this pick was planned against is no longer on the
                // retainer (PLAN.md Edge Cases "AT cache stale") — recorded short/failed, not an
                // abort. No invoke was sent, so no frame-gap wait is needed before retrying the loop.
                targetFailureReasons[pending.Target] = "Item no longer found on the retainer.";
                pullQueue.Dequeue();
                return;
            }

            if (!RetainerCommandInvoker.Retrieve((uint)slot.SlotIndex, slot.Container))
            {
                var reason = RetainerCommandInvoker.Bound
                    ? "Lost the retainer inventory mid-pull (addon closed or Retainer agent inactive)."
                    : "The retainer retrieve command is unavailable (signature not bound for this game version).";
                ErrorOut(reason);
                return;
            }

            activePull = pending;
            activeSlot = (slot.Container, slot.SlotIndex);
            activePreQty = slot.Qty;
            landingDeadlineFrame = pullFrameCounter + LandingTimeoutFrames;
            pullPhase = PullPhase.AwaitingLanding;
            nextActionFrame = pullFrameCounter + Math.Max(1, Plugin.C.MoveTickGap);
        }

        /// <summary>
        /// (c) Verifies the last invoked pull landed by re-scanning and confirming the source slot's
        /// qty dropped / the item is gone (PLAN.md Task 6.3). No infinite retry: once
        /// <see cref="LandingTimeoutFrames"/> elapse the pick is recorded failed and the loop advances
        /// (PLAN.md Edge Cases "Pull didn't land").
        /// </summary>
        private static void TickPullingVerify()
        {
            var landed = !RetainerScan.LivePages().Any(s =>
                s.Container == activeSlot.Container &&
                s.SlotIndex == activeSlot.SlotIndex &&
                s.ItemId == activePull!.ItemId &&
                s.Qty >= activePreQty);

            if (landed)
            {
                pulledByTarget[activePull!.Target] = pulledByTarget.GetValueOrDefault(activePull.Target) + activePreQty;

                // Live per-stack feedback in chat as each pull lands.
                var hqTag = activePull.Hq ? " " + SeIconChar.HighQuality.ToIconString() : string.Empty;
                Plugin.ChatGui.Print($"[RetainerReach] Retrieved {activePreQty}x {ItemSheet.Name(activePull.ItemId)}{hqTag}");

                pullQueue.Dequeue();
                activePull = null;
                pullPhase = PullPhase.Ready;
                nextActionFrame = pullFrameCounter + Math.Max(1, Plugin.C.MoveTickGap);
                return;
            }

            if (pullFrameCounter >= landingDeadlineFrame)
            {
                targetFailureReasons[activePull!.Target] = "A retrieve did not land within the timeout.";
                // The slot is still live (its qty never dropped); mark it dead so the loop doesn't
                // re-select it for the next same-item pick instead of a distinct untouched stack.
                deadSlots.Add(activeSlot);
                pullQueue.Dequeue();
                activePull = null;
                pullPhase = PullPhase.Ready;
            }

            nextActionFrame = pullFrameCounter + Math.Max(1, Plugin.C.MoveTickGap);
        }

        /// <summary>Marks every not-yet-attempted pick's target with <paramref name="reason"/> (Task 6.3(a) bag-full shortfall) without touching qty already recorded as pulled.</summary>
        private static void DrainRemainingAsShort(string reason)
        {
            foreach (var pending in pullQueue)
                targetFailureReasons[pending.Target] = reason;

            pullQueue.Clear();
        }

        /// <summary>
        /// Converts this retainer's <see cref="pulledByTarget"/>/<see cref="targetFailureReasons"/>
        /// into a <see cref="RetainerResult"/> and appends it to <see cref="Results"/> (Task 6.5).
        /// Classification: <see cref="TargetOutcome.Moved"/> when pulled &gt;= requested (whole-stack
        /// overshoot still counts as moved), <see cref="TargetOutcome.Short"/> when some but not all
        /// landed, <see cref="TargetOutcome.Failed"/> when nothing landed at all.
        /// </summary>
        private static void FinalizeCurrentRetainerResult()
        {
            if (currentRetainer == null)
                return;

            var targets = new List<TargetResult>();
            foreach (var target in currentRetainer)
            {
                var pulled = pulledByTarget.GetValueOrDefault(target);
                targetFailureReasons.TryGetValue(target, out var reason);

                var outcome = pulled >= target.Qty
                    ? TargetOutcome.Moved
                    : pulled > 0
                        ? TargetOutcome.Short
                        : TargetOutcome.Failed;

                targets.Add(new TargetResult(target.ItemId, target.Hq, target.Qty, pulled, outcome, reason));
            }

            results.Add(new RetainerResult(CurrentRetainerName ?? currentRetainer.Key.ToString(), currentRetainer.Key, targets));
            currentRetainerFinalized = true;
        }

        /// <summary>
        /// The Pulling exit point once a retainer's pull queue has drained or its bags filled (the two
        /// call sites in <see cref="TickPullingReady"/>, immediately after <see cref="FinalizeCurrentRetainerResult"/>):
        /// the Task 6.4 transition to <see cref="State.CloseInventory"/>.
        /// </summary>
        private static void AdvancePastPulling()
        {
            CurrentState = State.CloseInventory;
        }

        // Phase 5 non-mutating close+advance transitions (Task 5.2 UI wrappers only). Task 6.4
        // reviewed these unchanged: the accounting hand-off (Task 6.5) happens in
        // FinalizeCurrentRetainerResult(), called from TickPullingReady() at the exact
        // Pulling -> CloseInventory transition, so these two methods needed no modification.
        private static void TickCloseInventory()
        {
            if (!waitPending)
                BeginWait("CloseInventory", () => RetainerUi.CloseRetainerInventory() == true, DefaultWaitTimeoutMs);

            if (TryFinishWait(out var failed))
            {
                if (failed)
                {
                    ErrorOut("Timed out closing the retainer inventory.");
                    return;
                }

                CurrentState = State.NextRetainer;
            }
        }

        // Phase 5 non-mutating close+advance transition (Task 5.2 UI wrappers only, no pull logic);
        // Task 6.4 reviewed this unchanged — see the note above TickCloseInventory().
        private static void TickNextRetainer()
        {
            if (workQueue.Count == 0)
            {
                if (!waitPending)
                    BeginWait("CloseRetainerList", () => RetainerUi.CloseRetainerList() == true, DefaultWaitTimeoutMs);

                if (TryFinishWait(out var failed))
                {
                    RetainerUi.CloseRetainerAgent();
                    currentRetainer = null;
                    CurrentRetainerName = null;

                    if (failed)
                    {
                        ErrorOut("Timed out closing the retainer list.");
                    }
                    else
                    {
                        // Own-bell run finished cleanly — hand AutoRetainer back its scheduler.
                        AutoRetainerIpc.RestoreAutoRetainer();
                        CurrentState = State.Done;
                    }
                }

                return;
            }

            // More retainers to visit. After CloseInventory we're back at THIS retainer's menu
            // (SelectString) with the RetainerList closed, so we can't just select the next retainer —
            // first quit this retainer's menu to reopen the list. Success = the list is back AND the
            // old menu is gone (else the next SelectRetainer would instantly false-pass on the stale
            // menu via RetainerMenuReady).
            if (!waitPending)
            {
                BeginWait("BackToList", () =>
                {
                    if (RetainerUi.RetainerListOpen() && !RetainerUi.RetainerMenuReady())
                        return true;

                    RetainerUi.QuitRetainerMenu();
                    return false;
                }, DefaultWaitTimeoutMs);
            }

            if (TryFinishWait(out var backFailed))
            {
                if (backFailed)
                {
                    ErrorOut("Timed out returning to the retainer list for the next retainer.");
                    return;
                }

                currentRetainer = workQueue.Dequeue();
                CurrentRetainerName = ResolveRetainerName(currentRetainer);
                currentRetainerFinalized = false;
                CurrentState = State.SelectRetainer;
            }
        }

        /// <summary>
        /// Starts a bounded, retried wait: <paramref name="condition"/> is invoked via the internal
        /// <see cref="TaskManager"/> (its own Framework.Update subscription) once per frame until it
        /// returns true or <paramref name="timeoutMs"/> elapses (AbortOnTimeout=true), per PLAN.md
        /// Task 5.5 ("Each wait uses TaskManager TimeLimitMS with AbortOnTimeout=true -&gt; State.Error").
        /// </summary>
        private static void BeginWait(string name, Func<bool> condition, int timeoutMs)
        {
            waitPending = true;
            waitFailed = false;

            var config = new TaskManagerConfiguration(timeLimitMS: timeoutMs, abortOnTimeout: true, showError: false)
            {
                OnTaskTimeout = OnWaitTimeout,
            };
            tasks.Enqueue(condition, name, config);
        }

        private static void OnWaitTimeout(TaskManagerTask task, ref long remainingTimeMs)
        {
            // The internal TaskManager's wait clock is wall-clock and runs independently of Tick()'s
            // Guards.SafeToAct() pause. If the timeout fires while we're legitimately paused (a long
            // cutscene / quest event / loading), that's not a stuck wait — grant more time rather than
            // failing, so a normal mid-run interruption doesn't trip a spurious State.Error.
            if (!Guards.SafeToAct())
            {
                remainingTimeMs = DefaultWaitTimeoutMs;
                return;
            }

            waitFailed = true;
        }

        /// <summary>
        /// True once the wait started by <see cref="BeginWait"/> has finished (success or timeout);
        /// <paramref name="failed"/> reflects whether it timed out. False while still in progress.
        /// </summary>
        private static bool TryFinishWait(out bool failed)
        {
            failed = false;

            if (!waitPending || tasks.IsBusy)
                return false;

            waitPending = false;
            failed = waitFailed;
            return true;
        }

        private static void AbortWithMessage(string message)
        {
            // Reached only from the own-bell path (TickTargetBell) after Start() suppressed AR.
            AutoRetainerIpc.RestoreAutoRetainer();

            ErrorMessage = message;
            Plugin.ChatGui.PrintError($"[RetainerReach] {message}");
            CurrentState = State.Aborted;
        }

        private static void ErrorOut(string message)
        {
            tasks.Abort();
            waitPending = false;
            ResetPullingState();

            AutoRetainerIpc.RestoreAutoRetainer();

            ErrorMessage = message;
            Plugin.ChatGui.PrintError($"[RetainerReach] {message}");

            CloseOpenRetainerUi();
            CurrentState = State.Error;
        }
    }
}
