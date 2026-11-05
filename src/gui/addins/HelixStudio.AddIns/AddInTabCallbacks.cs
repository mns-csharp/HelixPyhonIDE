namespace HelixPythonNamespace.AddIns
{
    using System;

    /// <summary>What the host tells an add-in about one of its registered tabs.</summary>
    public sealed class AddInTabCallbacks
    {
        /// <summary>The tab became the selected main tab.</summary>
        public Action Activated { get; set; }

        /// <summary>
        /// The tab stopped being the selected main tab: the user picked another tab, or the tab
        /// left the strip. Fires before the shell rearranges itself for the newly selected tab
        /// (notebook mode, for one), so a tab that changed the workbench while it was in front can
        /// put it back first.
        /// </summary>
        public Action Deactivated { get; set; }

        /// <summary>
        /// The tab was closed. Fires after the host's deferred re-check, so a tab that is only
        /// being reordered (removed and re-inserted) does not count as closed.
        /// </summary>
        public Action Removed { get; set; }
    }
}
