namespace HelixPythonNamespace.Editor.Completion
{
    using System.Drawing;
    using System.Windows.Forms;

    [System.ComponentModel.DesignerCategory("Form")]
    internal sealed partial class NoActivateForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        internal NoActivateForm() { InitializeComponent(); }
        internal Label TitleLabel { get { return titleLabel; } }
        internal Label BodyLabel { get { return bodyLabel; } }
        internal Panel BodyPanel { get { return bodyPanel; } }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                value.ExStyle = HelixPythonNamespace.Core.TestEnvironment.ExStyle(value.Style, value.ExStyle);   // test runs only
                return value;
            }
        }
        protected override void OnHandleCreated(System.EventArgs e)
        {
            base.OnHandleCreated(e);
            HelixPythonNamespace.Core.TestEnvironment.HandleCreated(Handle, CreateParams.Style);   // alpha 0 in a test run
        }
        private void OnBorderPaint(object sender, PaintEventArgs e)
        {
            using (var pen = new Pen(Color.FromArgb(180, 180, 180))) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
