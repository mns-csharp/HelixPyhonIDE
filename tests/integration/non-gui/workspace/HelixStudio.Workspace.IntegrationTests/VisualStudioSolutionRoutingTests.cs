using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.LanguageServices;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// A `.sln` is a container, not a language. The extension is registered to Python (this IDE
    /// writes PTVS solutions), so the loader must decide by what the solution CONTAINS — otherwise
    /// opening an ordinary Visual Studio solution fails with "contains no .pyproj references",
    /// which is what a user sees as "none of my solutions load".
    /// </summary>
    [TestClass]
    public class VisualStudioSolutionRoutingTests
    {
        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "helix_slnroute_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private string WriteSln(string name, params string[] projectRelPaths)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            int i = 0;
            foreach (string rel in projectRelPaths)
            {
                sb.AppendLine("Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \""
                    + Path.GetFileNameWithoutExtension(rel) + "\", \"" + rel
                    + "\", \"{00000000-0000-0000-0000-00000000000" + (i++) + "}\"");
                sb.AppendLine("EndProject");
            }
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        private string WriteCsproj(string relDir, string name, string outputType, params string[] sources)
        {
            string dir = Path.Combine(_dir, relDir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
                "    <OutputType>" + outputType + "</OutputType>\n" +
                "    <TargetFramework>net472</TargetFramework>\n" +
                "  </PropertyGroup>\n</Project>\n");
            foreach (string s in sources)
                File.WriteAllText(Path.Combine(dir, s), "namespace N { public class C { } }\n");
            return relDir + "\\" + name + ".csproj";
        }

        [TestMethod]
        public void SlnOfCsprojs_IsRefusedByName_NotBlamedOnMissingPyproj()
        {
            // HelixPython does not open C# solutions — HelixCSharp does. What matters is that the
            // refusal names what the .sln actually holds and where to open it, instead of the
            // pre-split "contains no .pyproj references", which reads as "your solution is broken".
            string app = WriteCsproj("App", "App", "Exe", "Program.cs");
            string lib = WriteCsproj("Lib", "Lib", "Library", "Widget.cs");
            string sln = WriteSln("Demo.sln", app, lib);

            SolutionLoadResult result = new SolutionService().LoadAsync(sln).GetAwaiter().GetResult();

            Assert.IsFalse(result.Success, "a C#-only .sln is not a HelixPython solution");
            StringAssert.Contains(result.ErrorMessage, "C# project",
                "the refusal must name what the solution holds");
            StringAssert.Contains(result.ErrorMessage, "HelixCSharp",
                "the refusal must say which product does open it");
        }

        [TestMethod]
        public void SlnOfCsprojs_IsRefusedThroughTheRegistryToo_NotOnlyTheDirectLoad()
        {
            // The path a user actually takes: File ▸ Open Solution goes through the registry. The
            // refusal must survive that route too — routing a .sln somewhere that opens it as
            // Python is the failure this guards.
            string app = WriteCsproj("App", "App", "Exe", "Program.cs");
            string sln = WriteSln("Reg.sln", app);

            ILanguageWorkspaceProvider provider = LanguageWorkspaceRegistry.TryDetect(sln);
            if (provider == null) return;   // no provider claims it: refused before it starts

            WorkspaceOpenResult opened = provider.OpenAsync(sln).GetAwaiter().GetResult();
            Assert.IsFalse(opened.Success, "a C#-only .sln must not open here: " + opened.ErrorMessage);
        }

        [TestMethod]
        public void SolutionListingOnlyProjectKindsWeCannotOpen_SaysSoConcretely()
        {
            string dir = Path.Combine(_dir, "Native");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Native.vcxproj"), "<Project/>");
            string sln = WriteSln("Native.sln", "Native\\Native.vcxproj");

            SolutionLoadResult result = new SolutionService().LoadAsync(sln).GetAwaiter().GetResult();

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, ".vcxproj",
                "the message must name what the solution actually holds, not blame missing .pyproj files");
        }

        [TestMethod]
        public void SolutionFoldersAreNotMistakenForProjects()
        {
            // Driven with a .pyproj, the kind of solution HelixPython actually opens: a solution
            // FOLDER uses the same Project(...) syntax but its "path" is a display name with no
            // file behind it, and counting it as a project puts a phantom node in the tree.
            string app = WritePyproj("App", "App");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sb.AppendLine("Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Solution Items\", \"Solution Items\", \"{11111111-1111-1111-1111-111111111111}\"");
            sb.AppendLine("EndProject");
            sb.AppendLine("Project(\"{888888F0-9F3C-457C-B88F-3E11021DDE28}\") = \"App\", \"" + app + "\", \"{22222222-2222-2222-2222-222222222222}\"");
            sb.AppendLine("EndProject");
            string sln = Path.Combine(_dir, "Folders.sln");
            File.WriteAllText(sln, sb.ToString());

            SolutionLoadResult result = new SolutionService().LoadAsync(sln).GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(1, result.Solution.Projects.Count, "a solution folder is not a project");
        }

        private string WritePyproj(string relDir, string name)
        {
            string dir = Path.Combine(_dir, relDir);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".pyproj");
            File.WriteAllText(path,
                "<Project ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
                "  <PropertyGroup>\n    <Name>" + name + "</Name>\n    <ProjectHome>.</ProjectHome>\n" +
                "  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(dir, "module.py"), "class Widget:\n    pass\n");
            return path;
        }
    }
}
