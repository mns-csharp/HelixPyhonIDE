using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Tools ▸ Register TOML Files, and the double-click it exists to enable.
    ///
    /// The registration itself is NOT clicked here: the handler writes to the live
    /// <c>HKCU\Software\Classes</c>, and a suite that repoints the developer's .toml files would be
    /// a defect rather than coverage (the registry shapes are pinned in
    /// <c>HelixPythonNamespace.Core.Tests.TomlFileAssociationTests</c> against a scratch key instead). What
    /// this file proves is the half that lives in the IDE: the item is on the live menu with a
    /// handler attached, and the path Explorer would hand the process opens the whole SOLUTION.
    /// </summary>
    [TestClass]
    public class RegisterTomlFilesMenuTests
    {
        private static ToolStripMenuItem ToolsMenu(MainForm form)
        {
            return form.MainMenuStrip.Items.Cast<ToolStripItem>()
                .OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => (m.Text ?? string.Empty).Replace("&", string.Empty)
                                     .StartsWith("Tools", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void ToolsMenu_OffersRegisterTomlFiles_WithAHandlerAttached()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                ToolStripMenuItem tools = ToolsMenu(form);
                Assert.IsNotNull(tools, "the live menu bar has a Tools menu");

                var item = tools.DropDownItems.Cast<ToolStripItem>()
                    .OfType<ToolStripMenuItem>()
                    .FirstOrDefault(i => (i.Text ?? string.Empty).IndexOf("Register TOML",
                                          StringComparison.OrdinalIgnoreCase) >= 0);

                Assert.IsNotNull(item,
                    "Tools has no Register TOML item; got: " + string.Join(" | ",
                        tools.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text)));
                Assert.AreEqual("&Register TOML Files", item.Text);
                Assert.IsFalse(string.IsNullOrEmpty(item.ToolTipText), "the item explains what it changes");

                // An item with no Click subscriber looks identical on screen and does nothing.
                var click = typeof(ToolStripItem)
                    .GetField("EventClick", System.Reflection.BindingFlags.NonPublic
                                          | System.Reflection.BindingFlags.Static);
                Assert.IsNotNull(click, "ToolStripItem.EventClick key not found — WinForms internals moved");
                object key = click.GetValue(null);
                var events = typeof(System.ComponentModel.Component)
                    .GetProperty("Events", System.Reflection.BindingFlags.NonPublic
                                         | System.Reflection.BindingFlags.Instance)
                    .GetValue(item, null) as System.ComponentModel.EventHandlerList;
                Assert.IsNotNull(events[key], "the Register TOML item has no Click handler wired");
            }
        }

        [TestMethod]
        public void ToolsMenu_KeepsRegisterTomlVisible_WhateverTheActiveDocumentIs()
        {
            // Every other Tools item is gated by workspace/extension (OnToolsMenuOpening, which
            // hides what does not apply). This one must not be: a user with nothing open is exactly
            // the user who needs to associate .toml before double-clicking their first solution.
            using (var form = TestMainForm.CreateComposed())
            {
                var opening = typeof(MainForm).GetMethod("OnToolsMenuOpening",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(opening, "OnToolsMenuOpening expected");
                opening.Invoke(form, new object[] { form, EventArgs.Empty });

                ToolStripMenuItem item = MainForm.FindMenuItemByText(ToolsMenu(form), "Register TOML");
                Assert.IsNotNull(item, "Register TOML Files disappeared from the Tools menu");
                Assert.IsTrue(item.Available, "Register TOML Files must stay available with no solution open");
            }
        }

        [TestMethod]
        public void AShellHandedMemberManifest_OpensTheWholeSolution()
        {
            // The acceptance oracle for the feature: this is the exact call Explorer's double-click
            // produces (Program.Main -> QueueShellOpen -> OpenShellPath), given a MEMBER package's
            // manifest rather than the workspace root. "The entire solution loads" means the model
            // carries every member, not just the one that was clicked.
            string root = FixtureSolution.Path_("python", "python-test-sample-oop");
            string memberToml = Path.Combine(root, "lib_core", "pyproject.toml");
            Assert.IsTrue(File.Exists(memberToml), "fixture member manifest missing: " + memberToml);

            // NOT shown: the form is driven headlessly, the way FixtureSolution.OpenSolution drives
            // every other real-solution test. Showing it and then disposing while OpenSolutionAsync's
            // continuations are still creating controls takes the whole test host down
            // ("Dispose() cannot be called while doing CreateHandle()") — measured here.
            using (var form = TestMainForm.CreateComposed())
            {
                form.OpenShellPath(memberToml);

                // Same wait FixtureSolution.OpenSolution uses: OpenSolutionAsync is `async void`,
                // so its continuation only runs while this thread pumps.
                for (int i = 0; i < 2400 && form._currentSolution == null; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(25);
                }

                Assert.IsNotNull(form._currentSolution, "nothing opened for " + memberToml);
                Assert.AreEqual(Path.GetFullPath(Path.Combine(root, "pyproject.toml")),
                                Path.GetFullPath(form._currentSolution.FilePath), true,
                                "the WORKSPACE ROOT must open, not the clicked member");
                Assert.IsTrue(form._currentSolution.Projects.Count > 1,
                    "a one-project solution means the member opened alone; got " +
                    form._currentSolution.Projects.Count + " project(s)");
            }
        }
    }
}
