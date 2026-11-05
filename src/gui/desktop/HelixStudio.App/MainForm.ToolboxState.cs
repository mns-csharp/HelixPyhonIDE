namespace HelixPythonNamespace.App
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.App.Controls;
    using HelixPythonNamespace.AddIns.Kit;

    /// <summary>
    /// Remembering which toolbox categories each designer had folded away.
    ///
    /// <para><b>Why the shell owns this and not the designers.</b> The settings file is the shell's,
    /// and a designer that reached into it would be a designer that could only exist inside this
    /// application. The canvases already state their folds through
    /// <see cref="IToolboxCollapseState"/>; this is the one place that knows those folds are worth
    /// keeping and where to put them.</para>
    ///
    /// <para><b>Written on every change, not on shutdown.</b> Folding a category is a small,
    /// deliberate act, and a user who does it and then loses the IDE to a crash should not find the
    /// work undone. The settings manager already writes the whole file at once, so there is nothing
    /// to batch.</para>
    /// </summary>
    public partial class MainForm
    {
        /// <summary>The designer keys used in the settings file. Stable: changing one forgets the folds.</summary>
        internal const string ToolboxKeyCg = "cg";
        internal const string ToolboxKeyGui = "gui";

        /// <summary>
        /// Restores <paramref name="surface"/>'s folds and keeps them saved from then on.
        ///
        /// <para>Restore first, subscribe second. The other order would have the restore itself
        /// raise the change event and write back what was just read -- harmless today, and exactly
        /// the kind of loop that stops being harmless when someone adds a migration to the read.</para>
        /// </summary>
        internal void PersistToolboxCollapse(string key, IToolboxCollapseState surface)
        {
            if (surface == null || string.IsNullOrEmpty(key)) return;

            surface.CollapsedToolboxCategories = ReadToolboxCollapse(key);
            surface.ToolboxCollapseChanged += delegate
            {
                WriteToolboxCollapse(key, surface.CollapsedToolboxCategories);
            };
        }

        private IList<string> ReadToolboxCollapse(string key)
        {
            UserSettings settings = _settingsManager == null ? null : _settingsManager.Settings;
            if (settings == null || settings.ToolboxCollapsed == null) return new List<string>();

            List<string> folded;
            return settings.ToolboxCollapsed.TryGetValue(key, out folded) && folded != null
                ? new List<string>(folded)
                : new List<string>();
        }

        private void WriteToolboxCollapse(string key, IList<string> folded)
        {
            if (_settingsManager == null) return;
            UserSettings settings = _settingsManager.Settings;
            if (settings == null) return;

            if (settings.ToolboxCollapsed == null)
                settings.ToolboxCollapsed = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            settings.ToolboxCollapsed[key] = folded == null
                ? new List<string>() : new List<string>(folded);

            // Never let a settings write take the IDE down. A fold is a preference, and a disk that
            // refuses it is not a reason to lose the user's session.
            try { _settingsManager.Save(); }
            catch (Exception) { }
        }
    }
}
