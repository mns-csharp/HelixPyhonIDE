using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.Terminal.App;
using HelixPythonNamespace.Terminal.App.Terminal;

namespace HelixPythonNamespace.Terminal;

internal sealed class TerminalPopupForm : Form
{
    private readonly TerminalControl _terminal;
    private readonly ShellHost _shell;

    public TerminalPopupForm(string workingDirectory, string command)
    {
        Text = "HelixPython Terminal";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 300);
        Size = new Size(980, 620);
        ShowInTaskbar = true;

        _terminal = new TerminalControl { Dock = DockStyle.Fill };
        Controls.Add(_terminal);
        _shell = new ShellHost(_terminal, workingDirectory);

        if (!string.IsNullOrWhiteSpace(command)) _terminal.QueueCommand(command);
        _terminal.TitleChanged += title => Text = string.IsNullOrWhiteSpace(title)
            ? "HelixPython Terminal"
            : title + " — HelixPython Terminal";
        _shell.ExitRequested += CloseFromShell;
        Shown += (_, _) =>
        {
            _shell.Start();
            _terminal.Focus();
        };
        FormClosed += (_, _) => _shell.Dispose();
    }

    private void CloseFromShell()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(Close)); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }
}
