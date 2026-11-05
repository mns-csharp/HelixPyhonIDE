using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// User report: the Build menu for a C++/CUDA workspace offered only "Build CMake
    /// Project" — no Rebuild, no Clean, and nothing at project scope — where VS2017 gives
    /// Build/Rebuild/Clean for the solution AND for the individual project. These assert the
    /// VS-parity set exists, is reachable in a native workspace, and stays out of a Python one.
    /// </summary>
    [TestClass]
    public class NativeBuildMenuTests
    {
        private const string BuildSolution = "buildCMakeSolutionMenuItem";
        private const string RebuildSolution = "rebuildCMakeSolutionMenuItem";
        private const string CleanSolution = "cleanCMakeSolutionMenuItem";
        private const string BuildProject = "buildCMakeProjectMenu";
        private const string RebuildProject = "rebuildCMakeProjectMenu";
        private const string CleanProject = "cleanCMakeProjectMenu";

        private static readonly string[] AllNativeItems =
        { BuildSolution, RebuildSolution, CleanSolution, BuildProject, RebuildProject, CleanProject };

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        private static void SetField(object obj, string name, object value)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            f.SetValue(obj, value);
        }

        private static ToolStripMenuItem Item(MainForm form, string name)
        {
            var build = Field<ToolStripMenuItem>(form, "buildTopMenuItem");
            foreach (ToolStripItem it in build.DropDownItems)
            {
                var mi = it as ToolStripMenuItem;
                if (mi != null && string.Equals(mi.Name, name, StringComparison.Ordinal)) return mi;
            }
            return null;
        }

        private static void OpenBuildMenu(MainForm form)
        {
            MethodInfo m = typeof(MainForm).GetMethod("OnBuildMenuOpening",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "OnBuildMenuOpening expected");
            m.Invoke(form, new object[] { form, EventArgs.Empty });
        }

        private static void MakeNative(MainForm form)
        {
            SetField(form, "_currentSolution", new SolutionModel(@"C:\native\CMakeLists.txt", "native"));
            SetField(form, "_currentCMakeFolder", @"C:\native");
            SetField(form, "_currentCMakeProject",
                FormatterServices.GetUninitializedObject(typeof(CMakeProjectDescriptor)));
        }

        [TestMethod]
        public void NativeWorkspace_BuildMenu_CarriesTheFullVsStyleCommandSet()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                MakeNative(form);
                OpenBuildMenu(form);

                foreach (string name in AllNativeItems)
                {
                    ToolStripMenuItem mi = Item(form, name);
                    Assert.IsNotNull(mi, name + " must exist on the Build menu");
                    Assert.IsTrue(mi.Available, name + " must be reachable in a C++/CUDA workspace");
                }
            }
        }

        /// <summary>
        /// The three per-project entries are submenus filled from the open project's real CMake
        /// targets. With no targets resolved they must still say so rather than be silently empty —
        /// an empty submenu reads as a broken command.
        /// </summary>
        [TestMethod]
        public void PerProjectCommands_ArePopulatedSubmenus()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                MakeNative(form);
                OpenBuildMenu(form);

                foreach (string name in new[] { BuildProject, RebuildProject, CleanProject })
                {
                    ToolStripMenuItem mi = Item(form, name);
                    Assert.IsNotNull(mi);
                    Assert.IsTrue(mi.DropDownItems.Count > 0,
                        name + " must list targets (or say there are none), never be an empty menu");
                }
            }
        }

        /// <summary>
        /// While a native build runs the message loop keeps pumping (so output can stream), which
        /// left every menu clickable and a second Build/Clean/Close able to start on top of the
        /// first. Everything must go grey except Stop Execution — the Build menu itself stays
        /// enabled only so Stop can be reached inside it.
        /// </summary>
        [TestMethod]
        public void DuringANativeBuild_EverythingButStopExecutionIsDisabled()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                MakeNative(form);
                OpenBuildMenu(form);

                var stop = Field<ToolStripMenuItem>(form, "_stopExecutionMenuItem");
                bool stopBefore = stop.Enabled;

                SetBuildBusy(form, true);

                var menu = Field<MenuStrip>(form, "_menuStrip");
                var buildMenu = Field<ToolStripMenuItem>(form, "buildTopMenuItem");
                foreach (ToolStripItem top in menu.Items)
                {
                    var m = top as ToolStripMenuItem;
                    if (m == null) continue;
                    if (ReferenceEquals(m, buildMenu))
                        Assert.IsTrue(m.Enabled, "Build stays reachable so Stop Execution can be clicked");
                    else
                        Assert.IsFalse(m.Enabled,
                            (m.Text ?? "") + " must be disabled while a native build is running");
                }

                Assert.AreEqual(stopBefore, stop.Enabled,
                    "the gate must not touch Stop Execution — JobRunner owns its enabled state");
                foreach (ToolStripItem child in buildMenu.DropDownItems)
                {
                    if (ReferenceEquals(child, stop)) continue;
                    Assert.IsFalse(child.Enabled,
                        "Build ▸ " + (child.Text ?? "<sep>") + " must be disabled mid-build");
                }

                SetBuildBusy(form, false);

                foreach (ToolStripItem top in menu.Items)
                {
                    var m = top as ToolStripMenuItem;
                    if (m != null) Assert.IsTrue(m.Enabled, (m.Text ?? "") + " must come back after the build");
                }
                Assert.IsTrue(Item(form, BuildSolution).Enabled, "Build Solution must come back after the build");
            }
        }

        /// <summary>
        /// Each Build/Rebuild/Clean/Run starts from an empty Output pane — otherwise a short Clean
        /// appears to have reprinted the previous build's whole log.
        /// </summary>
        [TestMethod]
        public void StartingACommand_ClearsTheOutputPane()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                MakeNative(form);
                var box = Field<TextBox>(form, "_buildOutputTextBox");
                box.Text = "output from the PREVIOUS command";

                typeof(MainForm).GetMethod("BeginNativeCommandOutput",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, new object[] { null });

                StringAssert.DoesNotMatch(box.Text,
                    new System.Text.RegularExpressions.Regex("PREVIOUS"),
                    "the previous command's output must not survive into this one");
            }
        }

        private static void SetBuildBusy(MainForm form, bool busy)
        {
            MethodInfo m = typeof(MainForm).GetMethod("SetCommandsEnabledDuringNativeBuild",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(m, "SetCommandsEnabledDuringNativeBuild expected");
            m.Invoke(form, new object[] { !busy });
        }

        [TestMethod]
        public void PythonSolution_HidesEveryNativeBuildCommand()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetField(form, "_currentSolution", new SolutionModel(@"C:\proj\pyproject.toml", "Proj"));
                SetField(form, "_currentCMakeFolder", null);
                SetField(form, "_currentCMakeProject", null);
                OpenBuildMenu(form);

                foreach (string name in AllNativeItems)
                {
                    ToolStripMenuItem mi = Item(form, name);
                    if (mi == null) continue;
                    Assert.IsFalse(mi.Available,
                        name + " is a native-workspace command — §6.2 hides it in a Python solution");
                }
            }
        }
    }
}
