using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using HelixPythonNamespace.Editor.Notebook;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Registering a module through the notebook surface: the route a user actually takes, from
    /// "I picked this file" to "the notebook remembers it and says what I can now write".
    ///
    /// <para>
    /// The engine tests prove the resolution is right. These prove the surface uses it — that the
    /// tab writes the resolved ROOT and not the file, tells the user the import statement rather
    /// than a path, marks the notebook dirty so the setting survives, and refuses a bad pick with a
    /// sentence instead of silence.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookModulePathSurfaceTests
    {
        private string _temp;

        [TestInitialize]
        public void CreateTemp()
        {
            _temp = Path.Combine(Path.GetTempPath(), "helix_modsurface_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        [TestCleanup]
        public void RemoveTemp()
        {
            try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Build `&lt;temp&gt;\code\cms\tensor.py` — the book's own shape.</summary>
        private string MakeBookishPackage()
        {
            string package = Path.Combine(_temp, "code", "cms");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "__init__.py"), string.Empty);
            string module = Path.Combine(package, "tensor.py");
            File.WriteAllText(module, "def device():\n    return 'cpu'\n");
            return module;
        }

        private static NotebookTabPage TabWithNotebook()
        {
            var tab = new NotebookTabPage();
            tab.LoadFile(WriteNotebook());
            return tab;
        }

        private static string WriteNotebook()
        {
            string path = Path.Combine(Path.GetTempPath(), "helix_nb_" + Guid.NewGuid().ToString("N") + ".ipynb");
            NotebookWriter.WriteFile(NotebookFactory.CreateNew(), path);
            return path;
        }

        [TestMethod]
        public void PickingAFileRegistersItsPackageRootAndSaysWhatCanNowBeWritten()
        {
            string module = MakeBookishPackage();
            using (NotebookTabPage tab = TabWithNotebook())
            {
                string reported = null;
                tab.StatusChanged += delegate (object sender, string message) { reported = message; };

                tab.RegisterModuleFile(module);

                CollectionAssert.AreEqual(new[] { Path.Combine(_temp, "code") },
                    new List<string>(tab.Document.ModuleSearchPaths).ToArray(),
                    "what goes on sys.path is the folder ABOVE the package, never the file picked");
                StringAssert.Contains(reported, "from cms.tensor import",
                    "the receipt is the import statement — that is the thing the user came for");
                Assert.IsFalse(tab.IsModified,
                    "the setting is written out as it is made, so nothing is left waiting for a Ctrl+S");
            }
        }

        [TestMethod]
        public void RegisteringTheSameFileTwiceSaysSoInsteadOfDuplicatingTheRoot()
        {
            string module = MakeBookishPackage();
            using (NotebookTabPage tab = TabWithNotebook())
            {
                tab.RegisterModuleFile(module);

                string reported = null;
                tab.StatusChanged += delegate (object sender, string message) { reported = message; };
                tab.RegisterModuleFile(module);

                Assert.AreEqual(1, tab.Document.ModuleSearchPaths.Count);
                StringAssert.Contains(reported, "Already on the import path");
            }
        }

        /// <summary>
        /// Two modules from the same package register one root, not two — the second pick is
        /// already covered by the first, and a list that grew a duplicate row per file would be
        /// unreadable after an afternoon.
        /// </summary>
        [TestMethod]
        public void TwoModulesFromOnePackageShareOneRoot()
        {
            string tensor = MakeBookishPackage();
            string chain = Path.Combine(Path.GetDirectoryName(tensor), "chain.py");
            File.WriteAllText(chain, "class Chain(object):\n    pass\n");

            using (NotebookTabPage tab = TabWithNotebook())
            {
                tab.RegisterModuleFile(tensor);
                tab.RegisterModuleFile(chain);

                Assert.AreEqual(1, tab.Document.ModuleSearchPaths.Count);
            }
        }

        [TestMethod]
        public void ABadPickIsRefusedWithAReasonAndChangesNothing()
        {
            string notPython = Path.Combine(_temp, "notes.txt");
            File.WriteAllText(notPython, "hello");

            using (NotebookTabPage tab = TabWithNotebook())
            {
                string reported = null;
                tab.StatusChanged += delegate (object sender, string message) { reported = message; };

                tab.RegisterModuleFile(notPython);

                Assert.AreEqual(0, tab.Document.ModuleSearchPaths.Count);
                StringAssert.Contains(reported, "not a Python source file");
                Assert.IsFalse(tab.IsModified);
            }
        }

        /// <summary>
        /// The reported failure: a repository root was registered, the status bar said "Added
        /// D:\…\sample_workspace", and the import still failed — because that folder holds no top-level
        /// package at all. "Added" answered a question the user had not asked. Registering a folder
        /// that offers nothing has to SAY it offers nothing.
        /// </summary>
        [TestMethod]
        public void AFolderWithNothingImportableSaysSoInsteadOfJustAdded()
        {
            // The real shape: sub-folders named like `00-sample-common`, none of them a package.
            string repository = Path.Combine(_temp, "sample_workspace");
            Directory.CreateDirectory(Path.Combine(repository, "00-sample-common"));
            Directory.CreateDirectory(Path.Combine(repository, "01-prepare"));
            File.WriteAllText(Path.Combine(repository, "README.md"), "#");

            using (NotebookTabPage tab = TabWithNotebook())
            {
                var reported = new List<string>();
                tab.StatusChanged += delegate (object sender, string message)
                {
                    lock (reported) reported.Add(message);      // the second one arrives off a pool thread
                };

                tab.RegisterModuleFolder(repository);
                WaitFor(delegate { lock (reported) return Joined(reported).Contains("…which")
                                                     || Joined(reported).Contains("…but"); });

                string verdict;
                lock (reported) verdict = string.Join(" | ", reported.ToArray());
                StringAssert.Contains(verdict, "nothing in sample_workspace can be imported");
                StringAssert.Contains(verdict, "Add a module",
                    "telling the user what went wrong without telling them what to do instead is half a message");
            }
        }

        [TestMethod]
        public void AFolderThatDoesOfferPackagesSaysWhichOnes()
        {
            MakeBookishPackage();                                  // <temp>\code\cms\

            using (NotebookTabPage tab = TabWithNotebook())
            {
                var reported = new List<string>();
                tab.StatusChanged += delegate (object sender, string message)
                {
                    lock (reported) reported.Add(message);
                };

                tab.RegisterModuleFolder(Path.Combine(_temp, "code"));
                WaitFor(delegate { lock (reported) return Joined(reported).Contains("…which")
                                                     || Joined(reported).Contains("…but"); });

                string verdict;
                lock (reported) verdict = string.Join(" | ", reported.ToArray());
                StringAssert.Contains(verdict, "makes cms");
            }
        }

        private static string Joined(List<string> messages)
        {
            return string.Join(" | ", messages.ToArray());
        }

        /// <summary>The folder report comes back off a pool thread; pump until it lands.</summary>
        private static void WaitFor(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(20);
            }
        }

        [TestMethod]
        public void AFolderCanBeRegisteredDirectlyForLayoutsTheResolverCannotName()
        {
            string folder = Path.Combine(_temp, "namespace_pkg");
            Directory.CreateDirectory(folder);

            using (NotebookTabPage tab = TabWithNotebook())
            {
                tab.RegisterModuleFolder(folder);

                CollectionAssert.AreEqual(new[] { folder },
                    new List<string>(tab.Document.ModuleSearchPaths).ToArray());
            }
        }

        [TestMethod]
        public void RemovingARootTakesItOffAndSaysWhatRemovingDoesNotDo()
        {
            string module = MakeBookishPackage();
            using (NotebookTabPage tab = TabWithNotebook())
            {
                tab.RegisterModuleFile(module);

                string reported = null;
                tab.StatusChanged += delegate (object sender, string message) { reported = message; };
                tab.RemoveModulePath(Path.Combine(_temp, "code"));

                Assert.AreEqual(0, tab.Document.ModuleSearchPaths.Count);
                StringAssert.Contains(reported, "already imported stay",
                    "sys.path is read at import time only, and not saying so is the confusing outcome");
            }
        }

        /// <summary>
        /// Reported as "after IDE reboot, the added path is gone".
        ///
        /// <para>
        /// Registering a path used to only mark the notebook dirty and wait for a save that the
        /// user was never prompted for — so anything that ended the session without one threw the
        /// setting away. The import path is a setting, not a keystroke: it has to be on disk the
        /// moment it is set. Asserted by reopening the file WITHOUT saving in between.
        /// </para>
        /// </summary>
        [TestMethod]
        public void ARegisteredPathIsOnDiskImmediatelyWithoutAnExplicitSave()
        {
            string module = MakeBookishPackage();
            string notebook = WriteNotebook();

            using (var tab = new NotebookTabPage())
            {
                tab.LoadFile(notebook);
                tab.RegisterModuleFile(module);

                Assert.IsFalse(tab.IsModified,
                    "the notebook should already be written — nothing is waiting for a Ctrl+S");
            }

            // Straight off the disk, through the reader, with no Save() call anywhere above.
            NotebookDocument onDisk = NotebookReader.ReadFile(notebook).Document;
            CollectionAssert.AreEqual(new[] { Path.Combine(_temp, "code") },
                new List<string>(onDisk.ModuleSearchPaths).ToArray());
        }

        [TestMethod]
        public void RemovingAPathIsWrittenOutImmediatelyToo()
        {
            string module = MakeBookishPackage();
            string notebook = WriteNotebook();

            using (var tab = new NotebookTabPage())
            {
                tab.LoadFile(notebook);
                tab.RegisterModuleFile(module);
                tab.RemoveModulePath(Path.Combine(_temp, "code"));
            }

            Assert.AreEqual(0, NotebookReader.ReadFile(notebook).Document.ModuleSearchPaths.Count,
                "a removal that only lives in memory comes back on the next open");
        }

        /// <summary>The whole point of storing it in the notebook: reopen the file, keep the import.</summary>
        [TestMethod]
        public void TheRegisteredRootSurvivesSavingAndReopeningTheNotebook()
        {
            string module = MakeBookishPackage();
            string notebook = WriteNotebook();

            using (var tab = new NotebookTabPage())
            {
                tab.LoadFile(notebook);
                tab.RegisterModuleFile(module);
                Assert.IsTrue(tab.Save());
            }

            using (var reopened = new NotebookTabPage())
            {
                Assert.IsNull(reopened.LoadFile(notebook));
                CollectionAssert.AreEqual(new[] { Path.Combine(_temp, "code") },
                    new List<string>(reopened.Document.ModuleSearchPaths).ToArray());
            }
        }

        // ------------------------------------------------------------------ the panes

        [TestMethod]
        public void TheModulesPaneListsEachRootAndWhatItMakesImportable()
        {
            MakeBookishPackage();
            var document = NotebookFactory.CreateNew();
            document.AddModuleSearchPath(Path.Combine(_temp, "code"));

            Assert.AreEqual("cms", NotebookModulesPanel.Describe(Path.Combine(_temp, "code")),
                "a row that only shows a path does not tell the user what registering it bought them");
            Assert.AreEqual("(this folder is missing)",
                NotebookModulesPanel.Describe(Path.Combine(_temp, "gone")),
                "a drive that is not mounted must say so, not read as an empty folder");
        }

        [TestMethod]
        public void TheModulesPaneIsOnTheRailAndTheRuntimeMenuOpensIt()
        {
            using (NotebookTabPage tab = TabWithNotebook())
            {
                Assert.IsNotNull(tab.Workspace.Pane("modules"), "the pane has to be reachable from the rail");

                tab.Workspace.ShowPane("modules");
                Assert.AreEqual("modules", tab.Workspace.OpenPane);
            }
        }

        /// <summary>
        /// The Files tree starts at the notebook's own folder, and the code being imported is by
        /// definition somewhere else. Without a sticky browse the tree snaps home on the next
        /// document refresh — back to the folder the user just navigated away from.
        /// </summary>
        [TestMethod]
        public void BrowsingTheFilesPaneElsewhereSurvivesADocumentRefresh()
        {
            string elsewhere = Path.Combine(_temp, "code");
            Directory.CreateDirectory(elsewhere);

            using (NotebookTabPage tab = TabWithNotebook())
            {
                var files = tab.Workspace.Pane("files") as NotebookFilesPanel;
                Assert.IsNotNull(files);

                files.BrowseTo(elsewhere);
                files.Refresh(tab.Document);

                var tree = FirstTree(files);
                Assert.AreEqual("code", tree.Nodes[0].Text);
                Assert.AreEqual(elsewhere, tree.Nodes[0].Tag as string);
            }
        }

        private static TreeView FirstTree(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                var tree = child as TreeView;
                if (tree != null) return tree;
                TreeView deeper = FirstTree(child);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }
}
