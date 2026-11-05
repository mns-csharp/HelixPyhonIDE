namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Reflection;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

    [System.ComponentModel.DesignerCategory("Form")]

    // Read-only "About" box: shows version/build/runtime info and a clickable link to the repo.
    public sealed partial class AboutDialog : LunaForm
    {
        private const string RepoUrl = "https://github.com/mns-csharp/HelixStudio";

        public AboutDialog()
        {
            InitializeComponent();
            // Populate the info labels from assembly/runtime metadata at construction time.
            versionLabel.Text = string.Format("Version {0}", GetVersionString());
            buildLabel.Text = string.Format("Build: {0}", GetBuildDateString());
            runtimeLabel.Text = string.Format(".NET {0}", Environment.Version);
            copyrightLabel.Text = string.Format("© {0} {1}. All rights reserved.", DateTime.Now.Year, MainForm.ProductName);
            repositoryLinkLabel.Text = RepoUrl;
        }

        // Open the repository URL in the default browser; swallow any shell/launch failure silently.
        private void OnRepositoryLinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try { Process.Start(RepoUrl); } catch { }
        }


        // Return Major.Minor.Build from the running assembly; "unknown" on any failure.
        private static string GetVersionString()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                if (v == null) return "unknown";
                return string.Format("{0}.{1}.{2}", v.Major, v.Minor, v.Build);
            }
            catch { return "unknown"; }
        }

        // Approximate the build date from the assembly file's last-write timestamp.
        private static string GetBuildDateString()
        {
            try
            {
                string path = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
                }
            }
            catch { }
            return "unknown";
        }
    }
}
