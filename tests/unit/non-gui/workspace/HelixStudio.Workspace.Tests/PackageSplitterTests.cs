using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PackageSplitterTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_split_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void W(string relPath, string content = "")
        {
            string full = Path.Combine(_root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        [TestMethod]
        public void Split_ExtractsSharedLibAndOneAppPackagePerEntryFile()
        {
            string pkg = Path.Combine(_root, "app");
            W(@"app\__init__.py", "");
            W(@"app\shared.py", "def helper():\n    return 1\n");
            W(@"app\main.py",
              "from . import shared\n\ndef main():\n    return shared.helper()\n");
            W(@"app\save_pdb.py",
              "from . import shared\n\ndef main():\n    return shared.helper()\n");
            W(@"app\_diag.py",
              "from .shared import helper\n\ndef main():\n    return helper()\n");

            PackageSplitResult result = PackageSplitter.Split(pkg, "app");

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(4, result.Packages.Count, "1 lib + 3 apps");
            Assert.AreEqual(1, result.Packages.Count(p => !p.IsExecutable));
            Assert.AreEqual(3, result.Packages.Count(p => p.IsExecutable));

            string libDir = result.Packages.First(p => !p.IsExecutable).DirectoryPath;
            Assert.AreEqual("app_lib", Path.GetFileName(libDir));
            Assert.IsTrue(File.Exists(Path.Combine(libDir, "shared.py")));
            Assert.IsTrue(File.Exists(Path.Combine(libDir, "__init__.py")));

            // Split() itself moves everything out but leaves the (now-empty) directory in
            // place — deleting it is ImportPlanExecutor's call to make, not this method's.
            Assert.IsTrue(Directory.Exists(pkg));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(pkg).Length, "everything moved out");
        }

        [TestMethod]
        public void Split_RenamesNonMainNamedEntryFiles_ToMainPy_InTheirOwnPackage()
        {
            string pkg = Path.Combine(_root, "app");
            W(@"app\__init__.py", "");
            W(@"app\main.py", "def main():\n    pass\n");
            W(@"app\save_pdb.py", "def main():\n    pass\n");

            PackageSplitResult result = PackageSplitter.Split(pkg, "app");

            Assert.IsTrue(result.Success, result.ErrorMessage);
            SplitPackage saveApp = result.Packages.Single(p => p.Name == "save_pdb");
            Assert.IsTrue(File.Exists(Path.Combine(saveApp.DirectoryPath, "main.py")),
                         "the entry file is renamed to main.py so PackageComplianceValidator's " +
                         "entry-module-naming rule is satisfied in its new package");
            Assert.IsFalse(File.Exists(Path.Combine(saveApp.DirectoryPath, "save_pdb.py")));

            SplitPackage mainApp = result.Packages.Single(p => p.Name == "main");
            Assert.IsTrue(File.Exists(Path.Combine(mainApp.DirectoryPath, "main.py")));
        }

        [TestMethod]
        public void Split_RewritesRelativeImports_ToAbsoluteAtTheNewLibPackage()
        {
            string pkg = Path.Combine(_root, "app");
            W(@"app\__init__.py", "");
            W(@"app\shared.py", "def helper():\n    return 1\n");
            W(@"app\main.py", "from . import shared\ndef main():\n    return shared.helper()\n");
            W(@"app\other.py", "from .shared import helper\ndef main():\n    return helper()\n");

            PackageSplitResult result = PackageSplitter.Split(pkg, "app");

            Assert.IsTrue(result.Success, result.ErrorMessage);
            string mainSource = File.ReadAllText(Path.Combine(result.Packages.Single(p => p.Name == "main").DirectoryPath, "main.py"));
            StringAssert.Contains(mainSource, "from app_lib import shared");

            string otherSource = File.ReadAllText(Path.Combine(result.Packages.Single(p => p.Name == "other").DirectoryPath, "main.py"));
            StringAssert.Contains(otherSource, "from app_lib.shared import helper");
        }

        [TestMethod]
        public void Split_MovesSubdirectoriesAndNonEntryFiles_IntoTheLibPackage()
        {
            string pkg = Path.Combine(_root, "app");
            W(@"app\__init__.py", "");
            W(@"app\utils\helpers.py", "x = 1\n");
            W(@"app\readme.txt", "notes");
            W(@"app\main.py", "def main():\n    pass\n");
            W(@"app\other.py", "def main():\n    pass\n");

            PackageSplitResult result = PackageSplitter.Split(pkg, "app");

            Assert.IsTrue(result.Success, result.ErrorMessage);
            string libDir = result.Packages.Single(p => !p.IsExecutable).DirectoryPath;
            Assert.IsTrue(Directory.Exists(Path.Combine(libDir, "utils")));
            Assert.IsTrue(File.Exists(Path.Combine(libDir, "utils", "helpers.py")));
            Assert.IsTrue(File.Exists(Path.Combine(libDir, "readme.txt")));
        }

        [TestMethod]
        public void Split_RewritesPlainImportOfAnotherEntryFile_ToItsNewPackagePath()
        {
            // A real shape: one standalone script reuses helpers from ANOTHER standalone
            // script via a plain top-level `import _other as alias` — both files
            // legitimately declare their own def main() too, so both get split into their
            // own executable package, but the reference between them must keep working:
            // _other.py -> other.py becomes package "other" holding main.py, so
            // "import _other as x" must become "import other.main as x".
            string pkg = Path.Combine(_root, "app");
            W(@"app\__init__.py", "");
            W(@"app\_helper_script.py",
              "def helper():\n    return 42\n\ndef main():\n    return helper()\n");
            W(@"app\consumer.py",
              "import _helper_script as hs\n\ndef main():\n    return hs.helper()\n");

            PackageSplitResult result = PackageSplitter.Split(pkg, "app");

            Assert.IsTrue(result.Success, result.ErrorMessage);
            string consumerPath = Path.Combine(
                result.Packages.Single(p => p.Name == "consumer").DirectoryPath, "main.py");
            string consumerSource = File.ReadAllText(consumerPath);
            StringAssert.Contains(consumerSource, "import helper_script.main as hs");
            Assert.IsFalse(consumerSource.Contains("_helper_script"),
                           "the old module name must not survive the rewrite");
        }

        [TestMethod]
        public void Split_FewerThanTwoEntryFiles_Fails()
        {
            string pkg = Path.Combine(_root, "app");
            W(@"app\__init__.py", "");
            W(@"app\main.py", "def main():\n    pass\n");

            PackageSplitResult result = PackageSplitter.Split(pkg, "app");

            Assert.IsFalse(result.Success);
            Assert.IsTrue(Directory.Exists(pkg), "nothing touched when there's nothing to split");
        }

        [TestMethod]
        public void Split_MissingDirectory_FailsCleanly()
        {
            PackageSplitResult result = PackageSplitter.Split(Path.Combine(_root, "nope"), "nope");
            Assert.IsFalse(result.Success);
        }
    }
}
