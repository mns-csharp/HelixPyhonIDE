using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Importing a package that lives somewhere else on the disk.
    ///
    /// <para>
    /// The bar is the user's sentence, not an internal one: point at a file, then write
    /// <c>from cms.tensor import device</c>. Everything asserted here is a step on that path —
    /// which directory has to go on <c>sys.path</c>, what the import statement then reads, and
    /// whether the answer survives a save and a reopen without disturbing the rest of the file.
    /// </para>
    /// </summary>
    [TestClass]
    public class ModuleSearchPathTests
    {
        private string _temp;

        [TestInitialize]
        public void CreateTemp()
        {
            _temp = Path.Combine(Path.GetTempPath(), "helix_modpath_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        [TestCleanup]
        public void RemoveTemp()
        {
            try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Build a directory chain and drop a file at the end of it.</summary>
        private string Make(string relativePath, params string[] packageDirectories)
        {
            foreach (string package in packageDirectories)
            {
                string directory = Path.Combine(_temp, package.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "__init__.py"), string.Empty);
            }
            string full = Path.Combine(_temp, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, "value = 1\n");
            return full;
        }

        // ------------------------------------------------------------------ resolving

        /// <summary>
        /// The shape this whole feature was built for: a book's example package, three directory
        /// levels down, under a folder whose own name is not a legal Python identifier.
        /// </summary>
        [TestMethod]
        public void APackagedModuleResolvesToItsPackageRootAndDottedName()
        {
            string file = Make("code/cms/tensor.py", "code/cms");

            PythonModuleResolution resolved = PythonModulePath.Resolve(file);

            Assert.IsTrue(resolved.Succeeded, resolved.Error);
            Assert.AreEqual(Path.Combine(_temp, "code"), resolved.RootDirectory,
                "the root is the first directory ABOVE the package — the one Python needs on sys.path");
            Assert.AreEqual("cms.tensor", resolved.ModuleName);
            Assert.AreEqual("cms", resolved.TopLevelName);
            Assert.AreEqual("from cms.tensor import <name>", resolved.ImportStatement);
        }

        [TestMethod]
        public void ANestedPackageKeepsEveryLevelOfItsDottedName()
        {
            string file = Make("code/cms/nn/layer.py", "code/cms", "code/cms/nn");

            PythonModuleResolution resolved = PythonModulePath.Resolve(file);

            Assert.IsTrue(resolved.Succeeded, resolved.Error);
            Assert.AreEqual(Path.Combine(_temp, "code"), resolved.RootDirectory);
            Assert.AreEqual("cms.nn.layer", resolved.ModuleName);
        }

        [TestMethod]
        public void ALooseScriptResolvesToItsOwnFolderAndAPlainImport()
        {
            string file = Make("scripts/helpers.py");

            PythonModuleResolution resolved = PythonModulePath.Resolve(file);

            Assert.IsTrue(resolved.Succeeded, resolved.Error);
            Assert.AreEqual(Path.Combine(_temp, "scripts"), resolved.RootDirectory);
            Assert.AreEqual("helpers", resolved.ModuleName);
            Assert.AreEqual("import helpers", resolved.ImportStatement);
        }

        /// <summary>Picking the package's own <c>__init__.py</c> must mean the package, not a module called `__init__`.</summary>
        [TestMethod]
        public void PickingAnInitFileResolvesToThePackageItself()
        {
            Make("code/cms/tensor.py", "code/cms");
            string init = Path.Combine(_temp, "code", "cms", "__init__.py");

            PythonModuleResolution resolved = PythonModulePath.Resolve(init);

            Assert.IsTrue(resolved.Succeeded, resolved.Error);
            Assert.AreEqual(Path.Combine(_temp, "code"), resolved.RootDirectory);
            Assert.AreEqual("cms", resolved.ModuleName);
            Assert.IsTrue(resolved.IsPackageInitializer);
        }

        /// <summary>
        /// A directory that cannot be a dotted-name segment stops the walk, because Python could
        /// not import through it either. The real corpus is full of these — <c>00-sample-common</c>,
        /// <c>03_Computational_…</c> — and walking past one produces a name that will never import.
        /// </summary>
        [TestMethod]
        public void AFolderThatIsNotAValidIdentifierStopsTheWalk()
        {
            string file = Make("00-sample-common/sample_common/model.py",
                               "00-sample-common", "00-sample-common/sample_common");

            PythonModuleResolution resolved = PythonModulePath.Resolve(file);

            Assert.IsTrue(resolved.Succeeded, resolved.Error);
            Assert.AreEqual(Path.Combine(_temp, "00-sample-common"), resolved.RootDirectory);
            Assert.AreEqual("sample_common.model", resolved.ModuleName);
        }

        [TestMethod]
        public void AFileNameThatIsNotAValidModuleNameIsRefusedWithAReason()
        {
            string file = Make("scripts/my-script.py");

            PythonModuleResolution resolved = PythonModulePath.Resolve(file);

            Assert.IsFalse(resolved.Succeeded);
            StringAssert.Contains(resolved.Error, "my-script");
        }

        [TestMethod]
        public void AKeywordIsNotAModuleName()
        {
            Assert.IsFalse(PythonModulePath.IsIdentifier("class"));
            Assert.IsFalse(PythonModulePath.IsIdentifier("import"));
            Assert.IsTrue(PythonModulePath.IsIdentifier("classifier"));
            Assert.IsTrue(PythonModulePath.IsIdentifier("_private"));
            Assert.IsFalse(PythonModulePath.IsIdentifier("2fast"));
        }

        [TestMethod]
        public void AMissingFileAndANonPythonFileAreBothRefusedWithAReason()
        {
            PythonModuleResolution missing = PythonModulePath.Resolve(Path.Combine(_temp, "nope.py"));
            Assert.IsFalse(missing.Succeeded);
            StringAssert.Contains(missing.Error, "no file");

            string text = Path.Combine(_temp, "notes.txt");
            File.WriteAllText(text, "hello");
            PythonModuleResolution wrong = PythonModulePath.Resolve(text);
            Assert.IsFalse(wrong.Succeeded);
            StringAssert.Contains(wrong.Error, "not a Python source file");
        }

        // ------------------------------------------------------------------ what a root offers

        [TestMethod]
        public void ImportableNamesListsPackagesAndModulesAndNothingElse()
        {
            Make("code/cms/tensor.py", "code/cms");
            Make("code/helpers.py");
            Directory.CreateDirectory(Path.Combine(_temp, "code", "data"));      // no __init__.py
            File.WriteAllText(Path.Combine(_temp, "code", "README.md"), "#");

            int total;
            IList<string> names = PythonModulePath.ImportableNames(Path.Combine(_temp, "code"), 10, out total);

            CollectionAssert.Contains((System.Collections.ICollection)names, "cms");
            CollectionAssert.Contains((System.Collections.ICollection)names, "helpers");
            CollectionAssert.DoesNotContain((System.Collections.ICollection)names, "data");
            CollectionAssert.DoesNotContain((System.Collections.ICollection)names, "README");
            Assert.AreEqual(2, total);
        }

        /// <summary>
        /// The cap is what keeps a pane from walking a repository to draw a label — and the total
        /// still comes back, so the caller says "and 40 more" instead of showing a prefix as if it
        /// were the whole list.
        /// </summary>
        [TestMethod]
        public void ImportableNamesIsCappedButStillReportsTheTrueTotal()
        {
            for (int i = 0; i < 12; i++) Make("many/module" + i + ".py");

            int total;
            IList<string> names = PythonModulePath.ImportableNames(Path.Combine(_temp, "many"), 5, out total);

            Assert.AreEqual(5, names.Count);
            Assert.AreEqual(12, total);
        }

        [TestMethod]
        public void AMissingRootReportsNothingRatherThanThrowing()
        {
            int total;
            IList<string> names = PythonModulePath.ImportableNames(
                Path.Combine(_temp, "not-there"), 10, out total);

            Assert.AreEqual(0, names.Count);
            Assert.AreEqual(0, total);
        }

        // ------------------------------------------------------------------ living in the notebook

        [TestMethod]
        public void ARegisteredPathSurvivesASaveAndReopen()
        {
            NotebookDocument document = NotebookFactory.CreateNew();
            string root = Path.Combine(_temp, "code");

            Assert.IsTrue(document.AddModuleSearchPath(root));

            NotebookDocument reopened = NotebookReader.Read(NotebookWriter.Write(document)).Document;

            CollectionAssert.AreEqual(new[] { root }, new List<string>(reopened.ModuleSearchPaths).ToArray());
        }

        [TestMethod]
        public void TheSamePathIsNotRegisteredTwice()
        {
            NotebookDocument document = NotebookFactory.CreateNew();

            Assert.IsTrue(document.AddModuleSearchPath(@"D:\code"));
            Assert.IsFalse(document.AddModuleSearchPath(@"d:\CODE\"),
                "Windows paths differ in case and trailing slash and are still the same folder");
            Assert.AreEqual(1, document.ModuleSearchPaths.Count);
        }

        [TestMethod]
        public void ThePathIsStoredUnderTheNamespacedHelixstudioKey()
        {
            NotebookDocument document = NotebookFactory.CreateNew();
            document.AddModuleSearchPath(@"D:\code");

            JObject written = JObject.Parse(NotebookWriter.Write(document));
            JToken stored = written["metadata"]["helixstudio"]["module_paths"];

            Assert.IsNotNull(stored, "namespaced, so Jupyter and Colab carry it through untouched");
            Assert.AreEqual(@"D:\code", stored[0].ToString());
        }

        /// <summary>
        /// The rule the whole writer is built around: a notebook nobody changed saves as the file it
        /// was. Adding a path and taking it away again has to leave no residue — not an empty array,
        /// not an empty `helixstudio` object.
        /// </summary>
        [TestMethod]
        public void AddingAndRemovingAPathLeavesTheFileAsItWas()
        {
            NotebookDocument document = NotebookFactory.CreateNew();
            string before = NotebookWriter.Write(document);

            document.AddModuleSearchPath(@"D:\code");
            Assert.AreNotEqual(before, NotebookWriter.Write(document), "the control: adding must change the file");

            Assert.IsTrue(document.RemoveModuleSearchPath(@"D:\code"));

            Assert.AreEqual(before, NotebookWriter.Write(document),
                "a path added and removed again must not leave a diff behind");
        }

        [TestMethod]
        public void ANotebookThatNeverRegisteredAnythingHasNoSuchKey()
        {
            JObject written = JObject.Parse(NotebookWriter.Write(NotebookFactory.CreateNew()));

            Assert.IsNull(written["metadata"]["helixstudio"],
                "writing the key into every notebook would put a diff on every file the user opened");
            Assert.AreEqual(0, NotebookFactory.CreateNew().ModuleSearchPaths.Count);
        }

        [TestMethod]
        public void RemovingAPathThatIsNotThereChangesNothing()
        {
            NotebookDocument document = NotebookFactory.CreateNew();
            document.AddModuleSearchPath(@"D:\code");

            Assert.IsFalse(document.RemoveModuleSearchPath(@"D:\elsewhere"));
            Assert.AreEqual(1, document.ModuleSearchPaths.Count);
        }
    }
}
