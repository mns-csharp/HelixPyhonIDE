namespace HelixPythonNamespace.App
{
    using System;
    using System.Windows.Forms;

    /// <summary>
    /// Makes the Toolbox and Properties panes follow whichever design surface is in front.
    ///
    /// <para><b>The bug this exists for.</b> Each designer claimed the shared Toolbox when it
    /// opened and gave it back only when its own tab closed. Nothing happened on a tab SWITCH — so
    /// selecting another design surface left the previous designer's palette sitting in the
    /// Toolbox, offering controls unrelated to the active canvas.
    /// Reported from the running IDE with exactly that screenshot. Whichever designer had opened
    /// most recently owned the Toolbox for the rest of the session.</para>
    ///
    /// <para><b>One router rather than three.</b> Every designer knowing when to let go means three
    /// implementations of the same rule, and the one that forgets is the one that wins. Selection
    /// changes in one place, so the decision belongs in one place: release whoever is holding the
    /// panes, then let the newly selected surface claim them. Release before claim, because
    /// <c>ClaimToolPanel</c> stashes the content it displaces and <c>ReleaseToolPanel</c> restores
    /// it — doing it the other way round stashes a pane that was already stashed.</para>
    ///
    /// <para><b>A non-designer tab gets no Toolbox at all.</b> The editor, the terminal and the
    /// Development Environment tab have no tools, and leaving the last designer's palette up beside
    /// them says they do. Releasing restores whatever the shell had in those panes before a designer
    /// borrowed them — for Properties that is the class diagram's own property grid, which is why
    /// the stash exists.</para>
    /// </summary>
    public partial class MainForm
    {
        private bool _routingToolbox;

        /// <summary>Hooked to the top-level strip's selection. Idempotent and re-entrancy guarded:
        /// claiming and releasing move tab pages about, which can raise selection changes of their
        /// own.</summary>
        private void OnActiveTabChangedForToolbox(object sender, EventArgs e)
        {
            SyncToolboxToActiveTab();
        }

        /// <summary>
        /// Hands the shared panes to the design surface in front.
        ///
        /// <para><b>Claim only; never release.</b> That is the whole of it, and the reason is that
        /// <c>ClaimToolPanel</c> already stashes whatever content it displaces and
        /// <c>ReleaseToolPanel</c> already puts it back — the ownership hand-off was solved, and
        /// the only thing missing was anyone doing it on a tab switch. A first attempt here also
        /// released the previous owner before claiming, which double-handled panes already managed
        /// by the designers' close paths and broke tab reordering.</para>
        ///
        /// <para>A non-designer tab therefore leaves the last palette where it is. It is not
        /// nothing-to-show versus the-wrong-thing: the editor has no palette of its own, so the
        /// alternative is an empty Toolbox tab that flickers in and out as the user moves between a
        /// canvas and a file. Retiring a palette is the owning designer's business, and it already
        /// does it when its tab or its document closes.</para>
        /// </summary>
        internal void SyncToolboxToActiveTab()
        {
            if (_routingToolbox || _mainTabControl == null) return;
            _routingToolbox = true;
            try
            {
                // An add-in's tab claims its own panes in its Activated callback. A designer with
                // an inner strip of documents (the GUI Designer Canvas add-in) also re-claims them
                // itself when its inner selection changes.
                ActivateAddInTab(_mainTabControl.SelectedTab);
            }
            finally
            {
                _routingToolbox = false;
            }

            // The status line moves with the palette. Both answer "which surface is in front?", and
            // splitting that question over two handlers is how one of them ends up a tab behind.
            SyncDesignerStatusToActiveTab();
        }

        /// <summary>
        /// Subscribes the router. Called from every designer's tab registration, and idempotent so
        /// that however many of them register, the strip carries exactly one handler.
        /// </summary>
        private void EnsureToolboxRouting()
        {
            if (_mainTabControl == null) return;
            _mainTabControl.SelectedIndexChanged -= OnActiveTabChangedForToolbox;
            _mainTabControl.SelectedIndexChanged += OnActiveTabChangedForToolbox;
        }
    }
}
