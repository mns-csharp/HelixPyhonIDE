using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// "Remove from Solution" (VS parity, all languages): unregisters the project
    /// but KEEPS its files — the non-destructive sibling of "Delete from Disk".
    /// Python parent → dropped from the workspace members list; native CMake
    /// parent → the add_subdirectory line is removed (root-level targets refused).
    /// </summary>
    [TestClass]
    public class RemoveProjectFromSolutionTests
    {
        private static TreeOperationEventArgs Args(ProjectModel pm)
        {
            return new TreeOperationEventArgs(null, pm);
        }

        [TestMethod]
        public void PythonProject_IsRemovedFromMembers_ButItsFilesSurvive()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_rm_py_" + Guid.NewGuid().ToString("N"));
            string keepDir = Path.Combine(root, "keep");
            string dropDir = Path.Combine(root, "drop");
            Directory.CreateDirectory(keepDir);
            Directory.CreateDirectory(dropDir);
            string rootToml = Path.Combine(root, "pyproject.toml");
            File.WriteAllText(rootToml,
                "[project]\nname = \"ws\"\n\n[tool.uv.workspace]\nmembers = [\"keep\", \"drop\"]\n");
            File.WriteAllText(Path.Combine(keepDir, "pyproject.toml"), "[project]\nname = \"keep\"\n");
            File.WriteAllText(Path.Combine(dropDir, "pyproject.toml"), "[project]\nname = \"drop\"\n");
            File.WriteAllText(Path.Combine(dropDir, "precious.py"), "X = 1\n");

            var sln = new SolutionModel(rootToml, "ws");
            var keep = new ProjectModel(Path.Combine(keepDir, "pyproject.toml"), "keep", sln);
            var drop = new ProjectModel(Path.Combine(dropDir, "pyproject.toml"), "drop", sln);
            sln.Projects.Add(keep);
            sln.Projects.Add(drop);

            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                form._currentSolution = sln;
                form.OnTreeRemoveProjectFromSolution(this, Args(drop));

                Assert.IsFalse(sln.Projects.Contains(drop), "unregistered from the in-memory solution");
                string members = File.ReadAllText(rootToml);
                StringAssert.Contains(members, "keep");
                Assert.IsFalse(members.Contains("drop"), "dropped from the workspace members list");
                Assert.IsTrue(File.Exists(Path.Combine(dropDir, "precious.py")),
                    "REMOVE keeps every file on disk — that is the whole point");
                form.Close();
            }
        }

        [TestMethod]
        public void NativeProject_RemovalDropsTheAddSubdirectoryLine_FilesSurvive()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_rm_cm_" + Guid.NewGuid().ToString("N"));
            string libDir = Path.Combine(root, "cpp_lib");
            Directory.CreateDirectory(libDir);
            string rootCmake = Path.Combine(root, "CMakeLists.txt");
            File.WriteAllText(rootCmake,
                "cmake_minimum_required(VERSION 3.18)\nproject(GpuApp LANGUAGES CUDA CXX)\n" +
                "add_executable(GpuApp main.cu)\nadd_subdirectory(\"cpp_lib\")\n");
            File.WriteAllText(Path.Combine(libDir, "CMakeLists.txt"),
                "project(cpp_lib LANGUAGES CXX)\nadd_library(cpp_lib STATIC lib.cpp)\n");
            File.WriteAllText(Path.Combine(libDir, "lib.cpp"), "int answer() { return 42; }\n");

            Assert.IsTrue(MainForm.TryRemoveAddSubdirectory(rootCmake, libDir));
            string updated = File.ReadAllText(rootCmake);
            Assert.IsFalse(updated.Contains("add_subdirectory"), "the reference line is gone");
            StringAssert.Contains(updated, "add_executable(GpuApp main.cu)", "everything else untouched");
            Assert.IsTrue(File.Exists(Path.Combine(libDir, "lib.cpp")), "files kept on disk");

            Assert.IsFalse(MainForm.TryRemoveAddSubdirectory(rootCmake, libDir),
                "no line left to remove — reports false instead of rewriting the file");
        }

        [TestMethod]
        public void NativeRootLevelTarget_HasNoSubdirectoryLine_AndIsReportedAsSuch()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_rm_root_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string rootCmake = Path.Combine(root, "CMakeLists.txt");
            const string original =
                "cmake_minimum_required(VERSION 3.18)\nproject(GpuApp LANGUAGES CUDA CXX)\nadd_executable(GpuApp main.cu)\n";
            File.WriteAllText(rootCmake, original);

            // The root target's "directory" is the root itself — outside the subdirectory scheme.
            Assert.IsFalse(MainForm.TryRemoveAddSubdirectory(rootCmake, root));
            Assert.AreEqual(original, File.ReadAllText(rootCmake), "file untouched");
        }
    }
}
