using System;
using ECommons.Reflection;

namespace RetainerReach.Ipc
{
    /// <summary>
    /// The one piece of AutoRetainer integration RetainerReach keeps: while a RetainerReach own-bell
    /// run is active, suppress AutoRetainer's autonomous scheduler + Talk auto-clicker so the two
    /// plugins don't both drive the summoning-bell / retainer UI and collide. This is NOT a
    /// piggyback/postprocess integration — RetainerReach always rings its own bell; it just tells AR
    /// to stand down for the duration and restores AR exactly as it found it afterwards.
    /// </summary>
    public static class AutoRetainerIpc
    {
        private const string InternalName = "AutoRetainer";
        private const string GetSuppressed = "AutoRetainer.GetSuppressed";
        private const string SetSuppressed = "AutoRetainer.SetSuppressed";

        /// <summary>True when the AutoRetainer plugin (InternalName "AutoRetainer") is installed and loaded.</summary>
        public static bool Installed
            => DalamudReflector.TryGetDalamudPlugin(InternalName, out _, false, true);

        /// <summary>
        /// AutoRetainer's <c>Suppressed</c> value captured when RetainerReach suppressed it for an
        /// own-bell run; null when we have not suppressed it. Lets <see cref="RestoreAutoRetainer"/>
        /// put AR back exactly as the user had it — and never clear a suppression the user set.
        /// </summary>
        private static bool? suppressedByUs;

        /// <summary>
        /// Halts AutoRetainer's autonomous scheduler and its <c>Talk</c> auto-clicker (both gate on
        /// AR's <c>IPC.Suppressed</c> — <c>SchedulerMain.PluginEnabled</c> / <c>MiniTA.Tick</c>) for
        /// the duration of a RetainerReach own-bell run, so the two plugins don't drive the
        /// summoning-bell / retainer UI on the same frames and fight over it. Remembers AR's prior
        /// Suppressed state; idempotent; a no-op when AR isn't installed. Paired with
        /// <see cref="RestoreAutoRetainer"/> on every own-bell run exit.
        /// </summary>
        public static void SuppressAutoRetainer()
        {
            try
            {
                if (!Installed || suppressedByUs != null)
                    return;

                var prior = Plugin.PluginInterface.GetIpcSubscriber<bool>(GetSuppressed).InvokeFunc();
                suppressedByUs = prior;
                if (!prior)
                    Plugin.PluginInterface.GetIpcSubscriber<bool, object>(SetSuppressed).InvokeAction(true);
            }
            catch (Exception ex)
            {
                // On any IPC failure forget the capture, so RestoreAutoRetainer won't later toggle AR
                // off the back of a half-applied state.
                suppressedByUs = null;
                Plugin.Logger.Error(ex, "[RetainerReach] Failed to suppress AutoRetainer for an own-bell run.");
            }
        }

        /// <summary>
        /// Restores AutoRetainer after an own-bell run: clears the suppression only if
        /// <see cref="SuppressAutoRetainer"/> is the one that set it (AR's prior value was
        /// un-suppressed), leaving a suppression the user toggled themselves untouched. Idempotent —
        /// safe to call from every run-exit path and defensively at run start / plugin dispose.
        /// </summary>
        public static void RestoreAutoRetainer()
        {
            var prior = suppressedByUs;
            suppressedByUs = null;

            // null = never suppressed by us; true = user already had it suppressed → leave as-is.
            if (prior != false)
                return;

            try
            {
                if (Installed)
                    Plugin.PluginInterface.GetIpcSubscriber<bool, object>(SetSuppressed).InvokeAction(false);
            }
            catch (Exception ex)
            {
                Plugin.Logger.Error(ex, "[RetainerReach] Failed to restore AutoRetainer after an own-bell run.");
            }
        }
    }
}
