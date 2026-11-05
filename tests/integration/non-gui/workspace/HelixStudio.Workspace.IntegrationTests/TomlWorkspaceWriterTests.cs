using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class TomlWorkspaceWriterTests
    {
        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ws_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static PackageDescriptor MakePackage(string rootDir, string relDir, string name)
        {
            string dir = Path.Combine(rootDir, relDir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "__init__.py"), string.Empty);
            return new PackageDescriptor(name, dir);
        }

        [TestMethod]
        public void RootWriter_EmitsUvWorkspaceMembers()
        {
            string root = TempDir();
            var packages = new List<PackageDescriptor>
            {
                MakePackage(root, Path.Combine("src", "alpha"), "alpha"),
                MakePackage(root, Path.Combine("src", "beta"), "beta")
            };
            string path = Path.Combine(root, "pyproject.toml");
            TomlWorkspaceRootWriter.Write(path, "ws", "3.12", packages, root);
            PyProjectMetadata meta = PyProjectReader.Read(path);
            Assert.AreEqual("ws", meta.Name);
            CollectionAssert.AreEquivalent(new[] { "src/alpha", "src/beta" }, meta.WorkspaceMembers);
            Assert.IsTrue(meta.IsWorkspace);
        }

        [TestMethod]
        public void RootWriter_PreservesUnknownTables()
        {
            string root = TempDir();
            string path = Path.Combine(root, "pyproject.toml");
            File.WriteAllText(path,
                "[project]\nname = \"ws\"\nrequires-python = \">=3.12\"\n" +
                "\n[tool.mypy]\nstrict = true\npython_version = \"3.12\"\n");
            var packages = new List<PackageDescriptor>
            {
                MakePackage(root, "pkg", "pkg")
            };
            TomlWorkspaceRootWriter.Write(path, "ws", "3.12", packages, root);
            string body = File.ReadAllText(path);
            StringAssert.Contains(body, "[tool.mypy]");
            StringAssert.Contains(body, "strict = true");
            StringAssert.Contains(body, "python_version = \"3.12\"");
            StringAssert.Contains(body, "[tool.uv.workspace]");
        }

        [TestMethod]
        public void RootWriter_ExcludesSyntheticRootFromMembers()
        {
            string root = TempDir();
            PackageDescriptor synthetic = new PackageDescriptor("root_pkg", root) { IsSynthesized = true };
            PackageDescriptor real = MakePackage(root, "real", "real");
            var packages = new List<PackageDescriptor> { synthetic, real };
            string path = Path.Combine(root, "pyproject.toml");
            TomlWorkspaceRootWriter.Write(path, "ws", "3.12", packages, root);
            string raw = File.ReadAllText(path);
            PyProjectMetadata meta = PyProjectReader.Read(path);
            Assert.AreEqual(1, meta.WorkspaceMembers.Count,
                "Got members=" + string.Join(",", meta.WorkspaceMembers) + " | file:\n" + raw);
            Assert.AreEqual("real", meta.WorkspaceMembers[0]);
        }

        [TestMethod]
        public void MemberWriter_Fresh_WritesMinimalPyProject()
        {
            string root = TempDir();
            PackageDescriptor pkg = MakePackage(root, "alpha", "alpha");
            string note = TomlWorkspaceMemberWriter.Write(pkg, "3.11", PyProjectConflictResolution.Overwrite);
            StringAssert.StartsWith(note, "created");
            string path = Path.Combine(pkg.DirectoryPath, "pyproject.toml");
            Assert.IsTrue(File.Exists(path));
            PyProjectMetadata meta = PyProjectReader.Read(path);
            Assert.AreEqual("alpha", meta.Name);
            Assert.AreEqual("3.11", meta.PythonVersion);
            Assert.IsTrue(meta.ToolHelixStudio.ContainsKey("interface-style"));
        }

        [TestMethod]
        public void MemberWriter_Keep_DoesNothing()
        {
            string root = TempDir();
            PackageDescriptor pkg = MakePackage(root, "alpha", "alpha");
            string path = Path.Combine(pkg.DirectoryPath, "pyproject.toml");
            File.WriteAllText(path, "[project]\nname = \"handcrafted\"\n# preserve me\n");
            string before = File.ReadAllText(path);
            string note = TomlWorkspaceMemberWriter.Write(pkg, "3.12", PyProjectConflictResolution.Keep);
            StringAssert.Contains(note, "kept");
            Assert.AreEqual(before, File.ReadAllText(path));
        }

        [TestMethod]
        public void MemberWriter_Merge_AddsOnlyHelixStudioTable()
        {
            string root = TempDir();
            PackageDescriptor pkg = MakePackage(root, "alpha", "alpha");
            string path = Path.Combine(pkg.DirectoryPath, "pyproject.toml");
            File.WriteAllText(path, "[project]\nname = \"handcrafted\"\n# preserve me\n\n[tool.mypy]\nstrict = true\n");
            string note = TomlWorkspaceMemberWriter.Write(pkg, "3.12", PyProjectConflictResolution.Merge);
            StringAssert.Contains(note, "merged");
            string body = File.ReadAllText(path);
            // Existing content preserved.
            StringAssert.Contains(body, "[project]");
            StringAssert.Contains(body, "handcrafted");
            StringAssert.Contains(body, "[tool.mypy]");
            StringAssert.Contains(body, "strict = true");
            // Our table appended.
            StringAssert.Contains(body, "[tool.helixstudio]");
            StringAssert.Contains(body, "interface-style = \"protocol\"");
        }

        [TestMethod]
        public void MemberWriter_Overwrite_ReplacesExisting()
        {
            string root = TempDir();
            PackageDescriptor pkg = MakePackage(root, "alpha", "alpha");
            string path = Path.Combine(pkg.DirectoryPath, "pyproject.toml");
            File.WriteAllText(path, "# junk that will be replaced\n[project]\nname = \"old_name\"\n");
            string note = TomlWorkspaceMemberWriter.Write(pkg, "3.12", PyProjectConflictResolution.Overwrite);
            StringAssert.Contains(note, "overwrote");
            PyProjectMetadata meta = PyProjectReader.Read(path);
            Assert.AreEqual("alpha", meta.Name);
        }

        [TestMethod]
        public void TomlTableMerger_AppendsMissingTable()
        {
            string root = TempDir();
            string path = Path.Combine(root, "t.toml");
            File.WriteAllText(path, "[project]\nname = \"x\"\n");
            TomlTableMerger.EnsureTable(path, "tool.helixstudio",
                new Dictionary<string, string> { { "key", "\"value\"" } });
            string body = File.ReadAllText(path);
            StringAssert.Contains(body, "[project]");
            StringAssert.Contains(body, "[tool.helixstudio]");
            StringAssert.Contains(body, "key = \"value\"");
        }

        [TestMethod]
        public void TomlTableMerger_UpdatesExistingKey()
        {
            string root = TempDir();
            string path = Path.Combine(root, "t.toml");
            File.WriteAllText(path, "[tool.helixstudio]\nkey = \"old\"\n");
            TomlTableMerger.EnsureTable(path, "tool.helixstudio",
                new Dictionary<string, string> { { "key", "\"new\"" } });
            string body = File.ReadAllText(path);
            StringAssert.Contains(body, "key = \"new\"");
            Assert.IsFalse(body.Contains("\"old\""), "Old value should be gone.");
        }

        [TestMethod]
        public void TomlTableMerger_CreatesFileIfMissing()
        {
            string root = TempDir();
            string path = Path.Combine(root, "new.toml");
            TomlTableMerger.EnsureTable(path, "section",
                new Dictionary<string, string> { { "k", "1" } });
            Assert.IsTrue(File.Exists(path));
            StringAssert.Contains(File.ReadAllText(path), "[section]");
        }
    }
}
