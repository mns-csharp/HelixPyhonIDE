using System.Windows.Forms;

namespace HelixPythonNamespace.Terminal;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string directory = string.Empty;
        string command = string.Empty;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == "--working-directory" && i + 1 < arguments.Length)
                directory = arguments[++i];
            else if (arguments[i] == "--command" && i + 1 < arguments.Length)
                command = arguments[++i];
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Application.Run(new TerminalPopupForm(directory, command));
    }
}
