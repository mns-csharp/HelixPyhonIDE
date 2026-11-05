using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 276: the integrated terminal now offers Git Bash (when installed) alongside cmd / PowerShell
    /// / python. Guards that ShellExe maps "bash" to a real bash.exe when present, and that the known
    /// shells are unaffected.
    /// </summary>
    [TestClass]
    public class TerminalShellTests
    {
        [TestMethod]
        public void Terminal_AutoStartsOnShow_AndKillsShellOnDispose()
        {
            FieldInfo sessionField = typeof(TerminalPanel).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(sessionField, "_session field expected");

            var panel = new TerminalPanel { Dock = DockStyle.Fill };
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000); // off-screen so nothing flashes
                form.ShowInTaskbar = false;
                form.Size = new Size(500, 320);
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                Assert.IsNotNull(sessionField.GetValue(panel),
                    "the shell should auto-start when the terminal becomes visible");

                form.Close(); // disposes the panel → StopSession must kill the shell
                Assert.IsNull(sessionField.GetValue(panel),
                    "the shell session must be stopped on dispose (no orphaned process)");
            }
        }

        private static string Invoke(string method, params object[] args)
        {
            MethodInfo m = typeof(TerminalPanel).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, method + " expected");
            return (string)m.Invoke(null, args);
        }

        [TestMethod]
        public void ShellExe_MapsKnownShells()
        {
            Assert.AreEqual("powershell.exe", Invoke("ShellExe", "powershell"));
            Assert.AreEqual("python.exe", Invoke("ShellExe", "python"));
            Assert.IsNull(Invoke("ShellExe", "cmd"), "cmd → null (engine defaults to %ComSpec%)");
        }

        [TestMethod]
        public void SetWorkingDirectory_UpdatesTheDirTheSessionStartsIn()
        {
            using (var panel = new TerminalPanel())
            {
                FieldInfo wd = typeof(TerminalPanel).GetField("_workingDir", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(wd, "_workingDir field expected");

                panel.SetWorkingDirectory(@"C:\some\project");
                Assert.AreEqual(@"C:\some\project", (string)wd.GetValue(panel),
                    "SetWorkingDirectory must set the dir the shell starts in");

                panel.SetWorkingDirectory(null);
                Assert.IsNull((string)wd.GetValue(panel), "null falls back to the process CWD");
            }
        }

        [TestMethod]
        public void ShellExe_Bash_ResolvesToRealBash_WhenInstalled()
        {
            string bash = Invoke("FindBash");
            if (string.IsNullOrEmpty(bash))
            {
                Assert.Inconclusive("Git Bash not installed — the bash shell option isn't offered.");
                return;
            }
            Assert.IsTrue(File.Exists(bash), "FindBash must return an existing bash.exe: " + bash);
            Assert.AreEqual(bash, Invoke("ShellExe", "bash"), "ShellExe(\"bash\") must equal FindBash()");
        }
    }
}
