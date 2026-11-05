using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CMakeProjectImporterRecursiveTests
    {
        [TestMethod]
        public void ImportsTargetsAndSourcesFromAddSubdirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-cmake-" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            try
            {
                File.WriteAllText(Path.Combine(root, "CMakeLists.txt"),
                    "cmake_minimum_required(VERSION 3.20)\nproject(Orbit LANGUAGES CXX CUDA)\nadd_subdirectory(src)\n");
                File.WriteAllText(Path.Combine(src, "CMakeLists.txt"), "add_executable(orbit main.cu helper.cpp)\n");
                File.WriteAllText(Path.Combine(src, "main.cu"), "__global__ void orbit() {}\n");
                File.WriteAllText(Path.Combine(src, "helper.cpp"), "void helper() {}\n");

                CMakeImportResult result = new CMakeProjectImporter().Import(root);
                Assert.IsTrue(result.Success, result.Error);
                Assert.AreEqual(ProjectKind.CudaExecutable, result.Project.Kind);
                Assert.AreEqual(1, result.Project.Targets.Count);
                Assert.AreEqual(2, result.Project.Targets[0].SourceFiles.Count);
                Assert.AreEqual(0, result.Project.Targets[0].MissingSources.Count);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void DynamicCMakeStillExposesEveryNativeSourceAsUnassigned()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-cmake-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "CMakeLists.txt"),
                    "project(Dynamic LANGUAGES CXX CUDA)\nfile(GLOB SOURCES src/*.cpp src/*.cu)\nadd_library(${TARGET_NAME} ${SOURCES})\n");
                Directory.CreateDirectory(Path.Combine(root, "src"));
                File.WriteAllText(Path.Combine(root, "src", "a.cpp"), "void a() {}\n");
                File.WriteAllText(Path.Combine(root, "src", "b.cu"), "__global__ void b() {}\n");

                CMakeImportResult result = new CMakeProjectImporter().Import(root);
                Assert.IsTrue(result.Success, result.Error);
                CMakeResolvedTarget unassigned = result.Project.Targets.Find(t => t.Name == "Unassigned Sources");
                Assert.IsNotNull(unassigned);
                Assert.AreEqual(2, unassigned.SourceFiles.Count);
                Assert.IsTrue(result.Diagnostics.Exists(x => x.Contains("without executing CMake")));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void RefusesSubdirectoryOutsideSolutionRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-cmake-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "project(Safe)\nadd_subdirectory(../outside)\n");
                CMakeImportResult result = new CMakeProjectImporter().Import(root);
                Assert.IsTrue(result.Success);
                Assert.AreEqual(0, result.Project.Targets.Count);
                Assert.IsTrue(result.Diagnostics.Exists(x => x.Contains("outside the project root")));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
