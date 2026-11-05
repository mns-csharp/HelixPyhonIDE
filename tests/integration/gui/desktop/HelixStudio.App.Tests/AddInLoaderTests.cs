using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HelixPythonNamespace.AddIns;
using Microsoft.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Spec 001 §4.4 and §4.4.1, oracle 3: the folder scan.
    ///
    /// <para>Each add-in here is a real DLL, compiled at test time into its own folder under a temp
    /// root, so the loader does exactly what it does at startup: <c>Assembly.LoadFrom</c>, type
    /// discovery, and binding the SDK from the IDE folder. Copying the test assembly instead would
    /// load it a second time under another identity, and its add-in types would not be the ones
    /// the test holds. Every assembly gets a unique name, because one loaded stays loaded.</para>
    /// </summary>
    [TestClass]
    public class AddInLoaderTests
    {
        private static string NewRoot()
        {
            // Left in %TEMP%: a loaded DLL is locked until the test host exits.
            string root = Path.Combine(Path.GetTempPath(), "helix-addin-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        /// <summary>Compiles one add-in into <c>root\folder\&lt;unique&gt;.dll</c> and returns the DLL path.</summary>
        private static string CompileAddIn(string root, string folder, string id, string displayName, bool throwInInitialize = false)
        {
            string dir = Path.Combine(root, folder);
            Directory.CreateDirectory(dir);
            string dll = Path.Combine(dir, folder + "_" + Guid.NewGuid().ToString("N") + ".dll");
            string source = @"
[assembly: System.Reflection.AssemblyVersion(""1.2.3.4"")]
public class TestAddIn : HelixPythonNamespace.AddIns.IHelixAddIn
{
    public string Id { get { return """ + id + @"""; } }
    public string DisplayName { get { return """ + displayName + @"""; } }
    public void Initialize(HelixPythonNamespace.AddIns.IAddInHost host)
    {
        host.AddMenuItem(""Hello"", delegate { }, null);
        " + (throwInInitialize ? "throw new System.InvalidOperationException(\"compiled add-in refuses to start\");" : "") + @"
    }
    public void Shutdown() { }
}
public abstract class NotAnAddIn : HelixPythonNamespace.AddIns.IHelixAddIn
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract void Initialize(HelixPythonNamespace.AddIns.IAddInHost host);
    public abstract void Shutdown();
}";
            var options = new CompilerParameters { OutputAssembly = dll, GenerateInMemory = false };
            options.ReferencedAssemblies.Add("System.dll");
            options.ReferencedAssemblies.Add("System.Drawing.dll");
            options.ReferencedAssemblies.Add("System.Windows.Forms.dll");
            options.ReferencedAssemblies.Add(typeof(IHelixAddIn).Assembly.Location);
            using (var compiler = new CSharpCodeProvider())
            {
                CompilerResults results = compiler.CompileAssemblyFromSource(options, source);
                Assert.AreEqual(0, results.Errors.Count,
                    string.Join("\n", results.Errors.Cast<CompilerError>().Select(e => e.ToString())));
            }
            return dll;
        }

        private static IEnumerable<KeyValuePair<string, AddInKind>> UserRoot(string root)
        {
            return new[] { new KeyValuePair<string, AddInKind>(root, AddInKind.User) };
        }

        private static ToolStripMenuItem SubmenuOf(MainForm form, string displayName)
        {
            return form.AddInMenuForTests.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == displayName);
        }

        private static string UniqueId() { return "test-" + Guid.NewGuid().ToString("N"); }

        [TestMethod]
        public void AGoodAddInInItsFolder_IsLoaded_WithItsVersionKindAndFolder()
        {
            string root = NewRoot();
            string id = UniqueId();
            string dll = CompileAddIn(root, "Good", id, "Good Add-in");

            using (MainForm form = TestMainForm.CreateComposed())
            {
                AddInLoader.LoadFrom(form, UserRoot(root));

                LoadedAddIn loaded = form.LoadedAddIns.SingleOrDefault(a => a.Id == id);
                Assert.IsNotNull(loaded, "loaded from " + dll + "; Output: " + form._buildOutputTextBox.Text);
                Assert.AreEqual("Good Add-in", loaded.DisplayName);
                Assert.AreEqual(new Version(1, 2, 3, 4), loaded.Version);
                Assert.AreEqual(AddInKind.User, loaded.Kind);
                Assert.AreEqual(Path.GetDirectoryName(dll), loaded.Folder);
                Assert.IsNotNull(SubmenuOf(form, "Good Add-in"), "its menu item is under Add-in ▸ Good Add-in");
                Assert.AreSame(typeof(IHelixAddIn).Assembly, loaded.Instance.GetType().GetInterfaces()
                    .Single(i => i.Name == "IHelixAddIn").Assembly, "one identity of the SDK");
            }
        }

        [TestMethod]
        public void AThrowingAddIn_IsReportedWithItsDll_AndTheOthersStillLoad()
        {
            string root = NewRoot();
            string badId = UniqueId(), goodId = UniqueId();
            string badDll = CompileAddIn(root, "ABad", badId, "Bad Add-in", throwInInitialize: true);
            CompileAddIn(root, "BGood", goodId, "Good Add-in");
            File.WriteAllText(Path.Combine(root, "ABad", "garbage.dll"), "this is not an assembly");

            using (MainForm form = TestMainForm.CreateComposed())
            {
                AddInLoader.LoadFrom(form, UserRoot(root));

                string output = form._buildOutputTextBox.Text;
                StringAssert.Contains(output, badDll, "the failure names the DLL");
                StringAssert.Contains(output, "compiled add-in refuses to start", "and the exception");
                StringAssert.Contains(output, Path.Combine(root, "ABad", "garbage.dll"), "a file that is not an assembly is reported too");
                Assert.IsFalse(form.LoadedAddIns.Any(a => a.Id == badId));
                Assert.IsTrue(form.LoadedAddIns.Any(a => a.Id == goodId), "the IDE carried on to the next add-in");
            }
        }

        [TestMethod]
        public void AnEmptyOrMissingRoot_LoadsNothing_AndSaysNothing()
        {
            string empty = NewRoot();
            string missing = Path.Combine(Path.GetTempPath(), "helix-addin-missing-" + Guid.NewGuid().ToString("N"));

            using (MainForm form = TestMainForm.CreateComposed())
            {
                int before = form.LoadedAddIns.Count;
                string outputBefore = form._buildOutputTextBox.Text;
                AddInLoader.LoadFrom(form, new[]
                {
                    new KeyValuePair<string, AddInKind>(empty, AddInKind.Bundled),
                    new KeyValuePair<string, AddInKind>(missing, AddInKind.User),
                });
                Assert.AreEqual(before, form.LoadedAddIns.Count);
                Assert.AreEqual(outputBefore, form._buildOutputTextBox.Text);
            }
        }

        [TestMethod]
        public void ARemovedAddIn_IsNotLoaded()
        {
            string root = NewRoot();
            string id = UniqueId();
            CompileAddIn(root, "Removed", id, "Removed Add-in");

            using (MainForm form = TestMainForm.CreateComposed())
            {
                form.AddInUserSettings.RemovedAddIns.Add(id);
                try
                {
                    AddInLoader.LoadFrom(form, UserRoot(root));
                    Assert.IsFalse(form.LoadedAddIns.Any(a => a.Id == id));
                    Assert.IsNull(SubmenuOf(form, "Removed Add-in"));
                }
                finally { form.AddInUserSettings.RemovedAddIns.Remove(id); }
            }
        }

        [TestMethod]
        public void TwoFoldersWithTheSameId_TheSecondIsRefused()
        {
            string root = NewRoot();
            string id = UniqueId();
            string first = CompileAddIn(root, "A", id, "Twin");
            string second = CompileAddIn(root, "B", id, "Twin");

            using (MainForm form = TestMainForm.CreateComposed())
            {
                AddInLoader.LoadFrom(form, UserRoot(root));
                LoadedAddIn loaded = form.LoadedAddIns.Single(a => a.Id == id);
                Assert.AreEqual(Path.GetDirectoryName(first), loaded.Folder, "folders load in name order; the first wins");
                StringAssert.Contains(form._buildOutputTextBox.Text, second);
                StringAssert.Contains(form._buildOutputTextBox.Text, "already loaded");
            }
        }

        [TestMethod]
        public void PendingDeletes_AreDeletedBeforeLoading()
        {
            string root = NewRoot();
            string doomed = Path.Combine(root, "Doomed");
            Directory.CreateDirectory(doomed);
            File.WriteAllText(Path.Combine(doomed, "leftover.txt"), "x");

            using (MainForm form = TestMainForm.CreateComposed())
            {
                form.AddInUserSettings.PendingAddInDeletes.Add(doomed);
                AddInLoader.LoadFrom(form, UserRoot(root));
                Assert.IsFalse(Directory.Exists(doomed), "the removed add-in's folder is gone");
                Assert.IsFalse(form.AddInUserSettings.PendingAddInDeletes.Contains(doomed), "and off the queue");
            }
        }

        [TestMethod]
        public void TheUserRoot_FollowsTheStateDirectory()
        {
            string state = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            Assert.IsFalse(string.IsNullOrWhiteSpace(state), "run with /Settings:HelixStudio.runsettings");
            Assert.AreEqual(Path.Combine(Environment.ExpandEnvironmentVariables(state), "addins"), AddInLoader.UserRoot);
            Assert.AreEqual(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "addins"), AddInLoader.BundledRoot);
        }

        /// <summary>
        /// The real startup path: the composed constructor scans the user root before the form is
        /// ever shown, so an add-in's tab is registered before the saved tab visibility is replayed.
        /// A fresh state directory for this one test, so nothing it installs reaches other tests.
        /// </summary>
        [TestMethod]
        public void TheComposedShell_LoadsUserAddIns_InItsConstructor()
        {
            string saved = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            string state = NewRoot();
            string id = UniqueId();
            try
            {
                Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", state);
                CompileAddIn(Path.Combine(state, "addins"), "Startup", id, "Startup Add-in");

                using (MainForm form = TestMainForm.CreateComposed())
                {
                    Assert.IsTrue(form.LoadedAddIns.Any(a => a.Id == id && a.Kind == AddInKind.User),
                        "Output: " + form._buildOutputTextBox.Text);
                    Assert.IsNotNull(SubmenuOf(form, "Startup Add-in"));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", saved);
            }
        }
    }
}
