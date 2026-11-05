namespace HelixPythonNamespace.App
{
    using System;
    using System.Windows.Forms;

    /// <summary>
    /// What the running IDE does when somebody launches it again.
    ///
    /// <para>The second process does not open a window — see <see cref="SingleInstance"/> for why
    /// two shells cannot safely share one settings directory. It broadcasts instead, and this is
    /// the half that listens: come forward, and open whatever that launch was asked to open.</para>
    ///
    /// <para><b>Coming forward is the whole point.</b> The case that matters is an IDE hidden in
    /// the notification area: the user clicks the shortcut again, a second process starts, finds
    /// the first, and exits. If the first did not then show itself, the user would have pressed
    /// the icon and watched nothing happen at all — which is worse than the second window they
    /// were going to get before.</para>
    /// </summary>
    public partial class MainForm
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == SingleInstance.ActivateMessage && SingleInstance.ActivateMessage != 0)
            {
                // Off the message, not in it. Restoring a window and loading a solution inside a
                // broadcast handler runs arbitrary work while every other top-level window in the
                // session is still being delivered the same message.
                BeginInvoke((MethodInvoker)ActivateFromSecondLaunch);
                return;
            }
            base.WndProc(ref m);
        }

        private void ActivateFromSecondLaunch()
        {
            if (IsDisposed) return;

            if (IsHiddenToTray) RestoreFromTray();
            else
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Show();
                Activate();
                BringToFront();
            }

            string path = PendingOpen.Take();
            if (!string.IsNullOrEmpty(path)) OpenShellPath(path);
        }
    }
}
