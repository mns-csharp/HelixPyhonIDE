namespace HelixPythonNamespace.AddIns.Kit
{
    using System;
    using System.Drawing;

    /// <summary>
    /// What a design surface has to say about itself — "6 layers · shapes OK · 2 import note(s)",
    /// "5 terms, 3 movers, 3 observers", "Saved folding_run.py", "Run refused — see Output".
    ///
    /// <para><b>Why this is not a Label any more.</b> Both designers used to paint that line onto
    /// their own top bar, beside the Framework combo and the Generate button. A toolbar is a row of
    /// things you can DO; status is the state you are in, and this product already has one place
    /// for that — the status bar along the bottom, where the caret position, the indentation, the
    /// line endings and the compile counts all live. Two designers each inventing a private status
    /// area meant three different places to look depending on which tab was in front, and the one
    /// on the toolbar was the one that had to be re-measured every time somebody added a
    /// button.</para>
    ///
    /// <para>So the status text is a value now, not a widget. The designer sets it exactly as it
    /// used to set a Label — <c>.Text</c>, <c>.ForeColor</c> — and <c>MainForm</c> renders it in the
    /// status bar for whichever surface is in front. The designer does not know where it is shown,
    /// which is the point: it cannot go stale when the layout changes, and a surface that is not on
    /// screen cannot overwrite the line belonging to the one that is.</para>
    /// </summary>
    public sealed class DesignerStatus
    {
        private string _text = string.Empty;
        private Color _foreColor = SystemColors.ControlText;
        private string _toolTipText = string.Empty;

        /// <summary>Raised when any of the three change, so the host can re-render. Raised once per
        /// change and only on a real change: assigning the same summary after every mouse-move
        /// would otherwise invalidate the status bar continuously.</summary>
        public event EventHandler Changed;

        public string Text
        {
            get { return _text; }
            set
            {
                string text = value ?? string.Empty;
                if (text == _text) return;
                _text = text;
                Raise();
            }
        }

        /// <summary>The ink the line is written in ON A LIGHT BACKGROUND. The status bar is dark, so
        /// it is a severity signal rather than a literal colour — see
        /// <c>MainForm.RenderDesignerStatus</c>, which maps it onto something readable there.</summary>
        public Color ForeColor
        {
            get { return _foreColor; }
            set
            {
                if (value == _foreColor) return;
                _foreColor = value;
                Raise();
            }
        }

        /// <summary>The longer explanation behind the summary — for the CG canvas, the findings that
        /// belong to the design as a whole rather than to any one node.</summary>
        public string ToolTipText
        {
            get { return _toolTipText; }
            set
            {
                string text = value ?? string.Empty;
                if (text == _toolTipText) return;
                _toolTipText = text;
                Raise();
            }
        }

        private void Raise()
        {
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Implemented by a top-level design surface that has a status line. <c>MainForm</c> asks the
    /// tab in front for this and shows nothing when the tab has none — an editor or a terminal does
    /// not get the last canvas's summary left hanging under it.
    /// </summary>
    public interface IDesignSurfaceStatus
    {
        DesignerStatus DesignStatus { get; }
    }
}
