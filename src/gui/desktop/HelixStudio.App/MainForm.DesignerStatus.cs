namespace HelixPythonNamespace.App
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.App.Controls;
    using HelixPythonNamespace.AddIns.Kit;

    /// <summary>
    /// Shows the active design surface's status line in the shell's status bar.
    ///
    /// <para>The status bar keeps the active design surface's summary in one consistent location,
    /// alongside editor and build status.</para>
    ///
    /// <para><b>One slot, not one per designer.</b> Exactly one design surface is in front at a
    /// time, so they share a slot and the router decides whose text is in it. A tab with no status
    /// of its own — an editor, the terminal, the Development Environment — empties it rather than
    /// leaving the last canvas's summary sitting under an unrelated document, which is the same
    /// rule the Toolbox follows and for the same reason.</para>
    /// </summary>
    public partial class MainForm
    {
        /// <summary>The shared slot. Left-aligned so it reads as a sentence next to the general
        /// status text, with a divider on its left so the two do not run together.</summary>
        private ToolStripStatusLabel _designerStatusLabel;

        /// <summary>The surface whose <c>Changed</c> event we are currently listening to. Kept so
        /// the subscription can be moved rather than accumulated — a designer that is rebuilt on
        /// every tab switch would otherwise leave a handler behind each time.</summary>
        private DesignerStatus _subscribedDesignerStatus;

        private void BuildDesignerStatusSlot()
        {
            _designerStatusLabel = new ToolStripStatusLabel(string.Empty)
            {
                ForeColor = Color.White,
                BorderSides = ToolStripStatusLabelBorderSides.Left,
                BorderStyle = Border3DStyle.Etched,
                Visible = false,
            };
            int at = _statusStrip.Items.IndexOf(_statusLabel);
            _statusStrip.Items.Insert(at + 1, _designerStatusLabel);
        }

        /// <summary>
        /// Puts the in-front surface's line in the slot, and empties it when there is none.
        ///
        /// <para>Called from the same place the Toolbox is routed, so a tab switch moves both
        /// together and neither can be left showing the other tab's contents.</para>
        /// </summary>
        internal void SyncDesignerStatusToActiveTab()
        {
            DesignerStatus status = ActiveDesignerStatus();

            if (!ReferenceEquals(status, _subscribedDesignerStatus))
            {
                if (_subscribedDesignerStatus != null)
                    _subscribedDesignerStatus.Changed -= OnDesignerStatusChanged;
                _subscribedDesignerStatus = status;
                if (_subscribedDesignerStatus != null)
                    _subscribedDesignerStatus.Changed += OnDesignerStatusChanged;
            }

            RenderDesignerStatus(status);
        }

        private void OnDesignerStatusChanged(object sender, EventArgs e)
        {
            // Only the surface in front may write here. A background designer that revalidates —
            // the CG canvas does it on a timer-free but still unsolicited path when a design is
            // loaded — must not overwrite the line belonging to the tab the user is looking at.
            DesignerStatus active = ActiveDesignerStatus();
            if (ReferenceEquals(sender, active)) RenderDesignerStatus(active);
        }

        private void RenderDesignerStatus(DesignerStatus status)
        {
            if (_designerStatusLabel == null) return;

            if (status == null || string.IsNullOrEmpty(status.Text))
            {
                _designerStatusLabel.Visible = false;
                _designerStatusLabel.Text = string.Empty;
                _designerStatusLabel.ToolTipText = string.Empty;
                return;
            }

            _designerStatusLabel.Text = status.Text;
            _designerStatusLabel.ToolTipText = status.ToolTipText ?? string.Empty;
            _designerStatusLabel.ForeColor = StatusBarInk(status.ForeColor);
            _designerStatusLabel.Visible = true;
        }

        /// <summary>
        /// Translates a designer's ink into something legible on the dark status strip.
        ///
        /// <para>The designers chose their colours for a light top bar: firebrick for a refusal,
        /// a dark amber for a warning, near-black for ordinary text. Two of those are unreadable
        /// against the status bar's dark blue, so the severity survives and the exact colour does
        /// not — a red-ish ink becomes the same salmon the compile-error count already uses here,
        /// an amber one becomes the amber that reads on dark, and everything else is white.</para>
        /// </summary>
        private static Color StatusBarInk(Color ink)
        {
            if (ink.R > 120 && ink.R > ink.G + 60 && ink.R > ink.B + 60)
                return Color.FromArgb(255, 150, 150);
            if (ink.R > 110 && ink.G > 70 && ink.B < 90)
                return Color.FromArgb(255, 210, 120);
            return Color.White;
        }

        /// <summary>The status of the design surface in front, or null when the tab in front is not
        /// one. The GUI Designer has no summary line of its own, so it falls through to null.</summary>
        private DesignerStatus ActiveDesignerStatus()
        {
            if (_mainTabControl == null) return null;
            TabPage selected = _mainTabControl.SelectedTab;
            if (selected == null) return null;

            // An add-in tab: the first control on it that reports a status line.
            if (IsAddInTab(selected))
                foreach (Control control in selected.Controls)
                {
                    DesignerStatus status = StatusOf(control);
                    if (status != null) return status;
                }
            return null;
        }

        private static DesignerStatus StatusOf(Control surface)
        {
            var source = surface as IDesignSurfaceStatus;
            if (source == null || surface.IsDisposed) return null;
            return source.DesignStatus;
        }
    }
}
