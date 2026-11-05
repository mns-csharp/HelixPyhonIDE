using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class VsSolutionReaderTests
    {
        [TestMethod]
        public void ReadsSlnxWithMultipleProjectReferences()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "helix_slnx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                foreach (string name in new[] { "PythonApplication1", "PythonApplication2" })
                {
                    string projectDir = Path.Combine(tempDir, name);
                    Directory.CreateDirectory(projectDir);
                    File.WriteAllText(Path.Combine(projectDir, name + ".pyproj"), "<Project />");
                }
                string slnx = Path.Combine(tempDir, "sample.slnx");
                File.WriteAllText(slnx, "<Solution><Project Path=\"PythonApplication1/PythonApplication1.pyproj\" /><Project Path=\"PythonApplication2/PythonApplication2.pyproj\" /></Solution>");
                var refs = VsSolutionReader.Read(slnx);
                Assert.AreEqual(2, refs.Count);
                foreach (var r in refs)
                {
                    Assert.IsTrue(Path.IsPathRooted(r.PyProjAbsolutePath));
                    Assert.IsTrue(File.Exists(r.PyProjAbsolutePath));
                }
                CollectionAssert.AreEquivalent(new[] { "PythonApplication1", "PythonApplication2" }, refs.Select(r => r.Name).ToList());
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [TestMethod]
        public void ReadsLegacySlnWithPyProjReferences()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "helix_sln_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(tempDir, "Alpha"));
            Directory.CreateDirectory(Path.Combine(tempDir, "Beta"));
            File.WriteAllText(Path.Combine(tempDir, "Alpha", "Alpha.pyproj"), "<Project/>");
            File.WriteAllText(Path.Combine(tempDir, "Beta", "Beta.pyproj"), "<Project/>");

            string slnContent =
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "Project(\"{888888A0-9F3D-457C-B088-3A5042F75D52}\") = \"Alpha\", \"Alpha\\Alpha.pyproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
                "EndProject\n" +
                "Project(\"{888888A0-9F3D-457C-B088-3A5042F75D52}\") = \"Beta\", \"Beta\\Beta.pyproj\", \"{22222222-2222-2222-2222-222222222222}\"\n" +
                "EndProject\n";
            string slnPath = Path.Combine(tempDir, "Combined.sln");
            File.WriteAllText(slnPath, slnContent);

            var refs = VsSolutionReader.Read(slnPath);
            try
            {
                Assert.AreEqual(2, refs.Count);
                CollectionAssert.AreEquivalent(
                    new[] { "Alpha", "Beta" },
                    refs.Select(r => r.Name).ToList());
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [TestMethod]
        public void SkipsReferencesWithMissingFiles()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "helix_sln_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string slnx = Path.Combine(tempDir, "x.slnx");
            File.WriteAllText(slnx,
                "<Solution>\n" +
                "  <Project Path=\"does/not/exist.pyproj\" Type=\"888888a0-9f3d-457c-b088-3a5042f75d52\" />\n" +
                "</Solution>\n");
            try
            {
                var refs = VsSolutionReader.Read(slnx);
                Assert.AreEqual(0, refs.Count, "Orphan references should be dropped.");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
