namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// Floating sizable-tool-window hosting a MinimapControl. Pattern mirrors
    /// ToolsPaletteForm — hide on X (so the menu toggle state stays consistent),
    /// persist location via MainForm.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class MinimapForm : LunaForm
    {
        private MinimapControl _minimap;

        public MinimapForm()
        {
            InitializeComponent();
        }

        /// <summary>Bind to (or clear) the active DiagramCanvas.</summary>
        public void SetCanvas(DiagramCanvas canvas)
        {
            _minimap.SetCanvas(canvas);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }
    }
}
